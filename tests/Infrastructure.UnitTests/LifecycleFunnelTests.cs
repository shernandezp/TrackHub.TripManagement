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

using Common.Application.Exceptions;
using Common.Application.Interfaces;
using Common.Domain.Enums;
using Microsoft.EntityFrameworkCore;
using Moq;
using TrackHub.TripManagement.Domain.Constants;
using TrackHub.TripManagement.Domain.Records;
using TrackHub.TripManagement.Infrastructure.TripDB.Writers;

namespace Infrastructure.UnitTests;

/// <summary>
/// The rebuilt lifecycle funnel (spec 11a §12): status, timestamps and the timeline event are ONE
/// save, so a duplicate or a racing transition writes nothing at all.
/// <para>
/// These run against a context that answers with a genuine PostgreSQL 23505, because the failure
/// this guards against is invisible to a stub: catching the violation without detaching leaves the
/// dead insert AND the status mutation in a request-scoped change tracker, and the next save on the
/// same request replays them.
/// </para>
/// </summary>
[TestFixture]
public class LifecycleFunnelTests
{
    private static readonly Guid TripId = Guid.Parse("22222222-2222-2222-2222-222222222222");
    private static readonly DateTimeOffset Measured = new(2026, 8, 3, 7, 42, 0, TimeSpan.Zero);

    private static Mock<IUser> User()
    {
        var user = new Mock<IUser>();
        user.SetupGet(u => u.PrincipalType).Returns(PrincipalType.User);
        user.SetupGet(u => u.UserId).Returns(Guid.Parse("99999999-9999-9999-9999-999999999999"));
        return user;
    }

    private static async Task<WriterTestContext> SeededAsync(string status = TripStatuses.Created)
    {
        var context = WriterTestContext.Create();
        var trip = WriterTestData.Trip(TripId, "TRIP-001");
        trip.Status = status;
        context.Trips.Add(trip);
        await context.SaveChangesAsync(CancellationToken.None);
        return context;
    }

    [Test]
    public async Task AStart_WritesTheStatusAndTheTimelineEventTogether()
    {
        using var context = await SeededAsync();
        var writer = new TripWriter(context, User().Object);

        var applied = await writer.TransitionTripAsync(
            TripId, WriterTestData.AccountId, TripStatuses.InProgress, TripEventTypes.TripStarted,
            TripEventSources.Detection, $"trip-start:{TripId:N}", null, null, false, Measured, CancellationToken.None);

        var trip = await context.Trips.FirstAsync(t => t.TripId == TripId, CancellationToken.None);

        Assert.Multiple(() =>
        {
            Assert.That(applied, Is.True);
            Assert.That(trip.Status, Is.EqualTo(TripStatuses.InProgress));

            // The MEASURED instant, never the server clock: an automatic start is a fact about the
            // vehicle, not about when the server got round to noticing (§12.3).
            Assert.That(trip.ActualStartAt, Is.EqualTo(Measured));
            Assert.That(context.TripEvents.Count(), Is.EqualTo(1));
        });
    }

    [Test]
    public async Task AStartWhoseOriginArrivalWasMeasured_AdoptsThatArrivalAsTheActualStart()
    {
        using var context = await SeededAsync();
        var writer = new TripWriter(context, User().Object);

        await writer.SetOriginVisitAsync(
            TripId, WriterTestData.AccountId, Measured, null, CancellationToken.None);

        await writer.TransitionTripAsync(
            TripId, WriterTestData.AccountId, TripStatuses.InProgress, TripEventTypes.TripStarted,
            TripEventSources.Detection, $"trip-start:{TripId:N}", null, null, false,
            Measured.AddMinutes(5), CancellationToken.None);

        var trip = await context.Trips.FirstAsync(t => t.TripId == TripId, CancellationToken.None);

        Assert.That(trip.ActualStartAt, Is.EqualTo(Measured), "ActualStartAt is the origin arrival, not the fix that noticed it");
    }

    /// <summary>
    /// The race the shared idempotency key exists for: a dispatcher's Start and an auto-start on the
    /// same trip. Exactly one lands, and the loser leaves NOTHING behind — not the event, not the
    /// status change, not the audit row.
    /// </summary>
    [Test]
    public async Task ADuplicateTransition_WritesNothingAndReportsIt()
    {
        using var context = await SeededAsync();
        var writer = new TripWriter(context, User().Object);
        context.FailNextSaveOn("ux_trip_events_idempotencykey");

        var applied = await writer.TransitionTripAsync(
            TripId, WriterTestData.AccountId, TripStatuses.InProgress, TripEventTypes.TripStarted,
            TripEventSources.Detection, $"trip-start:{TripId:N}", null, null, false, Measured, CancellationToken.None);

        Assert.That(applied, Is.False);

        // A later genuine write on the SAME request must not replay the rejected insert — the context
        // is request-scoped, and anything left tracked comes back.
        await writer.SetOriginVisitAsync(TripId, WriterTestData.AccountId, Measured, null, CancellationToken.None);

        Assert.Multiple(() =>
        {
            Assert.That(context.TripEvents.Count(), Is.Zero, "the rejected event must not resurface on a later save");
            Assert.That(context.AuditEvents.Count(), Is.Zero, "nor may it leave an audit row for something that never happened");
        });
    }

    [Test]
    public async Task AReplayedTransition_IsRecognisedBeforeTheMatrixGuardRuns()
    {
        // Idempotency is checked FIRST. Running the matrix guard first answered
        // TRIP_INVALID_TRANSITION to a replay of a transition the server itself had already applied.
        using var context = await SeededAsync();
        var writer = new TripWriter(context, User().Object);

        await writer.TransitionTripAsync(
            TripId, WriterTestData.AccountId, TripStatuses.InProgress, TripEventTypes.TripStarted,
            TripEventSources.Portal, $"trip-start:{TripId:N}", null, null, false, Measured, CancellationToken.None);

        var replay = await writer.TransitionTripAsync(
            TripId, WriterTestData.AccountId, TripStatuses.InProgress, TripEventTypes.TripStarted,
            TripEventSources.Detection, $"trip-start:{TripId:N}", null, null, false, Measured, CancellationToken.None);

        Assert.Multiple(() =>
        {
            Assert.That(replay, Is.False);
            Assert.That(context.TripEvents.Count(), Is.EqualTo(1));
        });
    }

    [Test]
    public async Task StartingATripOnABusyVehicle_IsABusyConflictAndLeavesNoTrace()
    {
        using var context = await SeededAsync();
        var writer = new TripWriter(context, User().Object);
        context.FailNextSaveOn("ux_trips_transporterid_inprogress");

        var ex = Assert.ThrowsAsync<ConflictException>(async () => await writer.TransitionTripAsync(
            TripId, WriterTestData.AccountId, TripStatuses.InProgress, TripEventTypes.TripStarted,
            TripEventSources.Portal, $"trip-start:{TripId:N}", null, null, false, Measured, CancellationToken.None));

        Assert.Multiple(() =>
        {
            Assert.That(ex!.Message, Does.Contain(TripErrorCodes.TransporterBusy));
            Assert.That(context.TripEvents.Count(), Is.Zero);
        });
    }

    [Test]
    public async Task ArmingIsIdempotentAndLeavesNoHistory()
    {
        using var context = await SeededAsync();
        var writer = new TripWriter(context, User().Object);

        var first = await writer.ArmTripAsync(TripId, WriterTestData.AccountId, CancellationToken.None);
        var second = await writer.ArmTripAsync(TripId, WriterTestData.AccountId, CancellationToken.None);

        var trip = await context.Trips.FirstAsync(t => t.TripId == TripId, CancellationToken.None);

        Assert.Multiple(() =>
        {
            Assert.That(first, Is.True);
            Assert.That(second, Is.False);
            Assert.That(trip.ArmedAt, Is.Not.Null);
            Assert.That(trip.OriginGeom, Is.Not.Null, "the origin zone is the whole point of arming");

            // No event, so the trip stays deletable: an armed trip that never ran orphans nothing
            // (acceptance 16).
            Assert.That(context.TripEvents.Count(), Is.Zero);
        });
    }

    [Test]
    public async Task ADeleteStillSucceeds_AfterATripHasBeenArmed()
    {
        // The consequence of arming writing no event, stated as the behaviour a dispatcher sees.
        using var context = await SeededAsync();
        var writer = new TripWriter(context, User().Object);

        await writer.ArmTripAsync(TripId, WriterTestData.AccountId, CancellationToken.None);

        Assert.DoesNotThrowAsync(() => writer.DeleteTripAsync(TripId, WriterTestData.AccountId, CancellationToken.None));
    }

    [Test]
    public async Task TheMeasuredOriginVisit_IsNeverOverwrittenByAReplay()
    {
        using var context = await SeededAsync();
        var writer = new TripWriter(context, User().Object);

        await writer.SetOriginVisitAsync(TripId, WriterTestData.AccountId, Measured, null, CancellationToken.None);
        await writer.SetOriginVisitAsync(
            TripId, WriterTestData.AccountId, Measured.AddHours(2), null, CancellationToken.None);

        var trip = await context.Trips.FirstAsync(t => t.TripId == TripId, CancellationToken.None);

        Assert.That(trip.OriginArrivedAt, Is.EqualTo(Measured), "first write wins (acceptance 12)");
    }

    [Test]
    public async Task TheOriginDeparture_IsStampedOnceAndClearsTheDebounceClock()
    {
        using var context = await SeededAsync(TripStatuses.InProgress);
        var unit = await WriterTestData.LoadedUnitAsync(context);

        unit.SetOriginOutsideSince(TripId, Measured);
        var first = unit.TryRecordOriginDeparture(TripId, Measured.AddSeconds(31));
        var second = unit.TryRecordOriginDeparture(TripId, Measured.AddHours(1));
        await unit.FlushAsync(CancellationToken.None);

        var trip = await context.Trips.AsNoTracking().FirstAsync(t => t.TripId == TripId, CancellationToken.None);

        Assert.Multiple(() =>
        {
            Assert.That(first, Is.True);
            Assert.That(second, Is.False);
            Assert.That(trip.OriginDepartedAt, Is.EqualTo(Measured.AddSeconds(31)));
            Assert.That(trip.OriginOutsideSinceAt, Is.Null);
        });
    }

    /// <summary>
    /// Re-pointing a trip detection is already watching would change the meaning of a measurement in
    /// flight (§12.4). While it is still only planned, the edit is allowed and DISARMS it, so the
    /// next cycle re-arms against the new plan rather than the old geometry.
    /// </summary>
    [Test]
    public async Task EditingAnArmedButUnstartedTrip_DisarmsItSoItRearmsAgainstTheNewPlan()
    {
        using var context = await SeededAsync();
        var writer = new TripWriter(context, User().Object);
        await writer.ArmTripAsync(TripId, WriterTestData.AccountId, CancellationToken.None);

        await writer.UpdateTripAsync(TripId, Dto(originLatitude: 5.10), WriterTestData.AccountId, CancellationToken.None);

        var trip = await context.Trips.FirstAsync(t => t.TripId == TripId, CancellationToken.None);

        Assert.Multiple(() =>
        {
            Assert.That(trip.ArmedAt, Is.Null);
            Assert.That(trip.OriginGeom, Is.Null);
        });
    }

    [Test]
    public async Task RePointingARunningTrip_IsRejected()
    {
        using var context = await SeededAsync(TripStatuses.InProgress);
        var writer = new TripWriter(context, User().Object);

        var ex = Assert.ThrowsAsync<ConflictException>(async () => await writer.UpdateTripAsync(
            TripId, Dto(originLatitude: 5.10), WriterTestData.AccountId, CancellationToken.None));

        Assert.That(ex!.Message, Does.Contain(TripErrorCodes.TripArmed));
    }

    [Test]
    public async Task EditingSomethingElseOnARunningTrip_IsStillAllowed()
    {
        // The guard is about WHAT detection measures against, not about freezing the row: a customer
        // name or a note has nothing to do with the arm/auto-start decision.
        using var context = await SeededAsync(TripStatuses.InProgress);
        var writer = new TripWriter(context, User().Object);

        await writer.UpdateTripAsync(TripId, Dto(notes: "Dock 3, ask for Ana"), WriterTestData.AccountId, CancellationToken.None);

        var trip = await context.Trips.FirstAsync(t => t.TripId == TripId, CancellationToken.None);
        Assert.That(trip.Notes, Is.EqualTo("Dock 3, ask for Ana"));
    }

    /// <summary>
    /// Arming snapshots a Created trip's stops, and the start-time fill only touches NULL
    /// geometries — so a stop MOVED on an armed trip would otherwise keep detecting arrivals at the
    /// place it used to be, for the whole trip, with nothing on screen to say so.
    /// </summary>
    [Test]
    public async Task MovingAStopOnAnArmedTrip_ReSnapshotsItsArrivalGeometry()
    {
        using var context = await SeededAsync();
        var stopId = Guid.NewGuid();
        context.TripStops.Add(WriterTestData.Stop(stopId, TripId, TripStopStatuses.Pending));
        await context.SaveChangesAsync(CancellationToken.None);

        var writer = new TripWriter(context, User().Object);
        await writer.ArmTripAsync(TripId, WriterTestData.AccountId, CancellationToken.None);

        var armed = await context.TripStops.FirstAsync(s => s.TripStopId == stopId, CancellationToken.None);
        var geometryAtArming = armed.ArrivalGeom!.Centroid.Y;

        var stopWriter = new TripStopWriter(context);
        await stopWriter.UpdateStopAsync(stopId, WriterTestData.AccountId, StopDto("Client X", TripStopActivities.Unload, latitude: 5.20), CancellationToken.None);

        var moved = await context.TripStops.FirstAsync(s => s.TripStopId == stopId, CancellationToken.None);

        Assert.That(moved.ArrivalGeom!.Centroid.Y, Is.Not.EqualTo(geometryAtArming).Within(1e-6),
            "the arrival ring must follow the stop, not stay where it was armed");
    }

    [Test]
    public async Task AddingAStopToAnArmedTrip_GivesItGeometryImmediately()
    {
        using var context = await SeededAsync();
        var writer = new TripWriter(context, User().Object);
        await writer.ArmTripAsync(TripId, WriterTestData.AccountId, CancellationToken.None);

        var stopWriter = new TripStopWriter(context);
        var added = await stopWriter.AddStopAsync(
            TripId, WriterTestData.AccountId, StopDto("Client Y", TripStopActivities.Unload), CancellationToken.None);

        var stop = await context.TripStops.FirstAsync(s => s.TripStopId == added.TripStopId, CancellationToken.None);
        Assert.That(stop.ArrivalGeom, Is.Not.Null);
    }

    [Test]
    public async Task ReplacingStopsOnARunningTrip_IsRejected()
    {
        using var context = await SeededAsync(TripStatuses.InProgress);
        context.TripStops.Add(WriterTestData.Stop(Guid.NewGuid(), TripId, TripStopStatuses.Arrived));
        await context.SaveChangesAsync(CancellationToken.None);

        var writer = new TripStopWriter(context);

        // Those stops carry arrivals, departures, deliveries and POD. A "re-plan" that deleted them
        // would erase measurements, which is why the partner import checks this too (§9.2).
        var ex = Assert.ThrowsAsync<ConflictException>(async () => await writer.ReplaceStopsAsync(
            TripId, WriterTestData.AccountId, [], CancellationToken.None));

        Assert.That(ex!.Message, Does.Contain(TripErrorCodes.TripNotActive));
    }

    [Test]
    public async Task ReplacingStopsOnAPlannedTrip_RewritesTheRouteInSequence()
    {
        using var context = await SeededAsync();
        context.TripStops.Add(WriterTestData.Stop(Guid.NewGuid(), TripId, TripStopStatuses.Pending));
        await context.SaveChangesAsync(CancellationToken.None);

        var writer = new TripStopWriter(context);

        await writer.ReplaceStopsAsync(
            TripId,
            WriterTestData.AccountId,
            [StopDto("Client X", TripStopActivities.Unload), StopDto("Plant 3", TripStopActivities.Load)],
            CancellationToken.None);

        var stops = context.TripStops.Where(s => s.TripId == TripId).OrderBy(s => s.Sequence).ToList();

        Assert.Multiple(() =>
        {
            Assert.That(stops, Has.Count.EqualTo(2));
            Assert.That(stops[0].Name, Is.EqualTo("Client X"));
            Assert.That(stops[1].Activity, Is.EqualTo(TripStopActivities.Load));
        });
    }

    private static TripDto Dto(double originLatitude = 4.65, string? notes = null)
        => new(
            "TRIP-001",
            Guid.Parse("55555555-5555-5555-5555-555555555555"),
            null,
            null,
            null,
            null,
            "Depot",
            originLatitude,
            -74.05,
            null,
            TripGeometry.DefaultRadiusMeters,
            new DateTimeOffset(2026, 8, 3, 12, 0, 0, TimeSpan.Zero),
            null,
            notes,
            null);

    private static TripStopDto StopDto(string name, string activity, double latitude = 4.7)
        => new(name, null, null, latitude, -74.0, null, TripGeometry.DefaultRadiusMeters, activity, null, null, false, 0, null);
}
