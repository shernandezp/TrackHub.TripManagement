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
using TrackHub.TripManagement.Application.Deliveries.Commands.UpdateOutcome;
using TrackHub.TripManagement.Application.ProofsOfDelivery.Commands.Record;

namespace TrackHub.TripManagement.Application.UnitTests;

/// <summary>
/// Acceptance 15 for the POD and delivery-outcome paths, and acceptance 25 for document scanning:
/// unverified bytes must never enter an auditable evidence trail.
/// </summary>
[TestFixture]
public class ProofOfDeliveryAndDeliveryOutcomeTests
{
    private static readonly Guid ClientEventId = Guid.Parse("88888888-8888-8888-8888-888888888888");
    private static readonly Guid DocumentId = Guid.Parse("99999999-9999-9999-9999-999999999999");
    private static readonly Guid DeliveryId = Guid.Parse("aaaaaaaa-aaaa-aaaa-aaaa-aaaaaaaaaaaa");

    [Test]
    public async Task Pod_WithCleanDocuments_IsRecordedAndKeyedOnTheClientEventId()
    {
        var harness = new PodHarness("Clean", TestFactory.AccountId);

        await harness.Handler().Handle(new RecordProofOfDeliveryCommand(TestFactory.TripId, Pod()), CancellationToken.None);

        harness.EventWriter.Verify(w => w.AppendAsync(
            TestFactory.AccountId, TestFactory.TripId, TestFactory.StopId, TripEventTypes.TripPodSubmitted,
            It.IsAny<DateTimeOffset>(), TripEventSources.Portal, null,
            $"trip-pod:{TestFactory.StopId:N}:{ClientEventId:N}", It.IsAny<CancellationToken>()), Times.Once);
    }

    [TestCase("Pending")]
    [TestCase("Infected")]
    [TestCase("Quarantined")]
    public void Pod_WithANonCleanDocument_IsRejected(string scanStatus)
    {
        var harness = new PodHarness(scanStatus, TestFactory.AccountId);

        var ex = Assert.ThrowsAsync<ValidationException>(async () =>
            await harness.Handler().Handle(new RecordProofOfDeliveryCommand(TestFactory.TripId, Pod()), CancellationToken.None));

        Assert.That(ex!.Errors.Values.SelectMany(v => v), Does.Contain(TripErrorCodes.PodDocumentNotClean));
        harness.Writer.Verify(w => w.RecordAsync(
            It.IsAny<Guid>(), It.IsAny<Guid>(), It.IsAny<ProofOfDeliveryDto>(), It.IsAny<CancellationToken>()), Times.Never);
    }

    [Test]
    public void Pod_WithACleanDocumentFromAnotherAccount_IsRejected()
    {
        var harness = new PodHarness("Clean", Guid.NewGuid());

        var ex = Assert.ThrowsAsync<ValidationException>(async () =>
            await harness.Handler().Handle(new RecordProofOfDeliveryCommand(TestFactory.TripId, Pod()), CancellationToken.None));

        Assert.That(ex!.Errors.Values.SelectMany(v => v), Does.Contain(TripErrorCodes.PodDocumentNotClean));
    }

    [Test]
    public void Pod_WithAnUnknownDocument_IsRejected()
    {
        var harness = new PodHarness(null, null);

        Assert.ThrowsAsync<ValidationException>(async () =>
            await harness.Handler().Handle(new RecordProofOfDeliveryCommand(TestFactory.TripId, Pod()), CancellationToken.None));
    }

    [Test]
    public async Task Pod_WithoutAnExplicitDelivery_ClosesTheWholeStop()
    {
        var harness = new PodHarness("Clean", TestFactory.AccountId);

        await harness.Handler().Handle(new RecordProofOfDeliveryCommand(TestFactory.TripId, Pod()), CancellationToken.None);

        harness.DeliveryWriter.Verify(w => w.MarkStopDeliveriesAsync(
            TestFactory.StopId, TestFactory.AccountId, DeliveryStatuses.Delivered, It.IsAny<CancellationToken>()), Times.Once);
    }

    [Test]
    public async Task Pod_NamingOneDelivery_LeavesTheOtherOutcomesAlone()
    {
        var harness = new PodHarness("Clean", TestFactory.AccountId);

        await harness.Handler().Handle(
            new RecordProofOfDeliveryCommand(TestFactory.TripId, Pod() with { DeliveryId = DeliveryId }), CancellationToken.None);

        harness.DeliveryWriter.Verify(w => w.MarkStopDeliveriesAsync(
            It.IsAny<Guid>(), It.IsAny<Guid>(), It.IsAny<string>(), It.IsAny<CancellationToken>()), Times.Never);
    }

    /// <summary>
    /// Acceptance 15 is about SIDE EFFECTS, not just row counts.
    /// <para>
    /// The writer was already idempotent — the unique (TripStopId, ClientEventId) index returns the
    /// existing POD instead of inserting a second one — but the handler could not tell a replay from
    /// a first submission and marked the stop's deliveries <c>Delivered</c> either way. The real
    /// sequence that breaks: POD recorded, deliveries go Delivered; the operator then records a
    /// genuine <c>Rejected</c> outcome; spec 10's offline outbox re-sends the original POD and
    /// silently reverts the rejection. Exactly one row, and a destroyed business outcome.
    /// </para>
    /// </summary>
    [Test]
    public async Task ReplayedPod_DoesNotReapplyTheDeliveryOutcome()
    {
        var harness = new PodHarness("Clean", TestFactory.AccountId) { Created = false };

        var result = await harness.Handler().Handle(
            new RecordProofOfDeliveryCommand(TestFactory.TripId, Pod()), CancellationToken.None);

        Assert.Multiple(() =>
        {
            // Still a success — a duplicate submission must never look like a failure to the client.
            Assert.That(result.TripStopId, Is.EqualTo(TestFactory.StopId));

            harness.DeliveryWriter.Verify(w => w.MarkStopDeliveriesAsync(
                It.IsAny<Guid>(), It.IsAny<Guid>(), It.IsAny<string>(), It.IsAny<CancellationToken>()), Times.Never);

            harness.AlertEmitter.Verify(e => e.EmitAsync(
                It.IsAny<string>(), It.IsAny<string>(), It.IsAny<string>(), It.IsAny<TripAlertDto>(),
                It.IsAny<CancellationToken>()), Times.Never);
        });
    }

    [Test]
    public async Task DeliveryOutcome_BuildsTheIdempotencyKeyFromTheClientEventId()
    {
        var harness = new OutcomeHarness();

        await harness.Handler().Handle(
            new UpdateDeliveryOutcomeCommand(TestFactory.TripId, DeliveryId, DeliveryStatuses.Delivered, null, ClientEventId),
            CancellationToken.None);

        harness.Writer.Verify(w => w.UpdateDeliveryOutcomeAsync(
            DeliveryId, TestFactory.AccountId, DeliveryStatuses.Delivered, null,
            $"trip-delivery-outcome:{DeliveryId:N}:{ClientEventId:N}", It.IsAny<CancellationToken>()), Times.Once);
    }

    [Test]
    public async Task DeliveryOutcome_DuplicateSubmission_SucceedsAndAppendsNoSecondEvent()
    {
        var harness = new OutcomeHarness(recorded: false);

        var result = await harness.Handler().Handle(
            new UpdateDeliveryOutcomeCommand(TestFactory.TripId, DeliveryId, DeliveryStatuses.Delivered, null, ClientEventId),
            CancellationToken.None);

        Assert.That(result, Is.False);
        harness.EventWriter.Verify(w => w.AppendAsync(
            It.IsAny<Guid>(), It.IsAny<Guid>(), It.IsAny<Guid?>(), It.IsAny<string>(), It.IsAny<DateTimeOffset>(),
            It.IsAny<string>(), It.IsAny<string?>(), It.IsAny<string>(), It.IsAny<CancellationToken>()), Times.Never);
    }

    private static ProofOfDeliveryDto Pod()
        => new(TestFactory.StopId, null, "R. Gomez", null, null, DateTimeOffset.UtcNow, 4.7, -74.0, [DocumentId], ClientEventId);

    /// <summary>
    /// A POD the server already holds is re-accepted even though the trip has since closed. Found by
    /// the deployed smoke suite.
    /// <para>
    /// POD is the last thing recorded at a stop, and closing that stop auto-completes the trip
    /// (§5.2) — so by the time an offline device re-sends, the trip is routinely terminal. Answering
    /// <c>TRIP_ALREADY_TERMINAL</c> to a submission already on file is a permanent failure spec 10's
    /// outbox can only retry forever, which is exactly what acceptance 15 forbids.
    /// </para>
    /// </summary>
    [Test]
    public async Task ReplayingAPodTheServerAlreadyHas_SucceedsEvenAfterTheTripHasClosed()
    {
        var harness = new PodHarness("Clean", TestFactory.AccountId) { Created = false };
        harness.Reader.Setup(r => r.GetTripAsync(TestFactory.TripId, TestFactory.AccountId, It.IsAny<Guid?>(), It.IsAny<CancellationToken>()))
            .ReturnsAsync(TestFactory.Trip(TripStatuses.Completed));
        harness.Writer.Setup(w => w.HasAsync(
                TestFactory.AccountId, TestFactory.StopId, It.IsAny<Guid>(), It.IsAny<CancellationToken>()))
            .ReturnsAsync(true);

        var result = await harness.Handler().Handle(new RecordProofOfDeliveryCommand(TestFactory.TripId, Pod()), CancellationToken.None);

        Assert.That(result.ProofOfDeliveryId, Is.Not.EqualTo(Guid.Empty),
            "the server already stored this POD; the terminal guard must not now reject it");
    }

    /// <summary>A genuinely NEW proof of delivery on a closed trip is still refused.</summary>
    [Test]
    public void ANewPodOnAClosedTrip_IsStillRejected()
    {
        var harness = new PodHarness("Clean", TestFactory.AccountId);
        harness.Reader.Setup(r => r.GetTripAsync(TestFactory.TripId, TestFactory.AccountId, It.IsAny<Guid?>(), It.IsAny<CancellationToken>()))
            .ReturnsAsync(TestFactory.Trip(TripStatuses.Completed));

        var ex = Assert.ThrowsAsync<ValidationException>(async () =>
            await harness.Handler().Handle(new RecordProofOfDeliveryCommand(TestFactory.TripId, Pod()), CancellationToken.None));

        Assert.That(ex!.Errors.Values.SelectMany(v => v), Does.Contain(TripErrorCodes.TripAlreadyTerminal));
    }

    private sealed class PodHarness
    {
        public PodHarness(string? scanStatus, Guid? documentAccountId)
        {
            Reader.Setup(r => r.GetTripAsync(TestFactory.TripId, TestFactory.AccountId, It.IsAny<Guid?>(), It.IsAny<CancellationToken>()))
                .ReturnsAsync(TestFactory.Trip());

            DocumentClient.Setup(c => c.GetDocumentStateAsync(DocumentId, It.IsAny<CancellationToken>()))
                .ReturnsAsync(scanStatus is null || documentAccountId is null
                    ? null
                    : new DocumentStateVm(DocumentId, documentAccountId.Value, scanStatus, "Active"));

            Writer.Setup(w => w.RecordAsync(It.IsAny<Guid>(), It.IsAny<Guid>(), It.IsAny<ProofOfDeliveryDto>(), It.IsAny<CancellationToken>()))
                .ReturnsAsync(() => (new ProofOfDeliveryVm(
                    Guid.NewGuid(), TestFactory.AccountId, TestFactory.StopId, null, "R. Gomez", null,
                    DateTimeOffset.UtcNow, 4.7, -74.0, null, []), Created));

            EventWriter.Setup(w => w.AppendAsync(
                    It.IsAny<Guid>(), It.IsAny<Guid>(), It.IsAny<Guid?>(), It.IsAny<string>(), It.IsAny<DateTimeOffset>(),
                    It.IsAny<string>(), It.IsAny<string?>(), It.IsAny<string>(), It.IsAny<CancellationToken>()))
                .ReturnsAsync(true);
        }

        /// <summary>
        /// Whether the writer reports it INSERTED the POD. False models a replayed offline
        /// submission that matched the unique (TripStopId, ClientEventId) index.
        /// </summary>
        public bool Created { get; set; } = true;

        public Mock<IProofOfDeliveryWriter> Writer { get; } = new();

        public Mock<IDeliveryWriter> DeliveryWriter { get; } = new();

        public Mock<ITripReader> Reader { get; } = new();

        public Mock<ITripEventWriter> EventWriter { get; } = new();

        public Mock<IDocumentClient> DocumentClient { get; } = new();

        public Mock<IAlertEmitter> AlertEmitter { get; } = new();

        public Mock<IUser> User { get; } = TestFactory.User();

        public Mock<IUserReader> UserReader { get; } = TestFactory.UserReader();

        public RecordProofOfDeliveryCommandHandler Handler()
            => new(Writer.Object, DeliveryWriter.Object, Reader.Object, EventWriter.Object, DocumentClient.Object,
                AlertEmitter.Object, UserReader.Object, User.Object, TestFactory.Logger<RecordProofOfDeliveryCommandHandler>());
    }

    private sealed class OutcomeHarness
    {
        public OutcomeHarness(bool recorded = true)
        {
            Reader.Setup(r => r.GetTripAsync(TestFactory.TripId, TestFactory.AccountId, It.IsAny<Guid?>(), It.IsAny<CancellationToken>()))
                .ReturnsAsync(TestFactory.Trip());

            // The delivery resolves to the same visible trip. DeliveryVisibilityTests covers the
            // case where it does not.
            Reader.Setup(r => r.FindVisibleTripIdByDeliveryAsync(
                    It.IsAny<Guid>(), TestFactory.AccountId, It.IsAny<Guid?>(), It.IsAny<CancellationToken>()))
                .ReturnsAsync(TestFactory.TripId);
            Writer.Setup(w => w.UpdateDeliveryOutcomeAsync(
                    It.IsAny<Guid>(), It.IsAny<Guid>(), It.IsAny<string>(), It.IsAny<string?>(), It.IsAny<string>(), It.IsAny<CancellationToken>()))
                .ReturnsAsync(recorded);
            EventWriter.Setup(w => w.AppendAsync(
                    It.IsAny<Guid>(), It.IsAny<Guid>(), It.IsAny<Guid?>(), It.IsAny<string>(), It.IsAny<DateTimeOffset>(),
                    It.IsAny<string>(), It.IsAny<string?>(), It.IsAny<string>(), It.IsAny<CancellationToken>()))
                .ReturnsAsync(true);
        }

        public Mock<IDeliveryWriter> Writer { get; } = new();

        public Mock<ITripReader> Reader { get; } = new();

        public Mock<ITripEventWriter> EventWriter { get; } = new();

        public Mock<IUser> User { get; } = TestFactory.User();

        public Mock<IUserReader> UserReader { get; } = TestFactory.UserReader();

        public UpdateDeliveryOutcomeCommandHandler Handler()
            => new(Writer.Object, Reader.Object, EventWriter.Object, UserReader.Object, User.Object,
                TestFactory.Logger<UpdateDeliveryOutcomeCommandHandler>());
    }
}
