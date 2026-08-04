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

using TrackHub.TripManagement.Application.Trips.Services;

namespace TrackHub.TripManagement.Application.UnitTests;

/// <summary>
/// Spec 11a §5.4: a trip planned after its truck already left. Evidence beats declaration — when
/// Geofencing recorded the visit, those measurements are replayed and the declared time is ignored.
/// </summary>
[TestFixture]
public class TripStartBackfillServiceTests
{
    private static readonly DateTimeOffset T0 = new(2026, 8, 3, 6, 0, 0, TimeSpan.Zero);
    private static readonly Guid OriginGeofenceId = Guid.Parse("77777777-7777-7777-7777-777777777777");
    private static readonly Guid StopGeofenceId = Guid.Parse("88888888-8888-8888-8888-888888888888");

    [Test]
    public async Task ARecordedOriginVisit_IsReplayedAsMeasurementAndBeatsTheDeclaredTime()
    {
        var harness = new BackfillHarness(originGeofenceId: OriginGeofenceId);
        harness.Visits = [new GeofenceVisitVm(OriginGeofenceId, T0, T0.AddMinutes(45))];

        var result = await harness.Service().ApplyAsync(
            TestFactory.TripId, TestFactory.AccountId, null, T0.AddHours(3), CancellationToken.None);

        Assert.Multiple(() =>
        {
            Assert.That(result.Started, Is.True);
            Assert.That(result.Backfilled, Is.True);

            // The declared 09:00 is discarded: the vehicle was measurably at the plant at 06:00.
            Assert.That(result.StartedAt, Is.EqualTo(T0));
        });

        harness.Writer.Verify(w => w.SetOriginVisitAsync(
            TestFactory.TripId, TestFactory.AccountId, T0, T0.AddMinutes(45), It.IsAny<CancellationToken>()), Times.Once);

        // Source Detection: these are measurements, replayed — not a dispatcher's assertion (§5.3).
        harness.Writer.Verify(w => w.TransitionTripAsync(
            TestFactory.TripId, TestFactory.AccountId, TripStatuses.InProgress,
            TripEventTypes.TripStarted, TripEventSources.Detection, $"trip-start:{TestFactory.TripId:N}",
            null, null, false, T0, It.IsAny<CancellationToken>()), Times.Once);
    }

    /// <summary>
    /// No evidence: the declared instant becomes the origin DEPARTURE, and <c>OriginArrivedAt</c>
    /// stays null. Loading was not measured, and a fabricated arrival would put an invented loading
    /// duration straight into the reports.
    /// </summary>
    [Test]
    public async Task WithNoRecordedVisit_TheDeclaredStartIsTheOriginDepartureAndLoadingStaysUnmeasured()
    {
        var harness = new BackfillHarness(originGeofenceId: null);

        var result = await harness.Service().ApplyAsync(
            TestFactory.TripId, TestFactory.AccountId, null, T0.AddHours(3), CancellationToken.None);

        Assert.Multiple(() =>
        {
            Assert.That(result.Started, Is.True);
            Assert.That(result.Backfilled, Is.False);
        });

        harness.Writer.Verify(w => w.SetOriginVisitAsync(
            TestFactory.TripId, TestFactory.AccountId, null, T0.AddHours(3), It.IsAny<CancellationToken>()), Times.Once);
        harness.Writer.Verify(w => w.TransitionTripAsync(
            It.IsAny<Guid>(), It.IsAny<Guid>(), It.IsAny<string>(), It.IsAny<string>(), TripEventSources.Portal,
            It.IsAny<string>(), It.IsAny<string?>(), It.IsAny<string?>(), It.IsAny<bool>(),
            T0.AddHours(3), It.IsAny<CancellationToken>()), Times.Once);
    }

    [Test]
    public void WithNeitherEvidenceNorADeclaredTime_TheCallerIsToldWhatIsMissing()
    {
        var harness = new BackfillHarness(originGeofenceId: null);

        var ex = Assert.ThrowsAsync<ValidationException>(async () => await harness.Service().ApplyAsync(
            TestFactory.TripId, TestFactory.AccountId, null, null, CancellationToken.None));

        Assert.That(ex!.Errors.Values.SelectMany(v => v), Does.Contain(TripErrorCodes.StartEvidenceRequired));
    }

    /// <summary>
    /// An OPEN visit means the truck is still standing at the origin. That is not a trip already in
    /// transit — it is a trip live detection is about to start on its own — so it must not count as
    /// evidence.
    /// </summary>
    [Test]
    public async Task AnOpenOriginVisit_IsNotEvidence()
    {
        var harness = new BackfillHarness(originGeofenceId: OriginGeofenceId);
        harness.Visits = [new GeofenceVisitVm(OriginGeofenceId, T0, null)];

        var result = await harness.Service().ApplyAsync(
            TestFactory.TripId, TestFactory.AccountId, null, T0.AddHours(3), CancellationToken.None);

        Assert.That(result.Backfilled, Is.False);
    }

    [Test]
    public async Task StopVisitsAfterTheOriginExit_AreReplayedUnderTheLiveDetectionKeys()
    {
        var harness = new BackfillHarness(originGeofenceId: OriginGeofenceId, stopGeofenceId: StopGeofenceId);
        harness.Visits = [
            new GeofenceVisitVm(OriginGeofenceId, T0, T0.AddMinutes(45)),
            new GeofenceVisitVm(StopGeofenceId, T0.AddHours(2), T0.AddHours(3))];

        var result = await harness.Service().ApplyAsync(
            TestFactory.TripId, TestFactory.AccountId, null, null, CancellationToken.None);

        Assert.That(result.StopsReplayed, Is.EqualTo(1));

        // The SAME keys detection uses, so a live fix for this stop moments later is recognised as
        // the event already recorded rather than written a second time.
        harness.StopWriter.Verify(w => w.RecordStopProgressAsync(
            TestFactory.TripId, TestFactory.StopId, TestFactory.AccountId, TripStopStatuses.Arrived,
            T0.AddHours(2), null, null, TripEventSources.Detection,
            $"trip-arrive:{TestFactory.StopId:N}", null, It.IsAny<CancellationToken>()), Times.Once);
        harness.StopWriter.Verify(w => w.RecordStopProgressAsync(
            TestFactory.TripId, TestFactory.StopId, TestFactory.AccountId, TripStopStatuses.Departed,
            T0.AddHours(3), null, null, TripEventSources.Detection,
            $"trip-depart:{TestFactory.StopId:N}", null, It.IsAny<CancellationToken>()), Times.Once);
    }

    [Test]
    public async Task AVisitFromBeforeTheOriginExit_IsNotMistakenForThisTripsStop()
    {
        // Yesterday's delivery to the same client. Matching it would fabricate an arrival hours
        // before the truck left the plant.
        var harness = new BackfillHarness(originGeofenceId: OriginGeofenceId, stopGeofenceId: StopGeofenceId);
        harness.Visits = [
            new GeofenceVisitVm(StopGeofenceId, T0.AddHours(-6), T0.AddHours(-5)),
            new GeofenceVisitVm(OriginGeofenceId, T0, T0.AddMinutes(45))];

        var result = await harness.Service().ApplyAsync(
            TestFactory.TripId, TestFactory.AccountId, null, null, CancellationToken.None);

        Assert.That(result.StopsReplayed, Is.Zero);
    }

    [Test]
    public async Task ATripThatIsAlreadyRunning_IsLeftUntouched()
    {
        var harness = new BackfillHarness(originGeofenceId: OriginGeofenceId, status: TripStatuses.InProgress);

        var result = await harness.Service().ApplyAsync(
            TestFactory.TripId, TestFactory.AccountId, null, T0, CancellationToken.None);

        Assert.That(result.Started, Is.False);
        harness.Writer.Verify(w => w.TransitionTripAsync(
            It.IsAny<Guid>(), It.IsAny<Guid>(), It.IsAny<string>(), It.IsAny<string>(), It.IsAny<string>(),
            It.IsAny<string>(), It.IsAny<string?>(), It.IsAny<string?>(), It.IsAny<bool>(),
            It.IsAny<DateTimeOffset?>(), It.IsAny<CancellationToken>()), Times.Never);
    }

    private sealed class BackfillHarness
    {
        public BackfillHarness(
            Guid? originGeofenceId,
            Guid? stopGeofenceId = null,
            string status = TripStatuses.Created)
        {
            var trip = TestFactory.Trip(status, originGeofenceId: originGeofenceId);
            var stop = TestFactory.Stop() with { GeofenceId = stopGeofenceId };

            Reader
                .Setup(r => r.GetTripDetailAsync(TestFactory.TripId, TestFactory.AccountId, It.IsAny<Guid?>(), It.IsAny<CancellationToken>()))
                .ReturnsAsync(new TripDetailVm(trip, [stop], null, null, [], []));
            Reader
                .Setup(r => r.GetTripAsync(TestFactory.TripId, TestFactory.AccountId, It.IsAny<Guid?>(), It.IsAny<CancellationToken>()))
                .ReturnsAsync(trip);

            AccountFeatureReader
                .Setup(r => r.GetAccountConfigAsync(It.IsAny<Guid>(), It.IsAny<CancellationToken>()))
                .ReturnsAsync(TripAccountConfigVm.Default);

            VisitReader
                .Setup(r => r.GetVisitsAsync(
                    It.IsAny<Guid>(), It.IsAny<Guid>(), It.IsAny<IReadOnlyCollection<Guid>>(),
                    It.IsAny<DateTimeOffset>(), It.IsAny<CancellationToken>()))
                .ReturnsAsync((Guid _, Guid _, IReadOnlyCollection<Guid> geofenceIds, DateTimeOffset _, CancellationToken _) =>
                    [.. Visits.Where(v => geofenceIds.Contains(v.GeofenceId))]);

            Writer
                .Setup(w => w.ArmTripAsync(It.IsAny<Guid>(), It.IsAny<Guid>(), It.IsAny<CancellationToken>()))
                .ReturnsAsync(true);
            Writer
                .Setup(w => w.SetOriginVisitAsync(
                    It.IsAny<Guid>(), It.IsAny<Guid>(), It.IsAny<DateTimeOffset?>(), It.IsAny<DateTimeOffset?>(), It.IsAny<CancellationToken>()))
                .Returns(Task.CompletedTask);
            Writer
                .Setup(w => w.TransitionTripAsync(
                    It.IsAny<Guid>(), It.IsAny<Guid>(), It.IsAny<string>(), It.IsAny<string>(), It.IsAny<string>(),
                    It.IsAny<string>(), It.IsAny<string?>(), It.IsAny<string?>(), It.IsAny<bool>(),
                    It.IsAny<DateTimeOffset?>(), It.IsAny<CancellationToken>()))
                .ReturnsAsync(true);

            StopWriter
                .Setup(w => w.RecordStopProgressAsync(
                    It.IsAny<Guid>(), It.IsAny<Guid>(), It.IsAny<Guid>(), It.IsAny<string>(), It.IsAny<DateTimeOffset>(),
                    It.IsAny<double?>(), It.IsAny<double?>(), It.IsAny<string>(), It.IsAny<string>(),
                    It.IsAny<string?>(), It.IsAny<CancellationToken>()))
                .ReturnsAsync(true);
        }

        public List<GeofenceVisitVm> Visits { get; set; } = [];

        public Mock<ITripReader> Reader { get; } = new();

        public Mock<ITripWriter> Writer { get; } = new();

        public Mock<ITripStopWriter> StopWriter { get; } = new();

        public Mock<IGeofenceVisitReader> VisitReader { get; } = new();

        public Mock<IAccountFeatureReader> AccountFeatureReader { get; } = new();

        public Mock<IAlertEmitter> AlertEmitter { get; } = new();

        public TripStartBackfillService Service()
            => new(Reader.Object, Writer.Object, StopWriter.Object, VisitReader.Object,
                AccountFeatureReader.Object, AlertEmitter.Object, TestFactory.Logger<TripStartBackfillService>());
    }
}
