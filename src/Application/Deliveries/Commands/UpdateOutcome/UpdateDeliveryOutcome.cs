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

namespace TrackHub.TripManagement.Application.Deliveries.Commands.UpdateOutcome;

/// <summary>
/// Records what actually happened to a delivery (delivered / partially delivered / rejected).
/// Idempotent on <c>trip-delivery-outcome:{deliveryId:N}:{clientEventId:N}</c>: a duplicate
/// submission returns success and writes no second row (acceptance 15) — the guarantee spec 10's
/// offline outbox depends on, which is why the key is built server-side from the caller's event id
/// rather than trusted from the caller.
/// <para>
/// No <c>PrincipalTypes = "Driver"</c> here (acceptance 6); spec 10 widens the attribute additively.
/// </para>
/// </summary>
[Authorize(Resource = Resources.Trips, Action = Actions.Edit)]
[RequireFeature(FeatureKeys.TripManagement)]
// Enforcement: the handler derives the caller's own account and passes it to the reader/writer,
// which filters every row on it (TripVisibility is the single visibility resolver - spec 11).
[AccountScopeEnforcedInHandler]
public readonly record struct UpdateDeliveryOutcomeCommand(
    Guid TripId,
    Guid DeliveryId,
    string Status,
    string? Observations,
    Guid ClientEventId) : IRequest<bool>;

public sealed class UpdateDeliveryOutcomeCommandHandler(
    IDeliveryWriter writer,
    ITripReader reader,
    ITripEventWriter tripEventWriter,
    IUserReader userReader,
    IUser user,
    ILogger<UpdateDeliveryOutcomeCommandHandler> logger) : IRequestHandler<UpdateDeliveryOutcomeCommand, bool>
{
    private Guid UserId { get; } = TripVisibility.RequireUserId(user);

    public async Task<bool> Handle(UpdateDeliveryOutcomeCommand request, CancellationToken cancellationToken)
    {
        var caller = await userReader.GetUserAsync(UserId, cancellationToken);
        var scopeUserId = TripVisibility.ResolveScopeUserId(user, UserId);
        var trip = await reader.GetTripAsync(request.TripId, caller.AccountId, scopeUserId, cancellationToken);

        // The delivery is addressed independently of the trip, so it is resolved under the same
        // scope: passing a visible TripId alongside another group's DeliveryId must not work.
        await TripVisibility.ResolveVisibleTripByDeliveryAsync(
            reader, request.DeliveryId, caller.AccountId, scopeUserId, cancellationToken);

        var idempotencyKey = $"trip-delivery-outcome:{request.DeliveryId:N}:{request.ClientEventId:N}";

        // Idempotency BEFORE the terminal guard (acceptance 15), for the same reason as POD and stop
        // progress: the trip closes behind the outcome, and a replay of an outcome the server already
        // recorded must be a success the device can retire — not a permanent error it retries forever.
        // A genuinely new outcome on a closed trip is still refused.
        if (!await tripEventWriter.HasEventAsync(caller.AccountId, idempotencyKey, cancellationToken)
            && TripStatuses.IsTerminal(trip.Status))
        {
            throw TripValidationFailure.Create(nameof(UpdateDeliveryOutcomeCommand.TripId), TripErrorCodes.TripAlreadyTerminal);
        }

        var recorded = await writer.UpdateDeliveryOutcomeAsync(
            request.DeliveryId, caller.AccountId, request.Status, request.Observations, idempotencyKey, cancellationToken);

        if (!recorded)
        {
            logger.LogDebug("Delivery outcome {IdempotencyKey} was a duplicate; no second row written", idempotencyKey);
            return false;
        }

        await tripEventWriter.AppendAsync(
            caller.AccountId,
            request.TripId,
            null,
            TripEventTypes.TripDeliveryOutcomeRecorded,
            DateTimeOffset.UtcNow,
            TripEventSources.Portal,
            null,
            idempotencyKey,
            cancellationToken);

        return true;
    }
}

public sealed class UpdateDeliveryOutcomeValidator : AbstractValidator<UpdateDeliveryOutcomeCommand>
{
    public UpdateDeliveryOutcomeValidator()
    {
        RuleFor(v => v.TripId).NotEmpty();
        RuleFor(v => v.DeliveryId).NotEmpty();
        RuleFor(v => v.ClientEventId).NotEmpty();
        RuleFor(v => v.Observations).MaximumLength(1000);
        RuleFor(v => v.Status)
            .Must(DeliveryStatuses.IsValid)
            .WithMessage("Unknown delivery status.");
    }
}
