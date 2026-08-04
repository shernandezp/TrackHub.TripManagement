// Copyright (c) 2026 Sergio Hernandez. All rights reserved.
//
//  Licensed under the Apache License, Version 2.0 (the "License").
//  You may not use this file except in compliance with the License.
//  You may obtain a copy of the License at
//
//      http://www.apache.org/licenses/LICENSE-2.0
//
//  Unless required by applicable law or agreed to in writing, software
//  distributed under the License is distributed on an "AS IS" BASIS,
//  WITHOUT WARRANTIES OR CONDITIONS OF ANY KIND, either express or implied.
//  See the License for the specific language governing permissions and
//  limitations under the License.
//

using Common.Application.Interfaces;
using Common.Domain.Enums;
using Microsoft.EntityFrameworkCore;
using Moq;
using TrackHub.TripManagement.Domain.Constants;
using TrackHub.TripManagement.Domain.Records;
using TrackHub.TripManagement.Infrastructure.TripDB.Writers;

namespace Infrastructure.UnitTests;

/// <summary>
/// Two writes to the SAME row inside ONE request, which is what every automatic path in this module
/// actually is.
/// <para>
/// The context is registered <c>NoTracking</c>, so each writer call used to re-query its row, get a
/// second instance of it, and <c>Attach</c> that — which throws once the change tracker already
/// holds the row. Nothing caught it: the writer fixtures ran under <c>TrackAll</c>, where the query
/// returns the tracked instance and the attach is a no-op, and the detection fixtures mock the
/// writers outright. In production the whole zero-touch lifecycle failed on its second write, and
/// Router's best-effort try/catch swallowed it, so it presented as "automation does nothing".
/// </para>
/// <para>
/// Every case below is a real call sequence, named for the pipeline step that produces it. They are
/// deliberately assertion-light: what is under test is that the SECOND write happens at all.
/// </para>
/// </summary>
[TestFixture]
public class MultiWriteRequestTests
{
    private static readonly Guid TripId = Guid.Parse("33333333-3333-3333-3333-333333333333");
    private static readonly Guid StopId = Guid.Parse("44444444-4444-4444-4444-444444444444");
    private static readonly DateTimeOffset Fix = new(2026, 8, 3, 7, 42, 0, TimeSpan.Zero);

    private static IUser User()
    {
        var user = new Mock<IUser>();
        user.SetupGet(u => u.PrincipalType).Returns(PrincipalType.User);
        user.SetupGet(u => u.UserId).Returns(Guid.Parse("99999999-9999-9999-9999-999999999999"));
        return user.Object;
    }

    private static async Task<WriterTestContext> SeededAsync(string status, bool withStop = false)
    {
        var context = WriterTestContext.Create();
        var trip = WriterTestData.Trip(TripId, "TRIP-MW");
        trip.Status = status;
        context.Trips.Add(trip);

        if (withStop)
        {
            context.TripStops.Add(WriterTestData.Stop(StopId, TripId, TripStopStatuses.Pending));
        }

        await context.SaveChangesAsync(CancellationToken.None);

        // The request boundary: a real request starts with an empty tracker and reads its rows back
        // out of the database, so seeding must not leave the fixture holding tracked instances.
        context.ChangeTracker.Clear();
        return context;
    }

    [Test]
    public async Task Detection_ArmsThenStampsTheOriginVisit()
    {
        using var context = await SeededAsync(TripStatuses.Created);
        var writer = new TripWriter(context, User());

        Assert.That(await writer.ArmTripAsync(TripId, WriterTestData.AccountId, CancellationToken.None), Is.True);
        await writer.SetOriginVisitAsync(TripId, WriterTestData.AccountId, Fix, null, CancellationToken.None);

        var trip = await context.Trips.AsNoTracking().FirstAsync(t => t.TripId == TripId, CancellationToken.None);
        Assert.Multiple(() =>
        {
            Assert.That(trip.ArmedAt, Is.Not.Null);
            Assert.That(trip.OriginArrivedAt, Is.EqualTo(Fix));
        });
    }

    [Test]
    public async Task Detection_ArmsThenStartsOnTheSameFix()
    {
        using var context = await SeededAsync(TripStatuses.Created);
        var writer = new TripWriter(context, User());

        await writer.ArmTripAsync(TripId, WriterTestData.AccountId, CancellationToken.None);
        await writer.SetOriginVisitAsync(TripId, WriterTestData.AccountId, Fix, null, CancellationToken.None);
        var started = await writer.TransitionTripAsync(
            TripId, WriterTestData.AccountId, TripStatuses.InProgress, TripEventTypes.TripStarted,
            TripEventSources.Detection, $"trip-start:{TripId:N}", null, null, false, Fix, CancellationToken.None);

        var trip = await context.Trips.AsNoTracking().FirstAsync(t => t.TripId == TripId, CancellationToken.None);
        Assert.Multiple(() =>
        {
            Assert.That(started, Is.True);
            Assert.That(trip.Status, Is.EqualTo(TripStatuses.InProgress));

            // The measured start, not the server clock — the whole point of stamping the visit first.
            Assert.That(trip.ActualStartAt, Is.EqualTo(Fix));
        });
    }

    [Test]
    public async Task Detection_MovesTheOdometerThenTheOriginDebounceClock()
    {
        using var context = await SeededAsync(TripStatuses.InProgress);
        var unit = await WriterTestData.LoadedUnitAsync(context);

        unit.TryAdvanceProgress(TripId, 4.6, -74.0, Fix, 120);
        unit.SetOriginOutsideSince(TripId, Fix);
        await unit.FlushAsync(CancellationToken.None);

        var trip = await context.Trips.AsNoTracking().FirstAsync(t => t.TripId == TripId, CancellationToken.None);
        Assert.That(trip.OriginOutsideSinceAt, Is.EqualTo(Fix));
    }

    [Test]
    public async Task Detection_MovesTheOdometerThenTheDeviationRunLength()
    {
        using var context = await SeededAsync(TripStatuses.InProgress);
        var unit = await WriterTestData.LoadedUnitAsync(context);

        unit.TryAdvanceProgress(TripId, 4.6, -74.0, Fix, 120);
        unit.SetDeviationState(TripId, null, 1);
        await unit.FlushAsync(CancellationToken.None);

        var trip = await context.Trips.AsNoTracking().FirstAsync(t => t.TripId == TripId, CancellationToken.None);
        Assert.That(trip.ConsecutiveOutsideFixes, Is.EqualTo(1));
    }

    [Test]
    public async Task Detection_MovesTheOdometerThenAutoCompletes()
    {
        using var context = await SeededAsync(TripStatuses.InProgress);
        var writer = new TripWriter(context, User());
        var unit = await WriterTestData.LoadedUnitAsync(context);

        unit.TryAdvanceProgress(TripId, 4.6, -74.0, Fix, 120);
        var completed = await writer.TransitionTripAsync(
            TripId, WriterTestData.AccountId, TripStatuses.Completed, TripEventTypes.TripCompleted,
            TripEventSources.Detection, $"trip-complete:{TripId:N}", null, null, true, Fix, CancellationToken.None);

        Assert.That(completed, Is.True);
    }

    [Test]
    public async Task Detection_AcceptsSeveralFixesForOneTransporterInOneBatch()
    {
        using var context = await SeededAsync(TripStatuses.InProgress);
        var unit = await WriterTestData.LoadedUnitAsync(context);

        unit.TryAdvanceProgress(TripId, 4.60, -74.0, Fix, 0);
        unit.TryAdvanceProgress(TripId, 4.61, -74.0, Fix.AddMinutes(1), 1100);
        await unit.FlushAsync(CancellationToken.None);

        var trip = await context.Trips.AsNoTracking().FirstAsync(t => t.TripId == TripId, CancellationToken.None);
        Assert.Multiple(() =>
        {
            Assert.That(trip.LastPositionAt, Is.EqualTo(Fix.AddMinutes(1)));
            Assert.That(trip.ActualDistanceMeters, Is.EqualTo(1100));
        });
    }

    [Test]
    public async Task EtaSweep_UpdatesTheEtaThenStampsTheDelayOnTheSameStop()
    {
        using var context = await SeededAsync(TripStatuses.InProgress, withStop: true);
        var writer = new TripStopWriter(context);

        await writer.UpdateStopEtaAsync(StopId, WriterTestData.AccountId, Fix, EtaSources.Ors, CancellationToken.None);
        await writer.MarkStopDelayAlertedAsync(StopId, WriterTestData.AccountId, Fix, CancellationToken.None);

        var stop = await context.TripStops.AsNoTracking().FirstAsync(s => s.TripStopId == StopId, CancellationToken.None);
        Assert.Multiple(() =>
        {
            Assert.That(stop.EtaAt, Is.EqualTo(Fix));
            Assert.That(stop.DelayAlertedAt, Is.EqualTo(Fix));
        });
    }

    [Test]
    public async Task Detection_RecordsAnArrivalThenTheDepartureDebounceOnTheSameStop()
    {
        using var context = await SeededAsync(TripStatuses.InProgress, withStop: true);
        var writer = new TripStopWriter(context);

        var arrived = await writer.RecordStopProgressAsync(
            TripId, StopId, WriterTestData.AccountId, TripStopStatuses.Arrived, Fix, 4.7, -74.0,
            TripEventSources.Detection, $"trip-arrive:{StopId:N}", null, CancellationToken.None);

        await writer.SetStopOutsideSinceAsync(StopId, WriterTestData.AccountId, Fix.AddMinutes(20), CancellationToken.None);

        var stop = await context.TripStops.AsNoTracking().FirstAsync(s => s.TripStopId == StopId, CancellationToken.None);
        Assert.Multiple(() =>
        {
            Assert.That(arrived, Is.True);
            Assert.That(stop.Status, Is.EqualTo(TripStopStatuses.Arrived));
            Assert.That(stop.OutsideSinceAt, Is.EqualTo(Fix.AddMinutes(20)));
        });
    }

    [Test]
    public async Task Backfill_ArmsThenReplaysAStopBeforeTheTripIsEvenRunning()
    {
        using var context = await SeededAsync(TripStatuses.Created, withStop: true);
        var tripWriter = new TripWriter(context, User());
        var stopWriter = new TripStopWriter(context);

        await tripWriter.ArmTripAsync(TripId, WriterTestData.AccountId, CancellationToken.None);
        await tripWriter.SetOriginVisitAsync(TripId, WriterTestData.AccountId, Fix, Fix.AddMinutes(30), CancellationToken.None);

        var replayed = await stopWriter.RecordStopProgressAsync(
            TripId, StopId, WriterTestData.AccountId, TripStopStatuses.Arrived, Fix.AddHours(2), null, null,
            TripEventSources.Detection, $"trip-arrive:{StopId:N}", null, CancellationToken.None);

        var started = await tripWriter.TransitionTripAsync(
            TripId, WriterTestData.AccountId, TripStatuses.InProgress, TripEventTypes.TripStarted,
            TripEventSources.Detection, $"trip-start:{TripId:N}", null, null, false, Fix, CancellationToken.None);

        Assert.Multiple(() =>
        {
            Assert.That(replayed, Is.True);
            Assert.That(started, Is.True);
        });
    }

    /// <summary>
    /// The CSV and partner importers CREATE a trip and then declare it in transit inside one
    /// request, so the row is already tracked from its own insert when the backfill reaches it.
    /// That is a different entry into the same hazard as the seeded cases above — there is no
    /// preceding query, the tracker is populated by <c>Add</c> — so it gets its own case.
    /// </summary>
    [Test]
    public async Task Import_CreatesATripAndThenDeclaresItInTransitInTheSameRequest()
    {
        using var context = WriterTestContext.Create();
        var writer = new TripWriter(context, User());

        var created = await writer.CreateTripAsync(
            new TripDto("TRIP-CSV", Guid.Parse("55555555-5555-5555-5555-555555555555"), null, null, "EXT-1", "Acme",
                "Depot", 4.65, -74.05, null, TripGeometry.DefaultRadiusMeters,
                Fix.AddHours(1), null, null, null),
            WriterTestData.AccountId,
            CancellationToken.None);

        await writer.ArmTripAsync(created.TripId, WriterTestData.AccountId, CancellationToken.None);
        await writer.SetOriginVisitAsync(created.TripId, WriterTestData.AccountId, null, Fix, CancellationToken.None);
        var started = await writer.TransitionTripAsync(
            created.TripId, WriterTestData.AccountId, TripStatuses.InProgress, TripEventTypes.TripStarted,
            TripEventSources.Portal, $"trip-start:{created.TripId:N}", null, null, false, Fix, CancellationToken.None);

        var trip = await context.Trips.AsNoTracking().FirstAsync(t => t.TripId == created.TripId, CancellationToken.None);
        Assert.Multiple(() =>
        {
            Assert.That(started, Is.True);
            Assert.That(trip.Status, Is.EqualTo(TripStatuses.InProgress));
            Assert.That(trip.OriginDepartedAt, Is.EqualTo(Fix));

            // No recorded arrival, so loading stays unmeasured rather than invented (§5.4).
            Assert.That(trip.OriginArrivedAt, Is.Null);
            Assert.That(trip.ActualStartAt, Is.EqualTo(Fix));
        });
    }

    /// <summary>
    /// A start rejected because the unit is already running something else must leave the route's
    /// arrival geometry untouched too — the transition snapshots it BEFORE it saves, so the rejected
    /// attempt otherwise leaves every stop Modified in a request-scoped tracker and the next save in
    /// the batch arms the stops of a trip that never started.
    /// </summary>
    /// <summary>
    /// A rejected transition must undo ITSELF and nothing else — the two halves of the same claim.
    /// <para>
    /// The trip row is shared: the unit of work has this fix's odometer buffered on it while the
    /// transition is trying to start the trip. Undoing a losing start by DETACHING that row, which is
    /// what the writer used to do, silently discarded a measurement that had nothing to do with the
    /// start. Undoing too little is just as bad in the other direction: a start that never happened
    /// must not leave the route armed or the status moved.
    /// </para>
    /// <para>
    /// The odometer surviving is the load-bearing assertion. Break the revert into a detach and this
    /// is the test that fails.
    /// </para>
    /// </summary>
    [Test]
    public async Task ARejectedStart_UndoesItselfButKeepsTheMeasurementsBufferedAlongsideIt()
    {
        using var context = await SeededAsync(TripStatuses.Created, withStop: true);
        var writer = new TripWriter(context, User());
        var unit = await WriterTestData.LoadedUnitAsync(context, armableUntil: Fix.AddYears(1));

        // The same fix that is trying to start the trip also moved the odometer.
        unit.TryAdvanceProgress(TripId, 4.6, -74.0, Fix, 250);

        context.FailNextSaveOn("ux_trips_transporterid_inprogress");

        Assert.ThrowsAsync<Common.Application.Exceptions.ConflictException>(async () =>
            await writer.TransitionTripAsync(
                TripId, WriterTestData.AccountId, TripStatuses.InProgress, TripEventTypes.TripStarted,
                TripEventSources.Detection, $"trip-start:{TripId:N}", null, null, false, Fix, CancellationToken.None));

        await unit.FlushAsync(CancellationToken.None);

        var stop = await context.TripStops.AsNoTracking().FirstAsync(s => s.TripStopId == StopId, CancellationToken.None);
        var trip = await context.Trips.AsNoTracking().FirstAsync(t => t.TripId == TripId, CancellationToken.None);
        Assert.Multiple(() =>
        {
            // Undone: the start did not happen.
            Assert.That(trip.Status, Is.EqualTo(TripStatuses.Created));
            Assert.That(trip.ActualStartAt, Is.Null);
            Assert.That(stop.ArrivalGeom, Is.Null, "the rejected start's snapshot leaked into a later save");
            Assert.That(context.TripEvents.Count(), Is.Zero, "the rejected event must not resurface");
            Assert.That(context.AuditEvents.Count(), Is.Zero, "nor an audit row for something that never happened");

            // Kept: the measurement was never the transition's to discard.
            Assert.That(trip.ActualDistanceMeters, Is.EqualTo(250d), "a losing transition threw away the odometer buffered beside it");
            Assert.That(trip.LastPositionAt, Is.EqualTo(Fix));
        });
    }
}
