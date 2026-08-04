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
using Microsoft.Extensions.Logging;
using TrackHub.TripManagement.Application.Common;

namespace TrackHub.TripManagement.Application.ProofsOfDelivery.Commands.Record;

/// <summary>
/// Captures proof of delivery. Documents are ordinary Manager <c>Document</c> records uploaded
/// through the existing REST surface and linked here by id — this module introduces no storage
/// surface of its own (spec 11 §11).
/// <para>
/// Every referenced document is validated to belong to the trip's account AND to be
/// <c>ScanStatus = Clean</c> before anything is written. Linking a quarantined or unscanned file to
/// a delivery record would put unverified bytes into an auditable evidence trail, so the rejection
/// is <see cref="TripErrorCodes.PodDocumentNotClean"/> rather than a silent skip (acceptance 25).
/// </para>
/// <para>
/// Idempotent on the unique <c>(TripStopId, ClientEventId)</c> index (acceptance 15). No
/// <c>PrincipalTypes = "Driver"</c> here (acceptance 6) — spec 10 widens it additively.
/// </para>
/// </summary>
[Authorize(Resource = Resources.Trips, Action = Actions.Write)]
[RequireFeature(FeatureKeys.TripManagement)]
// Enforcement: the handler derives the caller's own account and passes it to the reader/writer,
// which filters every row on it (TripVisibility is the single visibility resolver - spec 11).
[AccountScopeEnforcedInHandler]
public readonly record struct RecordProofOfDeliveryCommand(Guid TripId, ProofOfDeliveryDto ProofOfDelivery) : IRequest<ProofOfDeliveryVm>;

public sealed class RecordProofOfDeliveryCommandHandler(
    IProofOfDeliveryWriter writer,
    IDeliveryWriter deliveryWriter,
    ITripReader reader,
    ITripEventWriter tripEventWriter,
    IDocumentClient documentClient,
    IAlertEmitter alertEmitter,
    IUserReader userReader,
    IUser user,
    ILogger<RecordProofOfDeliveryCommandHandler> logger) : IRequestHandler<RecordProofOfDeliveryCommand, ProofOfDeliveryVm>
{
    private const string CleanScanStatus = "Clean";

    private Guid UserId { get; } = TripVisibility.RequireUserId(user);

    public async Task<ProofOfDeliveryVm> Handle(RecordProofOfDeliveryCommand request, CancellationToken cancellationToken)
    {
        var caller = await userReader.GetUserAsync(UserId, cancellationToken);

        // Under the caller's visibility scope: recording proof of delivery against another group's
        // trip is a forged entry in an auditable evidence trail.
        var scopeUserId = TripVisibility.ResolveScopeUserId(user, UserId);
        var trip = await reader.GetTripAsync(request.TripId, caller.AccountId, scopeUserId, cancellationToken);

        // Idempotency BEFORE the terminal guard (acceptance 15). A POD is the last thing recorded at
        // a stop and the trip closes right behind it — auto-completion fires the moment that stop
        // closes (§5.2) — so by the time an offline device re-sends, the trip is routinely terminal.
        // Answering TRIP_ALREADY_TERMINAL to a submission the server already stored is a permanent
        // failure the outbox can only retry forever.
        //
        // A genuinely NEW proof of delivery on a closed trip is still refused: only the replay of a
        // submission already on file gets past this.
        var replay = await writer.HasAsync(
            caller.AccountId, request.ProofOfDelivery.TripStopId, request.ProofOfDelivery.ClientEventId, cancellationToken);

        if (!replay && TripStatuses.IsTerminal(trip.Status))
            throw TripValidationFailure.Create(nameof(RecordProofOfDeliveryCommand.TripId), TripErrorCodes.TripAlreadyTerminal);

        foreach (var documentId in request.ProofOfDelivery.DocumentIds)
        {
            var state = await documentClient.GetDocumentStateAsync(documentId, cancellationToken);
            if (state is not { } document
                || document.AccountId != caller.AccountId
                || !string.Equals(document.ScanStatus, CleanScanStatus, StringComparison.OrdinalIgnoreCase))
            {
                throw TripValidationFailure.Create(nameof(ProofOfDeliveryDto.DocumentIds), TripErrorCodes.PodDocumentNotClean);
            }
        }

        var (pod, created) = await writer.RecordAsync(caller.AccountId, request.TripId, request.ProofOfDelivery, cancellationToken);

        // A REPLAY stops here. The row was already there, so re-applying the side effects below
        // would undo work done since the original submission: the operator records a genuine
        // Rejected outcome, spec 10's offline outbox then re-sends the original POD, and the stop's
        // deliveries flip back to Delivered. One row was never the whole of idempotency
        // (acceptance 15) — the side effects have to be once-only too.
        if (!created)
        {
            return pod;
        }

        // A POD without an explicit delivery closes the whole stop; a POD naming one delivery
        // leaves the others alone, because a partial outcome is recorded through
        // UpdateDeliveryOutcome and must not be overwritten here.
        if (request.ProofOfDelivery.DeliveryId is null)
        {
            await deliveryWriter.MarkStopDeliveriesAsync(
                request.ProofOfDelivery.TripStopId, caller.AccountId, DeliveryStatuses.Delivered, cancellationToken);
        }

        await tripEventWriter.AppendAsync(
            caller.AccountId,
            request.TripId,
            request.ProofOfDelivery.TripStopId,
            TripEventTypes.TripPodSubmitted,
            request.ProofOfDelivery.CapturedAt,
            TripEventSources.Portal,
            null,
            $"trip-pod:{request.ProofOfDelivery.TripStopId:N}:{request.ProofOfDelivery.ClientEventId:N}",
            cancellationToken);

        try
        {
            await alertEmitter.EmitAsync(
                TripEventTypes.TripPodSubmitted,
                TripAlertSeverities.Info,
                $"trip-podsubmitted:{request.ProofOfDelivery.TripStopId:N}",
                new TripAlertDto(
                    caller.AccountId, request.TripId, request.ProofOfDelivery.TripStopId, trip.Code,
                    trip.TransporterId, trip.DriverId, null, request.ProofOfDelivery.CapturedAt,
                    null, null, null, request.ProofOfDelivery.Latitude, request.ProofOfDelivery.Longitude),
                cancellationToken);
        }
        catch (Exception ex)
        {
            logger.LogError(ex, "Failed to emit TripPodSubmitted alert for stop {TripStopId}", request.ProofOfDelivery.TripStopId);
        }

        return pod;
    }
}

public sealed class RecordProofOfDeliveryValidator : AbstractValidator<RecordProofOfDeliveryCommand>
{
    public RecordProofOfDeliveryValidator()
    {
        RuleFor(v => v.TripId).NotEmpty();
        RuleFor(v => v.ProofOfDelivery.TripStopId).NotEmpty();
        RuleFor(v => v.ProofOfDelivery.ClientEventId).NotEmpty();
        RuleFor(v => v.ProofOfDelivery.ReceiverName).NotEmpty().MaximumLength(200);
        RuleFor(v => v.ProofOfDelivery.ReceiverDocument).MaximumLength(50);
        RuleFor(v => v.ProofOfDelivery.Notes).MaximumLength(1000);
        RuleFor(v => v.ProofOfDelivery.CapturedAt).NotEqual(default(DateTimeOffset));
        RuleFor(v => v.ProofOfDelivery.Latitude).InclusiveBetween(-90d, 90d).When(v => v.ProofOfDelivery.Latitude.HasValue);
        RuleFor(v => v.ProofOfDelivery.Longitude).InclusiveBetween(-180d, 180d).When(v => v.ProofOfDelivery.Longitude.HasValue);
    }
}
