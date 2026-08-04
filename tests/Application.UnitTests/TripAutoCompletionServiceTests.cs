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
/// Spec 11a §5.2: a trip closes itself when its route is done. Two rules, and the second one exists
/// entirely for the depot reality — a truck that arrives home and parks never "departs" its final
/// stop, and the trip still has to close.
/// </summary>
[TestFixture]
public class TripAutoCompletionServiceTests
{
    private static readonly DateTimeOffset T0 = new(2026, 8, 3, 12, 0, 0, TimeSpan.Zero);

    [Test]
    public async Task EveryStopClosed_CompletesAtTheLastMeasuredDeparture()
    {
        var harness = new CompletionHarness([
            Stop(1, TripStopStatuses.Departed, T0, T0.AddMinutes(20)),
            Stop(2, TripStopStatuses.Departed, T0.AddHours(1), T0.AddHours(1).AddMinutes(15))]);

        var completed = await harness.Service().TryCompleteAsync(
            TestFactory.AccountId, TestFactory.TripId, T0.AddHours(5), 30, CancellationToken.None);

        Assert.That(completed, Is.True);
        harness.Writer.Verify(w => w.TransitionTripAsync(
            TestFactory.TripId, TestFactory.AccountId, TripStatuses.Completed,
            TripEventTypes.TripCompleted, TripEventSources.Detection, $"trip-complete:{TestFactory.TripId:N}",
            null, null, false, T0.AddHours(1).AddMinutes(15), It.IsAny<CancellationToken>()), Times.Once);
    }

    /// <summary>
    /// The last DEPARTURE, not the highest sequence. Traffic resequences real routes, so the stop a
    /// vehicle left last is not reliably the one numbered last — and the trip ended when the vehicle
    /// actually pulled away.
    /// </summary>
    [Test]
    public async Task EveryStopClosed_UsesTheLatestDepartureEvenWhenTheRouteRanOutOfOrder()
    {
        var harness = new CompletionHarness([
            Stop(1, TripStopStatuses.Departed, T0.AddHours(2), T0.AddHours(3)),
            Stop(2, TripStopStatuses.Departed, T0, T0.AddMinutes(30))]);

        await harness.Service().TryCompleteAsync(
            TestFactory.AccountId, TestFactory.TripId, T0.AddHours(9), 30, CancellationToken.None);

        harness.Writer.Verify(w => w.TransitionTripAsync(
            It.IsAny<Guid>(), It.IsAny<Guid>(), It.IsAny<string>(), It.IsAny<string>(), It.IsAny<string>(),
            It.IsAny<string>(), It.IsAny<string?>(), It.IsAny<string?>(), It.IsAny<bool>(),
            T0.AddHours(3), It.IsAny<CancellationToken>()), Times.Once);
    }

    [Test]
    public async Task AnArrivedFinalStop_DoesNotCompleteBeforeTheDwellThresholdElapses()
    {
        var harness = new CompletionHarness([
            Stop(1, TripStopStatuses.Departed, T0, T0.AddMinutes(20)),
            Stop(2, TripStopStatuses.Arrived, T0.AddHours(1), null)]);

        var completed = await harness.Service().TryCompleteAsync(
            TestFactory.AccountId, TestFactory.TripId, T0.AddHours(1).AddMinutes(29), 30, CancellationToken.None);

        Assert.That(completed, Is.False);
    }

    [Test]
    public async Task AnArrivedFinalStop_CompletesOnceTheDwellThresholdElapses_AtTheArrivalInstant()
    {
        var harness = new CompletionHarness([
            Stop(1, TripStopStatuses.Departed, T0, T0.AddMinutes(20)),
            Stop(2, TripStopStatuses.Arrived, T0.AddHours(1), null)]);

        var completed = await harness.Service().TryCompleteAsync(
            TestFactory.AccountId, TestFactory.TripId, T0.AddHours(1).AddMinutes(31), 30, CancellationToken.None);

        Assert.That(completed, Is.True);

        // `force` is TRUE here and only here: the final stop is still Arrived, so the writer's
        // open-stop guard would otherwise refuse the very completion this rule exists to perform.
        harness.Writer.Verify(w => w.TransitionTripAsync(
            It.IsAny<Guid>(), It.IsAny<Guid>(), TripStatuses.Completed, It.IsAny<string>(), It.IsAny<string>(),
            It.IsAny<string>(), It.IsAny<string?>(), It.IsAny<string?>(), true,
            T0.AddHours(1), It.IsAny<CancellationToken>()), Times.Once);
    }

    /// <summary>
    /// The dwell rule and the departure debounce were RACING, and the dwell rule won whenever the
    /// dwell outran <c>finalStopCompletionMinutes</c> — which an ordinary 45-minute unload does.
    /// <para>
    /// The rule's claim is "it parked here and will never depart". A running exit-debounce clock is
    /// positive evidence to the contrary, held in the same fix. Closing anyway stamped
    /// <c>ActualEndAt</c> with the ARRIVAL instant and dropped the trip out of the working set, so
    /// the real departure was never recorded: the trip silently lost its whole final dwell.
    /// </para>
    /// </summary>
    [Test]
    public async Task AFinalStopTheVehicleIsAlreadyLeaving_WaitsForTheDepartureInsteadOfClosingOnDwell()
    {
        var harness = new CompletionHarness([
            Stop(1, TripStopStatuses.Departed, T0, T0.AddMinutes(20)),
            Stop(2, TripStopStatuses.Arrived, T0.AddHours(1), null, outsideSinceAt: T0.AddHours(1).AddMinutes(30))]);

        var completed = await harness.Service().TryCompleteAsync(
            TestFactory.AccountId, TestFactory.TripId, T0.AddHours(1).AddMinutes(31), 30, CancellationToken.None);

        Assert.That(completed, Is.False);
        harness.Writer.VerifyNoOtherCalls();
    }

    [Test]
    public async Task AnOpenIntermediateStop_KeepsTheTripRunning()
    {
        var harness = new CompletionHarness([
            Stop(1, TripStopStatuses.Pending, null, null),
            Stop(2, TripStopStatuses.Arrived, T0, null)]);

        var completed = await harness.Service().TryCompleteAsync(
            TestFactory.AccountId, TestFactory.TripId, T0.AddDays(1), 30, CancellationToken.None);

        Assert.That(completed, Is.False);
    }

    [Test]
    public async Task ATripWithNoStops_IsNeverAutoCompleted()
    {
        // Nothing measures its end. It stays open for a dispatcher to close, because inventing a
        // completion instant for it would be a guess dressed as a measurement.
        var harness = new CompletionHarness([]);

        var completed = await harness.Service().TryCompleteAsync(
            TestFactory.AccountId, TestFactory.TripId, T0.AddDays(1), 30, CancellationToken.None);

        Assert.That(completed, Is.False);
        harness.Writer.VerifyNoOtherCalls();
    }

    [Test]
    public async Task ATripThatIsNoLongerRunning_IsLeftAlone()
    {
        var harness = new CompletionHarness(
            [Stop(1, TripStopStatuses.Departed, T0, T0.AddMinutes(5))],
            status: TripStatuses.Paused);

        var completed = await harness.Service().TryCompleteAsync(
            TestFactory.AccountId, TestFactory.TripId, T0.AddDays(1), 30, CancellationToken.None);

        Assert.That(completed, Is.False);
        harness.Writer.VerifyNoOtherCalls();
    }

    /// <summary>
    /// A dispatcher's Complete racing the sweep: they share an idempotency key, so the writer
    /// answers false to the loser and the loser must emit nothing.
    /// </summary>
    [Test]
    public async Task ALostRace_EmitsNoSecondAlert()
    {
        var harness = new CompletionHarness([Stop(1, TripStopStatuses.Departed, T0, T0.AddMinutes(5))]);
        harness.TransitionResult = false;

        var completed = await harness.Service().TryCompleteAsync(
            TestFactory.AccountId, TestFactory.TripId, T0.AddHours(1), 30, CancellationToken.None);

        Assert.That(completed, Is.False);
        harness.AlertEmitter.VerifyNoOtherCalls();
    }

    [Test]
    public async Task Sweep_SkipsAccountsRunningTheManualFlow()
    {
        var harness = new CompletionHarness([Stop(1, TripStopStatuses.Departed, T0, T0.AddMinutes(5))]);
        harness.Config = TripAccountConfigVm.Default with { AutoLifecycle = false };

        var completed = await harness.Service().SweepAsync(CancellationToken.None);

        Assert.That(completed, Is.Zero);
        harness.Writer.VerifyNoOtherCalls();
    }

    private static CompletionStopVm Stop(
        int sequence, string status, DateTimeOffset? arrivedAt, DateTimeOffset? departedAt, DateTimeOffset? outsideSinceAt = null)
        => new(Guid.NewGuid(), sequence, status, arrivedAt, departedAt, outsideSinceAt);

    private sealed class CompletionHarness
    {
        public CompletionHarness(IReadOnlyCollection<CompletionStopVm> stops, string status = TripStatuses.InProgress)
        {
            DetectionReader
                .Setup(r => r.GetCompletionStateAsync(TestFactory.AccountId, TestFactory.TripId, It.IsAny<CancellationToken>()))
                .ReturnsAsync(new TripCompletionCandidateVm(
                    TestFactory.TripId, "TRIP-001", status, TestFactory.TransporterId, null, stops));

            DetectionReader
                .Setup(r => r.GetCompletionCandidatesAsync(It.IsAny<Guid>(), It.IsAny<CancellationToken>()))
                .ReturnsAsync([TestFactory.TripId]);

            AccountFeatureReader
                .Setup(r => r.GetEnabledAccountIdsAsync(It.IsAny<string>(), It.IsAny<CancellationToken>()))
                .ReturnsAsync([TestFactory.AccountId]);
            AccountFeatureReader
                .Setup(r => r.GetAccountConfigAsync(It.IsAny<Guid>(), It.IsAny<CancellationToken>()))
                .ReturnsAsync(() => Config);

            Writer
                .Setup(w => w.TransitionTripAsync(
                    It.IsAny<Guid>(), It.IsAny<Guid>(), It.IsAny<string>(), It.IsAny<string>(), It.IsAny<string>(),
                    It.IsAny<string>(), It.IsAny<string?>(), It.IsAny<string?>(), It.IsAny<bool>(),
                    It.IsAny<DateTimeOffset?>(), It.IsAny<CancellationToken>()))
                .ReturnsAsync(() => TransitionResult);
        }

        public bool TransitionResult { get; set; } = true;

        public TripAccountConfigVm Config { get; set; } = TripAccountConfigVm.Default;

        public Mock<ITripDetectionReader> DetectionReader { get; } = new();

        public Mock<IAccountFeatureReader> AccountFeatureReader { get; } = new();

        public Mock<ITripWriter> Writer { get; } = new(MockBehavior.Strict);

        public Mock<IAlertEmitter> AlertEmitter { get; } = new();

        public TripAutoCompletionService Service()
            => new(AccountFeatureReader.Object, DetectionReader.Object, Writer.Object, AlertEmitter.Object,
                TestFactory.Logger<TripAutoCompletionService>());
    }
}
