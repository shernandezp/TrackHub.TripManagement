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

using TrackHub.TripManagement.Application.TripEvents.Services;
using TrackHub.TripManagement.Application.Trips.Services.Interfaces;

namespace TrackHub.TripManagement.Application.UnitTests;

/// <summary>
/// Acceptance 13 (arrival exactly once on replay, 30 s departure debounce) and
/// acceptance 14 (one deviation per episode, re-entry clears the state).
/// <para>
/// <b>The multi-call tests are the point of this fixture.</b> Router feeds this service exactly ONE
/// position per transporter per call (<c>PositionsRetrieved</c> takes the latest fix per
/// transporter), so a scenario played out inside a single <c>ProcessPositionsAsync</c> call is a
/// shape production never produces. Departure and deviation detection were both dead in production
/// while single-call tests stayed green, because the debounce clock and the run length lived in
/// per-request memory. Every state-spanning assertion below therefore calls the service repeatedly
/// with one position each, and <see cref="DetectionHarness"/> round-trips the state through the
/// writers exactly as the database does.
/// </para>
/// </summary>
[TestFixture]
public class TripDetectionServiceTests
{
    private static readonly DateTimeOffset T0 = new(2026, 7, 21, 12, 0, 0, TimeSpan.Zero);

    [Test]
    public async Task Arrival_IsRecordedOnceAndKeyedWithoutAClientEventId()
    {
        var harness = new DetectionHarness();
        harness.StopsContainingPoint(TestFactory.StopId);

        var result = await harness.Service().ProcessPositionsAsync(
            [TestFactory.Position(4.7, -74.0, T0)], TestFactory.AccountId, CancellationToken.None);

        Assert.That(result.StopsArrived, Is.EqualTo(1));
        harness.StopWriter.Verify(w => w.RecordStopProgressAsync(
            TestFactory.TripId, TestFactory.StopId, TestFactory.AccountId, TripStopStatuses.Arrived, T0, 4.7, -74.0,
            TripEventSources.Detection, $"trip-arrive:{TestFactory.StopId:N}", null, It.IsAny<CancellationToken>()), Times.Once);
    }

    [Test]
    public async Task Arrival_ReplayingTheSameBatch_CreatesNoSecondEvent()
    {
        // The writer reports "already recorded" on the replay, exactly as the unique idempotency
        // key makes it do in the database.
        var harness = new DetectionHarness();
        harness.StopsContainingPoint(TestFactory.StopId);

        var first = await harness.Service().ProcessPositionsAsync(
            [TestFactory.Position(4.7, -74.0, T0)], TestFactory.AccountId, CancellationToken.None);

        harness.RecordResult = false;
        var replay = await harness.Service().ProcessPositionsAsync(
            [TestFactory.Position(4.7, -74.0, T0)], TestFactory.AccountId, CancellationToken.None);

        Assert.Multiple(() =>
        {
            Assert.That(first.StopsArrived, Is.EqualTo(1));
            Assert.That(replay.StopsArrived, Is.Zero, "a replayed batch must not produce a second arrival");
        });
    }

    [Test]
    public async Task Arrival_IsRecordedForALaterStopOutOfOrder()
    {
        // Real routes get resequenced by traffic. An out-of-order arrival is recorded, not lost.
        var laterStopId = Guid.NewGuid();
        var harness = new DetectionHarness([
            TestFactory.OpenStop(TestFactory.StopId, sequence: 1),
            TestFactory.OpenStop(laterStopId, sequence: 2)]);
        harness.StopsContainingPoint(laterStopId);

        var result = await harness.Service().ProcessPositionsAsync(
            [TestFactory.Position(4.7, -74.0, T0)], TestFactory.AccountId, CancellationToken.None);

        Assert.That(result.StopsArrived, Is.EqualTo(1));
        harness.StopWriter.Verify(w => w.RecordStopProgressAsync(
            It.IsAny<Guid>(), laterStopId, It.IsAny<Guid>(), TripStopStatuses.Arrived, It.IsAny<DateTimeOffset>(), It.IsAny<double?>(),
            It.IsAny<double?>(), It.IsAny<string>(), It.IsAny<string>(), It.IsAny<string?>(), It.IsAny<CancellationToken>()), Times.Once);
    }

    [Test]
    public async Task Departure_RequiresTheThirtySecondDebounce()
    {
        var harness = new DetectionHarness([TestFactory.OpenStop(TestFactory.StopId, TripStopStatuses.Arrived)]);
        harness.StopsContainingPoint();

        // Leaves the geometry, then a fix 20 s later: still inside the debounce window.
        var result = await harness.Service().ProcessPositionsAsync(
            [TestFactory.Position(5.0, -74.5, T0), TestFactory.Position(5.0, -74.5, T0.AddSeconds(20))],
            TestFactory.AccountId, CancellationToken.None);

        Assert.That(result.StopsDeparted, Is.Zero, "a single bounce off the polygon edge must not close a stop");
    }

    [Test]
    public async Task Departure_IsRecordedOnceTheDebounceElapses()
    {
        var harness = new DetectionHarness([TestFactory.OpenStop(TestFactory.StopId, TripStopStatuses.Arrived)]);
        harness.StopsContainingPoint();

        var result = await harness.Service().ProcessPositionsAsync(
            [TestFactory.Position(5.0, -74.5, T0), TestFactory.Position(5.0, -74.5, T0.AddSeconds(31))],
            TestFactory.AccountId, CancellationToken.None);

        Assert.That(result.StopsDeparted, Is.EqualTo(1));
        harness.StopWriter.Verify(w => w.RecordStopProgressAsync(
            TestFactory.TripId, TestFactory.StopId, TestFactory.AccountId, TripStopStatuses.Departed, T0.AddSeconds(31),
            It.IsAny<double?>(), It.IsAny<double?>(), TripEventSources.Detection,
            $"trip-depart:{TestFactory.StopId:N}", null, It.IsAny<CancellationToken>()), Times.Once);
    }

    [Test]
    public async Task Departure_IsNotRecordedWhileTheVehicleStaysInside()
    {
        var harness = new DetectionHarness([TestFactory.OpenStop(TestFactory.StopId, TripStopStatuses.Arrived)]);
        harness.StopsContainingPoint(TestFactory.StopId);

        var result = await harness.Service().ProcessPositionsAsync(
            [TestFactory.Position(4.7, -74.0, T0), TestFactory.Position(4.7, -74.0, T0.AddMinutes(5))],
            TestFactory.AccountId, CancellationToken.None);

        Assert.That(result.StopsDeparted, Is.Zero);
    }

    // ----- The production shape: one position per call ---------------------------------------

    [Test]
    public async Task Departure_AcrossSeparateCalls_DoesNotFireOnTheFirstOutsideFixAndDoesOnceThirtySecondsHaveElapsed()
    {
        // THE regression test. Router pushes one fix per call; before OutsideSinceAt was persisted,
        // every call re-stamped the clock and the 30 s comparison was unreachable, so a stop stayed
        // Arrived forever and its trip could only ever be completed with `force`.
        var harness = new DetectionHarness([TestFactory.OpenStop(TestFactory.StopId, TripStopStatuses.Arrived)]);
        harness.StopsContainingPoint();
        var service = harness.Service();

        var first = await service.ProcessPositionsAsync(
            [TestFactory.Position(5.0, -74.5, T0)], TestFactory.AccountId, CancellationToken.None);
        var second = await service.ProcessPositionsAsync(
            [TestFactory.Position(5.0, -74.5, T0.AddSeconds(20))], TestFactory.AccountId, CancellationToken.None);
        var third = await service.ProcessPositionsAsync(
            [TestFactory.Position(5.0, -74.5, T0.AddSeconds(31))], TestFactory.AccountId, CancellationToken.None);

        Assert.Multiple(() =>
        {
            Assert.That(first.StopsDeparted, Is.Zero, "the first outside fix only starts the clock");
            Assert.That(second.StopsDeparted, Is.Zero, "still inside the debounce window");
            Assert.That(third.StopsDeparted, Is.EqualTo(1), "the debounce has elapsed across calls");
        });

        // The clock was PERSISTED on the first call - that is what makes the third call possible.
        harness.StopWriter.Verify(w => w.SetStopOutsideSinceAsync(
            TestFactory.StopId, TestFactory.AccountId, T0, It.IsAny<CancellationToken>()), Times.Once);
    }

    [Test]
    public async Task Departure_AcrossSeparateCalls_ReEntryClearsThePersistedClockAndRestartsTheWindow()
    {
        var harness = new DetectionHarness([TestFactory.OpenStop(TestFactory.StopId, TripStopStatuses.Arrived)]);
        var service = harness.Service();

        harness.StopsContainingPoint();
        await service.ProcessPositionsAsync([TestFactory.Position(5.0, -74.5, T0)], TestFactory.AccountId, CancellationToken.None);

        harness.StopsContainingPoint(TestFactory.StopId);
        await service.ProcessPositionsAsync([TestFactory.Position(4.7, -74.0, T0.AddSeconds(10))], TestFactory.AccountId, CancellationToken.None);

        harness.StopsContainingPoint();
        await service.ProcessPositionsAsync([TestFactory.Position(5.0, -74.5, T0.AddSeconds(40))], TestFactory.AccountId, CancellationToken.None);
        var afterRestart = await service.ProcessPositionsAsync(
            [TestFactory.Position(5.0, -74.5, T0.AddSeconds(50))], TestFactory.AccountId, CancellationToken.None);

        Assert.That(afterRestart.StopsDeparted, Is.Zero,
            "re-entry cleared the clock, so only 10 s of the new excursion have elapsed");
        harness.StopWriter.Verify(w => w.SetStopOutsideSinceAsync(
            TestFactory.StopId, TestFactory.AccountId, null, It.IsAny<CancellationToken>()), Times.Once);
    }

    [Test]
    public async Task Departure_ResumesFromThePersistedClockAfterAProcessRestart()
    {
        // A fresh service instance with nothing in memory: the stop row already carries the clock.
        var harness = new DetectionHarness([
            TestFactory.OpenStop(TestFactory.StopId, TripStopStatuses.Arrived, outsideSinceAt: T0)]);
        harness.StopsContainingPoint();

        var result = await harness.Service().ProcessPositionsAsync(
            [TestFactory.Position(5.0, -74.5, T0.AddSeconds(45))], TestFactory.AccountId, CancellationToken.None);

        Assert.That(result.StopsDeparted, Is.EqualTo(1));
    }

    [Test]
    public async Task Deviation_RequiresThreeSeparateCallsEachCarryingOneOutsideFix()
    {
        var harness = new DetectionHarness(hasReadyPlan: true);
        harness.StopsContainingPoint();
        harness.InsideCorridor = false;
        var service = harness.Service();

        var first = await service.ProcessPositionsAsync([TestFactory.Position(9.0, -70.0, T0)], TestFactory.AccountId, CancellationToken.None);
        var second = await service.ProcessPositionsAsync([TestFactory.Position(9.1, -70.1, T0.AddMinutes(1))], TestFactory.AccountId, CancellationToken.None);
        var third = await service.ProcessPositionsAsync([TestFactory.Position(9.2, -70.2, T0.AddMinutes(2))], TestFactory.AccountId, CancellationToken.None);
        var fourth = await service.ProcessPositionsAsync([TestFactory.Position(9.3, -70.3, T0.AddMinutes(3))], TestFactory.AccountId, CancellationToken.None);

        Assert.Multiple(() =>
        {
            Assert.That(first.DeviationsRaised, Is.Zero);
            Assert.That(second.DeviationsRaised, Is.Zero);
            Assert.That(third.DeviationsRaised, Is.EqualTo(1), "the run length has to survive the calls");
            Assert.That(fourth.DeviationsRaised, Is.Zero, "one alert per episode");
        });

        harness.AlertEmitter.Verify(e => e.EmitAsync(
            TripEventTypes.TripRouteDeviation, It.IsAny<string>(), It.IsAny<string>(), It.IsAny<TripAlertDto>(), It.IsAny<CancellationToken>()), Times.Once);
    }

    [Test]
    public async Task Deviation_PersistsTheRunLengthAndTheEpisodeStampThroughTheWriter()
    {
        var harness = new DetectionHarness(hasReadyPlan: true);
        harness.StopsContainingPoint();
        harness.InsideCorridor = false;
        var service = harness.Service();

        await service.ProcessPositionsAsync([TestFactory.Position(9.0, -70.0, T0)], TestFactory.AccountId, CancellationToken.None);
        await service.ProcessPositionsAsync([TestFactory.Position(9.1, -70.1, T0.AddMinutes(1))], TestFactory.AccountId, CancellationToken.None);
        await service.ProcessPositionsAsync([TestFactory.Position(9.2, -70.2, T0.AddMinutes(2))], TestFactory.AccountId, CancellationToken.None);

        // Nothing is stamped while the episode is still climbing to the threshold ...
        harness.UnitOfWork.Verify(u => u.SetDeviationState(
            TestFactory.TripId, null, 1), Times.Once);
        harness.UnitOfWork.Verify(u => u.SetDeviationState(
            TestFactory.TripId, null, 2), Times.Once);

        // ... and the stamp carries the instant the episode opened, which is its identity.
        harness.UnitOfWork.Verify(u => u.SetDeviationState(
            TestFactory.TripId, T0.AddMinutes(2), 3), Times.Once);
    }

    [Test]
    public async Task Deviation_ResumesFromThePersistedRunLengthAfterAProcessRestart()
    {
        // Two fixes were already counted before the process died; the third still opens the episode.
        var harness = new DetectionHarness(hasReadyPlan: true, consecutiveOutsideFixes: 2);
        harness.StopsContainingPoint();
        harness.InsideCorridor = false;

        var result = await harness.Service().ProcessPositionsAsync(
            [TestFactory.Position(9.0, -70.0, T0)], TestFactory.AccountId, CancellationToken.None);

        Assert.That(result.DeviationsRaised, Is.EqualTo(1));
    }

    [Test]
    public async Task Deviation_AnOpenEpisodeIsNotReRaisedAfterAProcessRestart()
    {
        var harness = new DetectionHarness(hasReadyPlan: true, deviationOpenedAt: T0, consecutiveOutsideFixes: 5);
        harness.StopsContainingPoint();
        harness.InsideCorridor = false;

        var result = await harness.Service().ProcessPositionsAsync(
            [TestFactory.Position(9.0, -70.0, T0.AddMinutes(10))], TestFactory.AccountId, CancellationToken.None);

        Assert.That(result.DeviationsRaised, Is.Zero, "the persisted stamp says this episode is already open");
        harness.AlertEmitter.Verify(e => e.EmitAsync(
            TripEventTypes.TripRouteDeviation, It.IsAny<string>(), It.IsAny<string>(), It.IsAny<TripAlertDto>(), It.IsAny<CancellationToken>()), Times.Never);
    }

    [Test]
    public async Task Deviation_AcrossSeparateCalls_ReEntryClearsTheEpisodeAndALaterDepartureMintsANewKey()
    {
        var harness = new DetectionHarness(hasReadyPlan: true);
        harness.StopsContainingPoint();
        var service = harness.Service();

        var raised = 0;
        harness.InsideCorridor = false;
        foreach (var minute in new[] { 0, 1, 2 })
        {
            raised += (await service.ProcessPositionsAsync(
                [TestFactory.Position(9.0, -70.0, T0.AddMinutes(minute))], TestFactory.AccountId, CancellationToken.None)).DeviationsRaised;
        }

        harness.InsideCorridor = true;
        await service.ProcessPositionsAsync(
            [TestFactory.Position(4.7, -74.0, T0.AddMinutes(3))], TestFactory.AccountId, CancellationToken.None);

        harness.InsideCorridor = false;
        foreach (var minute in new[] { 4, 5, 6 })
        {
            raised += (await service.ProcessPositionsAsync(
                [TestFactory.Position(9.0, -70.0, T0.AddMinutes(minute))], TestFactory.AccountId, CancellationToken.None)).DeviationsRaised;
        }

        Assert.That(raised, Is.EqualTo(2), "re-entry clears the episode; a later departure is a new one");

        // Two episodes, two DISTINCT idempotency keys - each derived from its own persisted start.
        Assert.That(harness.DeviationKeys, Is.EqualTo(new[]
        {
            $"trip-deviation:{TestFactory.TripId:N}:{T0.AddMinutes(2).UtcTicks}",
            $"trip-deviation:{TestFactory.TripId:N}:{T0.AddMinutes(6).UtcTicks}",
        }));

        // Re-entry closed the episode in the database, not only in memory.
        harness.UnitOfWork.Verify(u => u.SetDeviationState(
            TestFactory.TripId, null, 0), Times.Once);
    }

    [Test]
    public async Task Deviation_TwoOutsideFixesAreNotEnough()
    {
        var harness = new DetectionHarness(hasReadyPlan: true);
        harness.StopsContainingPoint();
        harness.InsideCorridor = false;

        var result = await harness.Service().ProcessPositionsAsync(
            [TestFactory.Position(9.0, -70.0, T0), TestFactory.Position(9.1, -70.1, T0.AddMinutes(1))],
            TestFactory.AccountId, CancellationToken.None);

        Assert.That(result.DeviationsRaised, Is.Zero);
    }

    [Test]
    public async Task Deviation_IsNotRaisedWithoutAReadyRoutePlan()
    {
        var harness = new DetectionHarness(hasReadyPlan: false);
        harness.StopsContainingPoint();
        harness.InsideCorridor = false;

        var result = await harness.Service().ProcessPositionsAsync(
            [
                TestFactory.Position(9.0, -70.0, T0),
                TestFactory.Position(9.1, -70.1, T0.AddMinutes(1)),
                TestFactory.Position(9.2, -70.2, T0.AddMinutes(2)),
            ],
            TestFactory.AccountId, CancellationToken.None);

        Assert.That(result.DeviationsRaised, Is.Zero);
    }

    [Test]
    public async Task Deviation_IsNotStampedWhenTheAlertEmissionFails()
    {
        // The geofence dwell precedent: nothing is stamped, so the episode is retried next cycle
        // instead of being lost to a transient Manager failure.
        var harness = new DetectionHarness(hasReadyPlan: true);
        harness.StopsContainingPoint();
        harness.InsideCorridor = false;
        harness.AlertEmitter
            .Setup(e => e.EmitAsync(TripEventTypes.TripRouteDeviation, It.IsAny<string>(), It.IsAny<string>(), It.IsAny<TripAlertDto>(), It.IsAny<CancellationToken>()))
            .ThrowsAsync(new InvalidOperationException("Manager is down"));

        var result = await harness.Service().ProcessPositionsAsync(
            [
                TestFactory.Position(9.0, -70.0, T0),
                TestFactory.Position(9.1, -70.1, T0.AddMinutes(1)),
                TestFactory.Position(9.2, -70.2, T0.AddMinutes(2)),
            ],
            TestFactory.AccountId, CancellationToken.None);

        Assert.That(result.DeviationsRaised, Is.Zero);
        harness.EventWriter.Verify(w => w.AppendAsync(
            It.IsAny<Guid>(), It.IsAny<Guid>(), It.IsAny<Guid?>(), TripEventTypes.TripRouteDeviation, It.IsAny<DateTimeOffset>(),
            It.IsAny<string>(), It.IsAny<string?>(), It.IsAny<string>(), It.IsAny<CancellationToken>()), Times.Never);

        // The run length is still persisted: the vehicle really is outside, and losing the count
        // would restart the climb to the threshold on every failed emission.
        harness.UnitOfWork.Verify(u => u.SetDeviationState(
            TestFactory.TripId, null, 3), Times.Once);
    }

    [Test]
    public async Task Deviation_ReEntryClearsTheEpisodeSoALaterDepartureRaisesANewOne()
    {
        var harness = new DetectionHarness(hasReadyPlan: true);
        harness.StopsContainingPoint();

        // Three outside → episode 1; one inside → cleared; three outside → episode 2.
        var outside = new Queue<bool>([false, false, false, true, false, false, false]);
        harness.DetectionReader
            .Setup(r => r.IsInsideCorridorAsync(It.IsAny<Guid>(), It.IsAny<Guid>(), It.IsAny<double>(), It.IsAny<double>(), It.IsAny<CancellationToken>()))
            .ReturnsAsync(() => outside.Dequeue());

        var result = await harness.Service().ProcessPositionsAsync(
            [
                TestFactory.Position(9.0, -70.0, T0),
                TestFactory.Position(9.1, -70.1, T0.AddMinutes(1)),
                TestFactory.Position(9.2, -70.2, T0.AddMinutes(2)),
                TestFactory.Position(4.7, -74.0, T0.AddMinutes(3)),
                TestFactory.Position(9.3, -70.3, T0.AddMinutes(4)),
                TestFactory.Position(9.4, -70.4, T0.AddMinutes(5)),
                TestFactory.Position(9.5, -70.5, T0.AddMinutes(6)),
            ],
            TestFactory.AccountId, CancellationToken.None);

        Assert.That(result.DeviationsRaised, Is.EqualTo(2), "re-entry clears the episode; a later departure is a new one");
    }

    [Test]
    public async Task AlertFailure_NeverFailsPositionProcessing()
    {
        var harness = new DetectionHarness();
        harness.StopsContainingPoint(TestFactory.StopId);
        harness.AlertEmitter
            .Setup(e => e.EmitAsync(It.IsAny<string>(), It.IsAny<string>(), It.IsAny<string>(), It.IsAny<TripAlertDto>(), It.IsAny<CancellationToken>()))
            .ThrowsAsync(new InvalidOperationException("Manager is down"));

        var result = await harness.Service().ProcessPositionsAsync(
            [TestFactory.Position(4.7, -74.0, T0)], TestFactory.AccountId, CancellationToken.None);

        Assert.Multiple(() =>
        {
            Assert.That(result.ProcessedCount, Is.EqualTo(1));
            Assert.That(result.StopsArrived, Is.EqualTo(1), "the arrival still stands; only the notification was lost");
        });
    }

    // ----- The odometer ------------------------------------------------------------------------

    [Test]
    public async Task Distance_TheFirstFixAddsNothing()
    {
        // There is no previous point to measure from. Anything but zero here would mean a trip
        // inherits a phantom leg from wherever the vehicle happened to be first seen.
        var harness = new DetectionHarness();
        harness.StopsContainingPoint();

        await harness.Service().ProcessPositionsAsync(
            [TestFactory.Position(4.65, -74.05, T0)], TestFactory.AccountId, CancellationToken.None);

        Assert.That(harness.AddedDistances, Is.EqualTo(new[] { 0d }));
    }

    [Test]
    public async Task Distance_AccumulatesAcrossSEPARATECallsFromThePersistedLastPoint()
    {
        // The production shape. The previous point can only come from the persisted Trip.LastPoint:
        // Router delivers one fix per call, so a per-request "last point" is always null and the
        // measured distance of every trip in the fleet would be a flat zero forever.
        var harness = new DetectionHarness();
        harness.StopsContainingPoint();
        var service = harness.Service();

        // Three points ~1.1 km apart in latitude (0.01° ≈ 1113 m at the equator-ish).
        await service.ProcessPositionsAsync([TestFactory.Position(4.60, -74.05, T0)], TestFactory.AccountId, CancellationToken.None);
        await service.ProcessPositionsAsync([TestFactory.Position(4.61, -74.05, T0.AddMinutes(1))], TestFactory.AccountId, CancellationToken.None);
        await service.ProcessPositionsAsync([TestFactory.Position(4.62, -74.05, T0.AddMinutes(2))], TestFactory.AccountId, CancellationToken.None);

        Assert.Multiple(() =>
        {
            Assert.That(harness.AddedDistances[0], Is.EqualTo(0d));
            Assert.That(harness.AddedDistances[1], Is.EqualTo(1112d).Within(5d), "the second call must measure from the PERSISTED first point");
            Assert.That(harness.AddedDistances[2], Is.EqualTo(1112d).Within(5d));
            Assert.That(harness.ActualDistanceMeters, Is.EqualTo(2224d).Within(10d));
        });
    }

    [Test]
    public async Task Distance_ResumesFromThePersistedLastPointAfterAProcessRestart()
    {
        // A fresh service with nothing in memory: the leg is measured from the trip row.
        var harness = new DetectionHarness(lastLatitude: 4.60, lastLongitude: -74.05, lastPositionAt: T0);
        harness.StopsContainingPoint();

        await harness.Service().ProcessPositionsAsync(
            [TestFactory.Position(4.61, -74.05, T0.AddMinutes(1))], TestFactory.AccountId, CancellationToken.None);

        Assert.That(harness.AddedDistances.Single(), Is.EqualTo(1112d).Within(5d));
    }

    [Test]
    public async Task Distance_ARejectedOutOfOrderFixLeavesTheOdometerWhereItWas()
    {
        // The writer rejects the stale fix, and the service must treat the whole fix as skipped —
        // the odometer, the last-seen point and every detector alike.
        var harness = new DetectionHarness();
        harness.StopsContainingPoint();
        var service = harness.Service();

        await service.ProcessPositionsAsync([TestFactory.Position(4.60, -74.05, T0.AddMinutes(5))], TestFactory.AccountId, CancellationToken.None);
        var before = harness.ActualDistanceMeters;

        // A redelivered older fix from far away: accepting it would add a ~600 km phantom leg.
        await service.ProcessPositionsAsync([TestFactory.Position(9.99, -70.0, T0)], TestFactory.AccountId, CancellationToken.None);

        Assert.That(harness.ActualDistanceMeters, Is.EqualTo(before));
    }

    [Test]
    public async Task Deviation_ARedeliveredOutOfCorridorFixDoesNotAdvanceTheRunLength()
    {
        // The reason TryAdvanceProgress returns bool at all. Arrival is protected by its
        // idempotency key, but the deviation run length is a plain counter: ONE genuinely
        // out-of-corridor fix redelivered three times (a client retry, or the WithRetry policy
        // firing after a timeout on a call that had already committed) would reach the three-fix
        // threshold on its own and raise a real alert about a vehicle that moved once.
        var harness = new DetectionHarness(hasReadyPlan: true);
        harness.StopsContainingPoint();
        harness.InsideCorridor = false;
        var service = harness.Service();

        var raised = 0;
        for (var delivery = 0; delivery < 3; delivery++)
        {
            // The SAME fix, same timestamp, three times — the writer rejects the second and third.
            raised += (await service.ProcessPositionsAsync(
                [TestFactory.Position(9.0, -70.0, T0)], TestFactory.AccountId, CancellationToken.None)).DeviationsRaised;
        }

        Assert.That(raised, Is.Zero, "one fix delivered three times is one fix, not three");
        harness.UnitOfWork.Verify(u => u.SetDeviationState(
            TestFactory.TripId, It.IsAny<DateTimeOffset?>(), 1), Times.Once);
        harness.UnitOfWork.Verify(u => u.SetDeviationState(
            TestFactory.TripId, It.IsAny<DateTimeOffset?>(), 2), Times.Never);
    }

    // ----- The corridor's three-way answer -----------------------------------------------------

    [Test]
    public async Task Deviation_ARoutePlanWithNoCorridorRaisesNothingHoweverManyFixesArrive()
    {
        // IsInsideCorridorAsync returns bool?, and null means "there is no corridor to evaluate".
        // Treating that as "outside" climbs to the three-fix threshold on a vehicle driving its
        // route perfectly — and RE-ENTRY CAN NEVER CLEAR IT, because there is nothing to re-enter,
        // so the false TripRouteDeviation stands for the rest of the trip.
        var harness = new DetectionHarness(hasReadyPlan: true);
        harness.StopsContainingPoint();
        harness.InsideCorridor = null;
        var service = harness.Service();

        var raised = 0;
        foreach (var minute in new[] { 0, 1, 2, 3, 4 })
        {
            raised += (await service.ProcessPositionsAsync(
                [TestFactory.Position(4.7, -74.0, T0.AddMinutes(minute))], TestFactory.AccountId, CancellationToken.None)).DeviationsRaised;
        }

        Assert.That(raised, Is.Zero, "a plan with no corridor geometry cannot produce a deviation");
        harness.AlertEmitter.Verify(e => e.EmitAsync(
            TripEventTypes.TripRouteDeviation, It.IsAny<string>(), It.IsAny<string>(), It.IsAny<TripAlertDto>(), It.IsAny<CancellationToken>()), Times.Never);
    }

    [Test]
    public async Task Deviation_ANullCorridorAnswerDoesNotTouchTheRunLength()
    {
        // Not merely "raises no alert": the persisted counter must not move either. If null bumped
        // it, a trip that spent a few cycles without a corridor would be primed to fire on its very
        // first genuinely-outside fix instead of needing three.
        var harness = new DetectionHarness(hasReadyPlan: true);
        harness.StopsContainingPoint();
        harness.InsideCorridor = null;

        await harness.Service().ProcessPositionsAsync(
            [TestFactory.Position(4.7, -74.0, T0)], TestFactory.AccountId, CancellationToken.None);

        harness.UnitOfWork.Verify(u => u.SetDeviationState(
            It.IsAny<Guid>(), It.IsAny<DateTimeOffset?>(), It.IsAny<int>()), Times.Never);
    }

    [Test]
    public async Task Deviation_ANullCorridorAnswerDoesNotCloseAnOpenEpisodeEither()
    {
        // The mirror of the above: null is not "inside" any more than it is "outside". Clearing an
        // open episode on null would let the same deviation be re-raised — a duplicate alert for
        // one continuous excursion — as soon as the corridor came back.
        var harness = new DetectionHarness(hasReadyPlan: true, deviationOpenedAt: T0, consecutiveOutsideFixes: 3);
        harness.StopsContainingPoint();
        harness.InsideCorridor = null;

        await harness.Service().ProcessPositionsAsync(
            [TestFactory.Position(4.7, -74.0, T0.AddMinutes(1))], TestFactory.AccountId, CancellationToken.None);

        harness.UnitOfWork.Verify(u => u.SetDeviationState(
            It.IsAny<Guid>(), null, 0), Times.Never);
    }

    /// <summary>
    /// An EMPTY working set short-circuits — which is a different statement from the one this test
    /// used to make. Before zero-touch the reader could only ever return <c>InProgress</c> trips, so
    /// "no open trips" and "nothing to do" were the same thing; now a <c>Created</c> trip in its
    /// activation window is very much actionable, and the reader decides. What is still true is that
    /// a batch the reader answers with nothing costs nothing.
    /// </summary>
    [Test]
    public async Task AnEmptyWorkingSet_ShortCircuits()
    {
        var harness = new DetectionHarness(openTrips: []);

        var result = await harness.Service().ProcessPositionsAsync(
            [TestFactory.Position(4.7, -74.0, T0)], TestFactory.AccountId, CancellationToken.None);

        Assert.That(result, Is.EqualTo(new TripProcessingResultVm(1, 0, 0, 0)));
    }

    // ----- Zero-touch lifecycle (spec 11a §5-§7) ------------------------------------------------

    [Test]
    public async Task Arming_HappensOnceAndWritesNoEvent()
    {
        // Arming must leave NO TripEvent: a trip that was armed and never ran has to stay deletable
        // (acceptance 16). A Detection-sourced row here would quietly make every queued trip
        // permanent the moment its window opened.
        var harness = new DetectionHarness(status: TripStatuses.Created);
        var service = harness.Service();

        await service.ProcessPositionsAsync(
            [TestFactory.Position(5.0, -74.5, T0)], TestFactory.AccountId, CancellationToken.None);
        await service.ProcessPositionsAsync(
            [TestFactory.Position(5.0, -74.5, T0.AddMinutes(1))], TestFactory.AccountId, CancellationToken.None);

        harness.UnitOfWork.Verify(u => u.ArmAsync(TestFactory.TripId, It.IsAny<CancellationToken>()), Times.Once);
        harness.EventWriter.VerifyNoOtherCalls();
    }

    [Test]
    public async Task AutoStart_FiresWhenTheUnitIsInsideTheOriginZone_StampingTheDeviceTime()
    {
        var harness = new DetectionHarness(status: TripStatuses.Created);
        harness.InsideOrigin = true;

        await harness.Service().ProcessPositionsAsync(
            [TestFactory.Position(4.65, -74.05, T0)], TestFactory.AccountId, CancellationToken.None);

        Assert.Multiple(() =>
        {
            // OriginArrivedAt is stamped BEFORE the transition, so the funnel adopts it as the
            // measured ActualStartAt instead of reaching for the server clock (§12.3).
            harness.UnitOfWork.Verify(u => u.RecordOriginVisit(TestFactory.TripId, T0, null), Times.Once);

            // The SAME key manual Start uses: the two paths race safely and exactly one wins.
            harness.TripWriter.Verify(w => w.TransitionTripAsync(
                TestFactory.TripId, TestFactory.AccountId, TripStatuses.InProgress,
                TripEventTypes.TripStarted, TripEventSources.Detection, $"trip-start:{TestFactory.TripId:N}",
                null, null, false, T0, It.IsAny<CancellationToken>()), Times.Once);
        });
    }

    [Test]
    public async Task AutoStart_DoesNotFireWhileTheUnitIsAwayFromTheOrigin()
    {
        var harness = new DetectionHarness(status: TripStatuses.Created);
        harness.InsideOrigin = false;

        await harness.Service().ProcessPositionsAsync(
            [TestFactory.Position(5.0, -74.5, T0)], TestFactory.AccountId, CancellationToken.None);

        harness.TripWriter.Verify(w => w.TransitionTripAsync(
            It.IsAny<Guid>(), It.IsAny<Guid>(), It.IsAny<string>(), It.IsAny<string>(), It.IsAny<string>(),
            It.IsAny<string>(), It.IsAny<string?>(), It.IsAny<string?>(), It.IsAny<bool>(),
            It.IsAny<DateTimeOffset?>(), It.IsAny<CancellationToken>()), Times.Never);
    }

    /// <summary>
    /// THE hazard spec 11 §10 refused to accept, and the reason §6 puts auto-start before the
    /// odometer: nothing may accrue against a trip that has not measurably started. A queued trip
    /// that picks up a distance baseline would report a journey it never made.
    /// </summary>
    [Test]
    public async Task AQueuedTrip_AccruesNoOdometerBeforeItStarts()
    {
        var harness = new DetectionHarness(status: TripStatuses.Created);
        harness.InsideOrigin = false;

        await harness.Service().ProcessPositionsAsync(
            [TestFactory.Position(5.0, -74.5, T0)], TestFactory.AccountId, CancellationToken.None);

        harness.UnitOfWork.Verify(u => u.TryAdvanceProgress(
            It.IsAny<Guid>(), It.IsAny<double>(), It.IsAny<double>(),
            It.IsAny<DateTimeOffset>(), It.IsAny<double>()), Times.Never);
    }

    [Test]
    public async Task OriginDeparture_RequiresTheSameThirtySecondDebounceAcrossCalls()
    {
        // One position per call — the deployed shape. A per-request clock would be re-stamped every
        // call and the window could never elapse, which is the defect that killed stop departure
        // detection in production.
        var harness = new DetectionHarness(hasOriginGeom: true, armedAt: T0.AddHours(-1));
        harness.StopsContainingPoint();
        var service = harness.Service();

        harness.InsideOrigin = false;
        await service.ProcessPositionsAsync(
            [TestFactory.Position(5.0, -74.5, T0)], TestFactory.AccountId, CancellationToken.None);

        harness.UnitOfWork.Verify(u => u.TryRecordOriginDeparture(
            It.IsAny<Guid>(), It.IsAny<DateTimeOffset>()), Times.Never);

        await service.ProcessPositionsAsync(
            [TestFactory.Position(5.0, -74.5, T0.AddSeconds(31))], TestFactory.AccountId, CancellationToken.None);

        Assert.Multiple(() =>
        {
            harness.UnitOfWork.Verify(u => u.TryRecordOriginDeparture(
                TestFactory.TripId, T0.AddSeconds(31)), Times.Once);

            // Timeline only, and never a Manager alert type: origin departure is measurement, not
            // something anyone needs to be woken up for (§11).
            harness.EventWriter.Verify(w => w.AppendAsync(
                TestFactory.AccountId, TestFactory.TripId, null, TripEventTypes.TripOriginDeparted,
                T0.AddSeconds(31), TripEventSources.Detection, null,
                $"trip-origin-depart:{TestFactory.TripId:N}", It.IsAny<CancellationToken>()), Times.Once);
        });
    }

    [Test]
    public async Task OriginDeparture_ClockResetsWhenTheUnitComesBackInside()
    {
        var harness = new DetectionHarness(hasOriginGeom: true, armedAt: T0.AddHours(-1));
        harness.StopsContainingPoint();
        var service = harness.Service();

        harness.InsideOrigin = false;
        await service.ProcessPositionsAsync(
            [TestFactory.Position(5.0, -74.5, T0)], TestFactory.AccountId, CancellationToken.None);

        // Rolled back into the yard: the debounce starts over, so the 31-second-later fix below is
        // only 1 second into a NEW window.
        harness.InsideOrigin = true;
        await service.ProcessPositionsAsync(
            [TestFactory.Position(4.65, -74.05, T0.AddSeconds(30))], TestFactory.AccountId, CancellationToken.None);

        harness.InsideOrigin = false;
        await service.ProcessPositionsAsync(
            [TestFactory.Position(5.0, -74.5, T0.AddSeconds(31))], TestFactory.AccountId, CancellationToken.None);

        harness.UnitOfWork.Verify(u => u.TryRecordOriginDeparture(
            It.IsAny<Guid>(), It.IsAny<DateTimeOffset>()), Times.Never);
    }

    /// <summary>
    /// A round trip's return stop IS the origin. Without this suppression the very fix that starts
    /// the trip also "arrives" at the return stop, and the route reads as complete before the truck
    /// has moved.
    /// </summary>
    [Test]
    public async Task AStopSharingTheOriginZone_DoesNotArriveBeforeTheTripLeaves()
    {
        var harness = new DetectionHarness(hasOriginGeom: true, armedAt: T0.AddHours(-1));
        harness.StopsContainingPoint(TestFactory.StopId);
        harness.InsideOrigin = true;

        var result = await harness.Service().ProcessPositionsAsync(
            [TestFactory.Position(4.65, -74.05, T0)], TestFactory.AccountId, CancellationToken.None);

        Assert.That(result.StopsArrived, Is.Zero);
    }

    [Test]
    public async Task TheReturnStop_ArrivesNormallyOnceTheTripHasLeftItsOrigin()
    {
        // The complement of the test above: the suppression is gated on the trip not having departed
        // its origin, so the genuine return at the END of the round trip still detects.
        var harness = new DetectionHarness(
            hasOriginGeom: true, armedAt: T0.AddHours(-1), originDepartedAt: T0.AddHours(-1));
        harness.StopsContainingPoint(TestFactory.StopId);
        harness.InsideOrigin = true;

        var result = await harness.Service().ProcessPositionsAsync(
            [TestFactory.Position(4.65, -74.05, T0)], TestFactory.AccountId, CancellationToken.None);

        Assert.That(result.StopsArrived, Is.EqualTo(1));
    }

    [Test]
    public async Task ClosingTheLastStop_AsksAutoCompletionToCloseTheTrip()
    {
        var harness = new DetectionHarness([TestFactory.OpenStop(TestFactory.StopId, TripStopStatuses.Arrived)]);
        harness.StopsContainingPoint();
        var service = harness.Service();

        await service.ProcessPositionsAsync(
            [TestFactory.Position(5.0, -74.5, T0)], TestFactory.AccountId, CancellationToken.None);
        await service.ProcessPositionsAsync(
            [TestFactory.Position(5.0, -74.5, T0.AddSeconds(31))], TestFactory.AccountId, CancellationToken.None);

        harness.AutoCompletion.Verify(s => s.TryCompleteAsync(
            TestFactory.AccountId, TestFactory.TripId, T0.AddSeconds(31),
            TripAccountConfigVm.DefaultFinalStopCompletionMinutes, It.IsAny<CancellationToken>()), Times.AtLeastOnce);
    }

    /// <summary>
    /// The depot case, and the reason completion is NOT gated on "a stop just closed": a truck that
    /// arrives home and parks never departs its final stop, so no departure ever fires — but it
    /// keeps reporting, and the dwell rule has to be asked on those fixes. Gated on a stop closing,
    /// this trip would have waited for the sweep, which exists for the opposite case (§5.2).
    /// </summary>
    [Test]
    public async Task AParkedTruckAtItsArrivedFinalStop_IsStillOfferedForCompletion()
    {
        var harness = new DetectionHarness([TestFactory.OpenStop(TestFactory.StopId, TripStopStatuses.Arrived)]);
        harness.StopsContainingPoint(TestFactory.StopId);

        // Sitting inside the stop's geometry: nothing arrives, nothing departs.
        await harness.Service().ProcessPositionsAsync(
            [TestFactory.Position(4.7, -74.0, T0)], TestFactory.AccountId, CancellationToken.None);

        harness.AutoCompletion.Verify(s => s.TryCompleteAsync(
            TestFactory.AccountId, TestFactory.TripId, T0,
            TripAccountConfigVm.DefaultFinalStopCompletionMinutes, It.IsAny<CancellationToken>()), Times.Once);
    }

    [Test]
    public async Task ATripWithAPendingStop_IsNeverOfferedForCompletion()
    {
        var harness = new DetectionHarness([
            TestFactory.OpenStop(TestFactory.StopId, TripStopStatuses.Arrived),
            TestFactory.OpenStop(Guid.NewGuid(), TripStopStatuses.Pending, sequence: 2)]);
        harness.StopsContainingPoint();

        await harness.Service().ProcessPositionsAsync(
            [TestFactory.Position(5.0, -74.5, T0)], TestFactory.AccountId, CancellationToken.None);

        harness.AutoCompletion.Verify(s => s.TryCompleteAsync(
            It.IsAny<Guid>(), It.IsAny<Guid>(), It.IsAny<DateTimeOffset>(), It.IsAny<int>(),
            It.IsAny<CancellationToken>()), Times.Never);
    }

    /// <summary>
    /// A trip closed earlier in the same batch must stop accruing. The odometer writer has no status
    /// guard of its own, so without this a caller that sends several fixes for one transporter would
    /// keep pushing distance onto a completed trip.
    /// </summary>
    [Test]
    public async Task OnceATripCompletesMidBatch_LaterFixesDoNotTouchIt()
    {
        var harness = new DetectionHarness([TestFactory.OpenStop(TestFactory.StopId, TripStopStatuses.Departed)]);
        harness.StopsContainingPoint();
        harness.AutoCompletion
            .Setup(s => s.TryCompleteAsync(
                It.IsAny<Guid>(), It.IsAny<Guid>(), It.IsAny<DateTimeOffset>(), It.IsAny<int>(), It.IsAny<CancellationToken>()))
            .ReturnsAsync(true);

        await harness.Service().ProcessPositionsAsync(
            [TestFactory.Position(5.0, -74.5, T0), TestFactory.Position(5.1, -74.6, T0.AddMinutes(1))],
            TestFactory.AccountId, CancellationToken.None);

        // The first fix ran the pipeline and completed the trip; the second must not.
        harness.UnitOfWork.Verify(u => u.TryAdvanceProgress(
            It.IsAny<Guid>(), It.IsAny<double>(), It.IsAny<double>(),
            It.IsAny<DateTimeOffset>(), It.IsAny<double>()), Times.Once);
    }

    /// <summary>
    /// The account kill switch (§8). With <c>autoLifecycle</c> off the reader is asked for
    /// <c>InProgress</c> trips only — no arming window at all — so a fleet without reliable GPS runs
    /// the manual flow exactly as it did before zero-touch.
    /// </summary>
    [Test]
    public async Task AutoLifecycleOff_AsksForNoArmableTrips()
    {
        var harness = new DetectionHarness();
        harness.Config = TripAccountConfigVm.Default with { AutoLifecycle = false };
        harness.StopsContainingPoint();

        await harness.Service().ProcessPositionsAsync(
            [TestFactory.Position(4.7, -74.0, T0)], TestFactory.AccountId, CancellationToken.None);

        harness.UnitOfWork.Verify(u => u.LoadAsync(
            TestFactory.AccountId,
            It.IsAny<IReadOnlyCollection<Guid>>(),
            It.Is<DateTimeOffset?>(until => !until.HasValue),
            It.IsAny<CancellationToken>()), Times.Once);
    }

    [Test]
    public async Task AutoLifecycleOn_OffersTheActivationWindowToTheReader()
    {
        var harness = new DetectionHarness();
        harness.StopsContainingPoint();

        await harness.Service().ProcessPositionsAsync(
            [TestFactory.Position(4.7, -74.0, T0)], TestFactory.AccountId, CancellationToken.None);

        harness.UnitOfWork.Verify(u => u.LoadAsync(
            TestFactory.AccountId,
            It.IsAny<IReadOnlyCollection<Guid>>(),
            It.Is<DateTimeOffset?>(until => until.HasValue),
            It.IsAny<CancellationToken>()), Times.Once);
    }

    /// <summary>
    /// A stand-in for the trip/stop rows. Every write the service makes lands here and every
    /// <c>LoadAsync</c> re-reads it, so state genuinely has to be PERSISTED to survive a
    /// call — the harness cannot be satisfied by per-request memory.
    /// </summary>
    private sealed class DetectionHarness
    {
        private readonly List<OpenTripStopVm> stops;
        private readonly IReadOnlyCollection<OpenTripVm>? fixedTrips;
        private readonly bool hasReadyPlan;
        private DateTimeOffset? deviationOpenedAt;
        private int consecutiveOutsideFixes;
        private DateTimeOffset? lastPositionAt;
        private double? lastLatitude;
        private double? lastLongitude;
        private double actualDistanceMeters;
        private string status;
        private DateTimeOffset? armedAt;
        private bool hasOriginGeom;
        private DateTimeOffset? originArrivedAt;
        private DateTimeOffset? originDepartedAt;
        private DateTimeOffset? originOutsideSinceAt;

        public DetectionHarness(
            IReadOnlyCollection<OpenTripStopVm>? stops = null,
            bool hasReadyPlan = false,
            IReadOnlyCollection<OpenTripVm>? openTrips = null,
            DateTimeOffset? deviationOpenedAt = null,
            int consecutiveOutsideFixes = 0,
            double? lastLatitude = null,
            double? lastLongitude = null,
            DateTimeOffset? lastPositionAt = null,
            string status = TripStatuses.InProgress,
            DateTimeOffset? armedAt = null,
            bool hasOriginGeom = false,
            DateTimeOffset? originArrivedAt = null,
            DateTimeOffset? originDepartedAt = null,
            DateTimeOffset? originOutsideSinceAt = null)
        {
            this.stops = [.. stops ?? [TestFactory.OpenStop(TestFactory.StopId)]];
            this.hasReadyPlan = hasReadyPlan;
            this.deviationOpenedAt = deviationOpenedAt;
            this.consecutiveOutsideFixes = consecutiveOutsideFixes;
            this.lastLatitude = lastLatitude;
            this.lastLongitude = lastLongitude;
            this.lastPositionAt = lastPositionAt;
            this.status = status;
            this.armedAt = armedAt;
            this.hasOriginGeom = hasOriginGeom;
            this.originArrivedAt = originArrivedAt;
            this.originDepartedAt = originDepartedAt;
            this.originOutsideSinceAt = originOutsideSinceAt;
            fixedTrips = openTrips;

            UnitOfWork
                .Setup(u => u.LoadAsync(
                    TestFactory.AccountId, It.IsAny<IReadOnlyCollection<Guid>>(), It.IsAny<DateTimeOffset?>(), It.IsAny<CancellationToken>()))
                .ReturnsAsync(CurrentTrips);
            DetectionReader
                .Setup(r => r.IsInsideCorridorAsync(It.IsAny<Guid>(), It.IsAny<Guid>(), It.IsAny<double>(), It.IsAny<double>(), It.IsAny<CancellationToken>()))
                .ReturnsAsync(() => InsideCorridor);
            UnitOfWork
                .Setup(u => u.IsInsideOrigin(It.IsAny<Guid>(), It.IsAny<double>(), It.IsAny<double>()))
                .Returns(() => InsideOrigin);

            AccountFeatureReader
                .Setup(r => r.GetAccountConfigAsync(It.IsAny<Guid>(), It.IsAny<CancellationToken>()))
                .ReturnsAsync(() => Config);

            // The real writer freezes the geometry and stamps ArmedAt; the harness models both, so a
            // trip armed on one fix is genuinely armable-no-more on the next.
            UnitOfWork
                .Setup(u => u.ArmAsync(It.IsAny<Guid>(), It.IsAny<CancellationToken>()))
                .ReturnsAsync(() =>
                {
                    if (this.armedAt.HasValue)
                    {
                        return false;
                    }

                    this.armedAt = DateTimeOffset.UtcNow;
                    this.hasOriginGeom = true;
                    return true;
                });

            TripWriter
                .Setup(w => w.TransitionTripAsync(
                    It.IsAny<Guid>(), It.IsAny<Guid>(), It.IsAny<string>(), It.IsAny<string>(), It.IsAny<string>(),
                    It.IsAny<string>(), It.IsAny<string?>(), It.IsAny<string?>(), It.IsAny<bool>(),
                    It.IsAny<DateTimeOffset?>(), It.IsAny<CancellationToken>()))
                .ReturnsAsync((Guid _, Guid _, string toStatus, string _, string _, string _, string? _, string? _,
                    bool _, DateTimeOffset? _, CancellationToken _) =>
                {
                    if (!TransitionResult)
                    {
                        return false;
                    }

                    this.status = toStatus;
                    return true;
                });

            UnitOfWork
                .Setup(u => u.RecordOriginVisit(It.IsAny<Guid>(), It.IsAny<DateTimeOffset?>(), It.IsAny<DateTimeOffset?>()))
                .Callback((Guid _, DateTimeOffset? arrivedAt, DateTimeOffset? departedAt) =>
                {
                    this.originArrivedAt ??= arrivedAt;
                    this.originDepartedAt ??= departedAt;
                });

            UnitOfWork
                .Setup(u => u.TryRecordOriginDeparture(It.IsAny<Guid>(), It.IsAny<DateTimeOffset>()))
                .Returns((Guid _, DateTimeOffset departedAt) =>
                {
                    if (this.originDepartedAt.HasValue)
                    {
                        return false;
                    }

                    this.originDepartedAt = departedAt;
                    this.originOutsideSinceAt = null;
                    return true;
                });

            UnitOfWork
                .Setup(u => u.SetOriginOutsideSince(It.IsAny<Guid>(), It.IsAny<DateTimeOffset?>()))
                .Callback((Guid _, DateTimeOffset? outsideSinceAt) => this.originOutsideSinceAt = outsideSinceAt);

            StopWriter
                .Setup(w => w.RecordStopProgressAsync(
                    It.IsAny<Guid>(), It.IsAny<Guid>(), It.IsAny<Guid>(), It.IsAny<string>(), It.IsAny<DateTimeOffset>(), It.IsAny<double?>(),
                    It.IsAny<double?>(), It.IsAny<string>(), It.IsAny<string>(), It.IsAny<string?>(), It.IsAny<CancellationToken>()))
                .ReturnsAsync((Guid _, Guid stopId, Guid _, string toStatus, DateTimeOffset _, double? _, double? _,
                    string _, string _, string? _, CancellationToken _) =>
                {
                    if (RecordResult)
                    {
                        // The real writer advances the status AND clears the persisted clock.
                        Mutate(stopId, s => s with { Status = toStatus, OutsideSinceAt = null });
                    }

                    return RecordResult;
                });
            StopWriter
                .Setup(w => w.SetStopOutsideSinceAsync(It.IsAny<Guid>(), It.IsAny<Guid>(), It.IsAny<DateTimeOffset?>(), It.IsAny<CancellationToken>()))
                .Callback((Guid stopId, Guid _, DateTimeOffset? outsideSinceAt, CancellationToken _) =>
                    Mutate(stopId, s => s with { OutsideSinceAt = outsideSinceAt }))
                .Returns(Task.CompletedTask);

            // Emulates the REAL writer's out-of-order guard rather than blanket-returning true: a
            // fix whose timestamp is not newer than the last accepted one is rejected, and the
            // service must then skip detection for it entirely. A harness that always accepted
            // would hide exactly the replay defect this models.
            UnitOfWork
                .Setup(u => u.TryAdvanceProgress(
                    It.IsAny<Guid>(), It.IsAny<double>(), It.IsAny<double>(), It.IsAny<DateTimeOffset>(), It.IsAny<double>()))
                .Returns((Guid _, double latitude, double longitude, DateTimeOffset positionAt, double added) =>
                {
                    // `this.` is load-bearing: the constructor's seed parameters share these names,
                    // and an unqualified assignment would write the parameter, leaving the harness
                    // permanently at its initial state while still looking correct.
                    if (this.lastPositionAt is { } last && positionAt <= last)
                    {
                        return false;
                    }

                    // The real writer also PERSISTS the point and the odometer, and the detection
                    // reader projects them back on the next call. Modelling that is what makes the
                    // one-fix-per-call accumulation assertions meaningful.
                    this.lastPositionAt = positionAt;
                    this.lastLatitude = latitude;
                    this.lastLongitude = longitude;
                    this.actualDistanceMeters += Math.Max(added, 0d);
                    AddedDistances.Add(added);
                    return true;
                });

            UnitOfWork
                .Setup(u => u.SetDeviationState(It.IsAny<Guid>(), It.IsAny<DateTimeOffset?>(), It.IsAny<int>()))
                .Callback((Guid _, DateTimeOffset? openedAt, int fixes) =>
                {
                    this.deviationOpenedAt = openedAt;
                    this.consecutiveOutsideFixes = fixes;
                });

            EventWriter
                .Setup(w => w.AppendAsync(
                    It.IsAny<Guid>(), It.IsAny<Guid>(), It.IsAny<Guid?>(), It.IsAny<string>(), It.IsAny<DateTimeOffset>(),
                    It.IsAny<string>(), It.IsAny<string?>(), It.IsAny<string>(), It.IsAny<CancellationToken>()))
                .ReturnsAsync((Guid _, Guid _, Guid? _, string eventType, DateTimeOffset _, string _, string? _,
                    string idempotencyKey, CancellationToken _) =>
                {
                    if (string.Equals(eventType, TripEventTypes.TripRouteDeviation, StringComparison.Ordinal))
                    {
                        DeviationKeys.Add(idempotencyKey);
                    }

                    return true;
                });
        }

        /// <summary>
        /// Deliberately <see cref="Nullable{T}"/>, matching <c>ITripDetectionReader</c>: null means
        /// "this route plan has no corridor geometry to test against", which is a THIRD outcome and
        /// not a synonym for "outside". A harness typed plain <c>bool</c> made that branch
        /// unreachable, so treating null as false stayed green while raising a false deviation on a
        /// vehicle driving its route perfectly.
        /// </summary>
        public bool? InsideCorridor { get; set; } = true;

        /// <summary>Whether the fix being processed falls inside the trip's snapshotted origin zone.</summary>
        public bool InsideOrigin { get; set; }

        /// <summary>What the funnel answers — false models a lost race or a duplicate transition.</summary>
        public bool TransitionResult { get; set; } = true;

        public TripAccountConfigVm Config { get; set; } = TripAccountConfigVm.Default;

        public bool RecordResult { get; set; } = true;

        /// <summary>Every <c>addedDistanceMeters</c> the service handed the writer, in order.</summary>
        public List<double> AddedDistances { get; } = [];

        /// <summary>The odometer as the trip row would hold it after the calls so far.</summary>
        public double ActualDistanceMeters => actualDistanceMeters;

        /// <summary>Every <c>TripEvent</c> idempotency key a deviation minted, in order.</summary>
        public List<string> DeviationKeys { get; } = [];

        public Mock<ITripDetectionReader> DetectionReader { get; } = new();

        public Mock<ITripWriter> TripWriter { get; } = new();

        /// <summary>
        /// The detection working set. It replaced six writer calls per fix with a load-once,
        /// commit-once unit, so the harness models the unit rather than the writers it retired.
        /// </summary>
        public Mock<ITripDetectionUnitOfWork> UnitOfWork { get; } = new();

        public Mock<ITripStopWriter> StopWriter { get; } = new();

        public Mock<ITripEventWriter> EventWriter { get; } = new();

        public Mock<IAlertEmitter> AlertEmitter { get; } = new();

        public Mock<IAccountFeatureReader> AccountFeatureReader { get; } = new();

        public Mock<ITripAutoCompletionService> AutoCompletion { get; } = new();

        public void StopsContainingPoint(params Guid[] stopIds)
            => UnitOfWork
                .Setup(u => u.StopsContainingPoint(It.IsAny<Guid>(), It.IsAny<double>(), It.IsAny<double>()))
                .Returns(stopIds);

        public TripDetectionService Service()
            => new(DetectionReader.Object, UnitOfWork.Object, AccountFeatureReader.Object, TripWriter.Object, StopWriter.Object,
                EventWriter.Object, AutoCompletion.Object, AlertEmitter.Object, TestFactory.Logger<TripDetectionService>());

        private IReadOnlyCollection<OpenTripVm> CurrentTrips()
            => fixedTrips ?? [TestFactory.OpenTrip(
                [.. stops], hasReadyPlan, deviationOpenedAt, consecutiveOutsideFixes,
                actualDistanceMeters, lastLatitude, lastLongitude, lastPositionAt,
                status, null, armedAt, hasOriginGeom, originArrivedAt, originDepartedAt, originOutsideSinceAt)];

        private void Mutate(Guid stopId, Func<OpenTripStopVm, OpenTripStopVm> change)
        {
            for (var i = 0; i < stops.Count; i++)
            {
                if (stops[i].TripStopId == stopId)
                {
                    stops[i] = change(stops[i]);
                    return;
                }
            }
        }
    }
}
