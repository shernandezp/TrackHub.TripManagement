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
using Microsoft.Extensions.Logging.Abstractions;
using NetTopologySuite.Geometries;
using TrackHub.TripManagement.Application.TripEvents.Services;
using TrackHub.TripManagement.Application.Trips.Services;
using TrackHub.TripManagement.Domain.Interfaces;
using TrackHub.TripManagement.Infrastructure.TripDB;
using TrackHub.TripManagement.Infrastructure.TripDB.Entities;
using TrackHub.TripManagement.Infrastructure.TripDB.Readers;
using TrackHub.TripManagement.Infrastructure.TripDB.Writers;

namespace TrackHub.TripManagement.Application.UnitTests;

/// <summary>
/// The whole zero-touch lifecycle driven through the REAL writers and the REAL reader, on a context
/// configured exactly as the service registers it.
/// <para>
/// Every other detection fixture mocks <c>ITripWriter</c>/<c>ITripStopWriter</c>, and the writer
/// fixtures call the writers directly. Nothing joined the two — and the seam between them is
/// precisely where the defect lived that made all of this inert in production: the second write to
/// a trip inside one request threw, Router swallowed it, and no test could see it. A suite that
/// only ever exercises each side alone cannot answer "does zero-touch work".
/// </para>
/// <para>
/// <b>One position per call, always.</b> Router pushes one deduped fix per transporter per tick, so
/// a scenario driven through a single call proves nothing about the deployed behaviour — the
/// debounce clocks and the deviation run length only mature across calls (spec 11 §7.4).
/// </para>
/// <para>
/// Containment runs in-process here: the InMemory provider evaluates
/// <c>ArrivalGeom.Contains(point)</c> against real NetTopologySuite geometry, which is the same
/// predicate PostGIS answers. Translation to SQL is guarded separately by
/// <c>TripQueryTranslationTests</c>.
/// </para>
/// </summary>
[TestFixture]
public class ZeroTouchEndToEndTests
{
    private static readonly Guid AccountId = Guid.Parse("11111111-1111-1111-1111-111111111111");
    private static readonly Guid TripId = Guid.Parse("bbbbbbbb-0000-0000-0000-000000000001");
    private static readonly Guid StopId = Guid.Parse("bbbbbbbb-0000-0000-0000-000000000002");
    private static readonly Guid TransporterId = Guid.Parse("55555555-5555-5555-5555-555555555555");

    private static readonly DateTimeOffset Start = new(2026, 8, 3, 7, 0, 0, TimeSpan.Zero);

    // The origin yard and a customer site ~7 km away, so neither 150 m ring can contain the other.
    private const double OriginLat = 4.65;
    private const double OriginLng = -74.05;
    private const double StopLat = 4.70;
    private const double StopLng = -74.00;

    /// <summary>
    /// Counts saves and can fail one on demand — the two things the transaction boundary is asserted
    /// with. Neither is observable in the resulting rows, which is why they need instrumentation.
    /// </summary>
    private sealed class CountingContext(DbContextOptions<ApplicationDbContext> options) : ApplicationDbContext(options)
    {
        private bool failNext;

        public int Saves { get; set; }

        public void FailNextSave() => failNext = true;

        public override Task<int> SaveChangesAsync(CancellationToken cancellationToken = default)
        {
            Saves++;

            if (!failNext)
            {
                return base.SaveChangesAsync(cancellationToken);
            }

            failNext = false;
            throw new InvalidOperationException("simulated failure part-way through a fix");
        }
    }

    private sealed class Harness : IDisposable
    {
        public Harness()
        {
            Context = new CountingContext(new DbContextOptionsBuilder<ApplicationDbContext>()
                .UseInMemoryDatabase($"zero-touch-{Guid.NewGuid()}")

                // The registered behaviour (TripDB/DependencyInjection.cs). Without this line the
                // whole point of the fixture is lost: every writer would silently work.
                .UseQueryTrackingBehavior(QueryTrackingBehavior.NoTracking)
                .ConfigureWarnings(w => w.Ignore(Microsoft.EntityFrameworkCore.Diagnostics.InMemoryEventId.TransactionIgnoredWarning))
                .Options);

            var user = new Mock<IUser>();
            user.SetupGet(u => u.PrincipalType).Returns(PrincipalType.User);
            user.SetupGet(u => u.UserId).Returns(Guid.NewGuid());

            AccountFeatureReader
                .Setup(r => r.GetAccountConfigAsync(It.IsAny<Guid>(), It.IsAny<CancellationToken>()))
                .ReturnsAsync(TripAccountConfigVm.Default);

            var detectionReader = new TripDetectionReader(Context);
            var tripWriter = new TripWriter(Context, user.Object);
            var stopWriter = new TripStopWriter(Context);
            var eventWriter = new TripEventWriter(Context);

            // The REAL unit of work, sharing the one context — so this fixture exercises the actual
            // load-once/commit-once boundary rather than a stub of it.
            var unitOfWork = new TripDetectionUnitOfWork(Context, NullLogger<TripDetectionUnitOfWork>.Instance);

            var autoCompletion = new TripAutoCompletionService(
                AccountFeatureReader.Object, detectionReader, tripWriter, AlertEmitter.Object,
                NullLogger<TripAutoCompletionService>.Instance);

            Service = new TripDetectionService(
                detectionReader, unitOfWork, AccountFeatureReader.Object, tripWriter, stopWriter, eventWriter,
                autoCompletion, AlertEmitter.Object, NullLogger<TripDetectionService>.Instance);
        }

        public CountingContext Context { get; }

        public Mock<IAccountFeatureReader> AccountFeatureReader { get; } = new();

        public Mock<IAlertEmitter> AlertEmitter { get; } = new();

        public TripDetectionService Service { get; }

        /// <summary>One fix, one call — the deployed shape.</summary>
        public Task FixAsync(double latitude, double longitude, DateTimeOffset at)
            => Service.ProcessPositionsAsync(
                [new TransporterPositionDto(TransporterId, latitude, longitude, at)], AccountId, CancellationToken.None);

        public async Task<Trip> TripAsync()
            => await Context.Trips.AsNoTracking().FirstAsync(t => t.TripId == TripId, CancellationToken.None);

        public async Task<TripStop> StopAsync()
            => await Context.TripStops.AsNoTracking().FirstAsync(s => s.TripStopId == StopId, CancellationToken.None);

        public void Dispose() => Context.Dispose();
    }

    private static async Task<Harness> SeededAsync()
    {
        var harness = new Harness();

        harness.Context.Trips.Add(new Trip
        {
            TripId = TripId,
            AccountId = AccountId,
            Code = "TRIP-E2E",
            Status = TripStatuses.Created,
            TransporterId = TransporterId,
            OriginName = "Depot",
            OriginPoint = new Point(OriginLng, OriginLat) { SRID = 4326 },
            OriginRadiusMeters = TripGeometry.DefaultRadiusMeters,

            // Inside the activation window, so the trip is armable on the first fix.
            PlannedStartAt = DateTimeOffset.UtcNow.AddMinutes(10),
        });

        harness.Context.TripStops.Add(new TripStop
        {
            TripStopId = StopId,
            AccountId = AccountId,
            TripId = TripId,
            Sequence = 1,
            Name = "Client X",
            Point = new Point(StopLng, StopLat) { SRID = 4326 },
            ArrivalRadiusMeters = TripGeometry.DefaultRadiusMeters,
            Activity = TripStopActivities.Unload,
            Status = TripStopStatuses.Pending,
            EtaSource = EtaSources.Unavailable,
        });

        await harness.Context.SaveChangesAsync(CancellationToken.None);
        harness.Context.ChangeTracker.Clear();
        return harness;
    }

    /// <summary>
    /// The mandate in one test: nobody clicks Start, nobody clicks Complete. A truck standing at its
    /// origin starts its own trip, leaving measures the loading window, reaching the customer marks
    /// the arrival, and leaving the customer closes the trip.
    /// </summary>
    [Test]
    public async Task AVehicleRunningItsRoute_StartsMeasuresAndClosesItsOwnTrip()
    {
        using var harness = await SeededAsync();

        // 1. Standing in the origin yard: arm, then auto-start on the same fix.
        await harness.FixAsync(OriginLat, OriginLng, Start);

        var afterStart = await harness.TripAsync();
        Assert.Multiple(() =>
        {
            Assert.That(afterStart.ArmedAt, Is.Not.Null, "the trip never armed");
            Assert.That(afterStart.Status, Is.EqualTo(TripStatuses.InProgress), "the trip never auto-started");
            Assert.That(afterStart.OriginArrivedAt, Is.EqualTo(Start));

            // The MEASURED start — the fix's own DeviceDateTime, never the server clock (§12.3).
            Assert.That(afterStart.ActualStartAt, Is.EqualTo(Start));
        });

        // 2. Rolling out. The first outside fix only starts the debounce clock.
        await harness.FixAsync(4.66, -74.04, Start.AddMinutes(20));
        var loading = await harness.TripAsync();
        Assert.Multiple(() =>
        {
            Assert.That(loading.OriginOutsideSinceAt, Is.EqualTo(Start.AddMinutes(20)), "the exit debounce clock was not persisted");
            Assert.That(loading.OriginDepartedAt, Is.Null, "the origin was abandoned on a single fix, with no debounce");
        });

        // 3. Still outside 30 s later: loading is over, transit begins.
        await harness.FixAsync(4.67, -74.03, Start.AddMinutes(21));
        var inTransit = await harness.TripAsync();
        Assert.That(inTransit.OriginDepartedAt, Is.EqualTo(Start.AddMinutes(21)), "the origin departure never registered");

        // 4. Arriving at the customer.
        await harness.FixAsync(StopLat, StopLng, Start.AddMinutes(60));
        var arrived = await harness.StopAsync();
        Assert.Multiple(() =>
        {
            Assert.That(arrived.Status, Is.EqualTo(TripStopStatuses.Arrived));
            Assert.That(arrived.ActualArrivalAt, Is.EqualTo(Start.AddMinutes(60)));
        });

        // 5. Leaving the customer — again two fixes, because the debounce spans calls.
        await harness.FixAsync(4.71, -74.01, Start.AddMinutes(90));
        Assert.That((await harness.StopAsync()).Status, Is.EqualTo(TripStopStatuses.Arrived), "the stop closed without its debounce");

        await harness.FixAsync(4.72, -74.02, Start.AddMinutes(91));

        var departed = await harness.StopAsync();
        var completed = await harness.TripAsync();
        Assert.Multiple(() =>
        {
            Assert.That(departed.Status, Is.EqualTo(TripStopStatuses.Departed));

            // 6. Last stop closed, so the trip closes itself — at the MEASURED departure.
            Assert.That(completed.Status, Is.EqualTo(TripStatuses.Completed), "the route finished but the trip never auto-completed");
            Assert.That(completed.ActualEndAt, Is.EqualTo(Start.AddMinutes(91)));
        });
    }

    /// <summary>
    /// The measurements the reports are built on (§4.3): loading is origin arrival → origin
    /// departure, and the odometer accumulates from the fixes rather than from the plan.
    /// </summary>
    [Test]
    public async Task TheMeasuredLoadingWindowAndOdometer_SurviveTheWholeRun()
    {
        using var harness = await SeededAsync();

        await harness.FixAsync(OriginLat, OriginLng, Start);
        await harness.FixAsync(4.66, -74.04, Start.AddMinutes(20));
        await harness.FixAsync(4.67, -74.03, Start.AddMinutes(21));

        var trip = await harness.TripAsync();
        Assert.Multiple(() =>
        {
            Assert.That(
                trip.OriginDepartedAt!.Value - trip.OriginArrivedAt!.Value,
                Is.EqualTo(TimeSpan.FromMinutes(21)),
                "the loading window is what the trip-summary report calls LoadingMinutes");
            Assert.That(trip.ActualDistanceMeters, Is.GreaterThan(0), "the odometer never accumulated");
            Assert.That(trip.LastPositionAt, Is.EqualTo(Start.AddMinutes(21)));
        });
    }

    /// <summary>
    /// A trip that only exists because a dispatcher planned it must not start on a vehicle that
    /// happens to be parked somewhere else. Positional evidence is the trigger, not the clock (§2).
    /// </summary>
    [Test]
    public async Task AnArmedTripWhoseVehicleIsElsewhere_ArmsButDoesNotStart()
    {
        using var harness = await SeededAsync();

        await harness.FixAsync(StopLat, StopLng, Start);

        var trip = await harness.TripAsync();
        Assert.Multiple(() =>
        {
            Assert.That(trip.ArmedAt, Is.Not.Null, "arming is what freezes the geometry; it should still have happened");
            Assert.That(trip.Status, Is.EqualTo(TripStatuses.Created), "a trip started on a clock, not on evidence");
            Assert.That(trip.ActualStartAt, Is.Null);

            // Nothing accrues before the measured start — the hazard spec 11 §10 refused to accept.
            Assert.That(trip.LastPositionAt, Is.Null, "the odometer ran before the trip started");
            Assert.That(trip.ActualDistanceMeters, Is.Zero);
        });
    }

    /// <summary>
    /// The account-level kill switch: with <c>autoLifecycle</c> off the working set is exactly what
    /// it was before zero-touch, so a fleet without reliable GPS runs the manual flow untouched (§8).
    /// </summary>
    [Test]
    public async Task WithAutoLifecycleOff_AVehicleAtItsOriginStartsNothing()
    {
        using var harness = await SeededAsync();
        harness.AccountFeatureReader
            .Setup(r => r.GetAccountConfigAsync(It.IsAny<Guid>(), It.IsAny<CancellationToken>()))
            .ReturnsAsync(TripAccountConfigVm.Default with { AutoLifecycle = false });

        await harness.FixAsync(OriginLat, OriginLng, Start);

        var trip = await harness.TripAsync();
        Assert.Multiple(() =>
        {
            Assert.That(trip.Status, Is.EqualTo(TripStatuses.Created));
            Assert.That(trip.ArmedAt, Is.Null, "the kill switch must keep the trip out of the working set entirely");
        });
    }

    /// <summary>
    /// One fix, one transaction (§6). The unit of work buffers everything a fix measures and commits
    /// it together, so a partially-applied fix is not a state the database can be left in.
    /// <para>
    /// This is asserted by counting saves rather than by inspecting rows, because the failure it
    /// guards is invisible in the final state: five sequential saves and one batched save leave the
    /// same data behind, and differ only in what a crash halfway through would have left.
    /// </para>
    /// </summary>
    [Test]
    public async Task AFixThatMovesSeveralMeasurements_CommitsThemInASingleTransaction()
    {
        using var harness = await SeededAsync();

        // Get the trip running and past its origin so the next fix exercises the busy path: odometer,
        // origin debounce clock and deviation counter all moving on one position.
        await harness.FixAsync(OriginLat, OriginLng, Start);
        harness.Context.Saves = 0;

        await harness.FixAsync(4.66, -74.04, Start.AddMinutes(20));

        Assert.That(harness.Context.Saves, Is.EqualTo(1),
            "the odometer and the origin debounce clock were committed separately — a crash between them leaves the trip contradicting itself");
    }

    /// <summary>
    /// A trip that throws part-way through its fix is dropped with its buffer, and the other vehicles
    /// in the same batch are unaffected. Committing the half-applied fix would publish exactly the
    /// state the unit exists to prevent.
    /// </summary>
    [Test]
    public async Task AFixThatFailsPartWay_CommitsNothingAndLeavesTheTripUntouched()
    {
        using var harness = await SeededAsync();

        await harness.FixAsync(OriginLat, OriginLng, Start);
        var beforeFailure = await harness.TripAsync();

        harness.Context.FailNextSave();
        await harness.FixAsync(4.66, -74.04, Start.AddMinutes(20));

        var afterFailure = await harness.TripAsync();
        Assert.Multiple(() =>
        {
            Assert.That(afterFailure.ActualDistanceMeters, Is.EqualTo(beforeFailure.ActualDistanceMeters),
                "a failed fix committed its odometer anyway");
            Assert.That(afterFailure.OriginOutsideSinceAt, Is.Null, "a failed fix committed its debounce clock anyway");
            Assert.That(afterFailure.Status, Is.EqualTo(TripStatuses.InProgress), "the trip itself must be untouched");
        });
    }

    /// <summary>
    /// A trip that fails is dropped ALONE. Its fleetmates in the same batch keep being measured.
    /// <para>
    /// Discarding the whole working set on a failure is the easy mistake, and it is invisible: the
    /// remaining vehicles do not error, they just quietly stop being detected for the rest of the
    /// batch. On a fleet of hundreds, one bad trip would silently switch automation off for all of
    /// them.
    /// </para>
    /// </summary>
    [Test]
    public async Task ATripThatFails_DoesNotSilenceTheOtherVehiclesInTheSameBatch()
    {
        using var harness = await SeededAsync();

        var otherTripId = Guid.Parse("bbbbbbbb-0000-0000-0000-0000000000ff");
        var otherTransporterId = Guid.Parse("66666666-6666-6666-6666-666666666666");
        harness.Context.Trips.Add(new Trip
        {
            TripId = otherTripId,
            AccountId = AccountId,
            Code = "TRIP-OTHER",
            Status = TripStatuses.InProgress,
            TransporterId = otherTransporterId,
            OriginName = "Depot",
            OriginPoint = new Point(OriginLng, OriginLat) { SRID = 4326 },
            OriginRadiusMeters = TripGeometry.DefaultRadiusMeters,
            PlannedStartAt = Start,
        });
        await harness.Context.SaveChangesAsync(CancellationToken.None);
        harness.Context.ChangeTracker.Clear();

        // Both vehicles report in one batch, and the FIRST one's save blows up. Ordering is by
        // transporter id, so the seeded 5555… trip is processed before the 6666… one.
        harness.Context.FailNextSave();
        await harness.Service.ProcessPositionsAsync(
            [
                new TransporterPositionDto(TransporterId, 4.66, -74.04, Start.AddMinutes(20)),
                new TransporterPositionDto(otherTransporterId, 4.80, -74.20, Start.AddMinutes(20)),
            ],
            AccountId,
            CancellationToken.None);

        var other = await harness.Context.Trips.AsNoTracking().FirstAsync(t => t.TripId == otherTripId, CancellationToken.None);
        Assert.That(other.LastPositionAt, Is.EqualTo(Start.AddMinutes(20)),
            "the failing trip took its fleetmate's measurements down with it");
    }

    /// <summary>
    /// Replaying a batch changes nothing (acceptance 13). Router retries, and the WithRetry policy
    /// can redeliver a call that already committed.
    /// </summary>
    [Test]
    public async Task ReplayingTheSameFix_ProducesNoSecondStartAndNoSecondEvent()
    {
        using var harness = await SeededAsync();

        await harness.FixAsync(OriginLat, OriginLng, Start);
        await harness.FixAsync(OriginLat, OriginLng, Start);

        var starts = await harness.Context.TripEvents.AsNoTracking()
            .CountAsync(e => e.TripId == TripId && e.EventType == TripEventTypes.TripStarted, CancellationToken.None);

        Assert.That(starts, Is.EqualTo(1), "a replayed batch started the trip twice");
    }
}
