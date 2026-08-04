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

using Microsoft.Extensions.Logging;
using TrackHub.TripManagement.Application.Common;

namespace TrackHub.TripManagement.Application.Integration.Commands.UpdateTripStatus;

/// <summary>
/// Partner/TMS status push, addressed by <c>ExternalReference</c> so the caller never needs to
/// learn TrackHub's ids. Per-item results, never a batch failure (spec 11 §7.9), and the same
/// transition matrix as the portal path — an integration cannot drive a trip through a state
/// change a dispatcher would be refused.
/// </summary>
[Authorize(Resource = Resources.Trips, Action = Actions.Write, PrincipalTypes = "ServiceClient")]
[RequireFeature(FeatureKeys.TripManagement)]
public readonly record struct UpdateTripStatusCommand(
    Guid AccountId,
    IReadOnlyCollection<TripStatusUpdateItem> Updates) : IRequest<IReadOnlyCollection<TripImportResultVm>>;

/// <summary>One external status push.</summary>
public readonly record struct TripStatusUpdateItem(string ExternalReference, string Status, string? Reason);

public sealed class UpdateTripStatusCommandHandler(
    ITripWriter writer,
    ITripReader reader,
    ILogger<UpdateTripStatusCommandHandler> logger) : IRequestHandler<UpdateTripStatusCommand, IReadOnlyCollection<TripImportResultVm>>
{
    public async Task<IReadOnlyCollection<TripImportResultVm>> Handle(UpdateTripStatusCommand request, CancellationToken cancellationToken)
    {
        var results = new List<TripImportResultVm>(request.Updates.Count);

        foreach (var update in request.Updates)
        {
            try
            {
                results.Add(await ApplyAsync(request.AccountId, update, cancellationToken));
            }
            catch (Exception ex)
            {
                logger.LogError(ex, "Trip status update failed for external reference {ExternalReference}", update.ExternalReference);
                results.Add(new TripImportResultVm(update.ExternalReference, false, null, "TRIP_STATUS_UPDATE_FAILED", ex.Message));
            }
        }

        return results;
    }

    private async Task<TripImportResultVm> ApplyAsync(Guid accountId, TripStatusUpdateItem update, CancellationToken cancellationToken)
    {
        var trip = await TripLookup.FindByExternalReferenceAsync(reader, accountId, update.ExternalReference, cancellationToken);
        if (trip is not { } current)
            return new TripImportResultVm(update.ExternalReference, false, null, "TRIP_NOT_FOUND", "No trip with that external reference.");

        if (!TripStatuses.CanTransition(current.Status, update.Status))
            return new TripImportResultVm(update.ExternalReference, false, current.TripId, TripErrorCodes.InvalidTransition, $"{current.Status} cannot transition to {update.Status}.");

        // One funnel call: the status change and its timeline event commit together, and the event
        // type is derived from the transition rather than string-built — `Trip{Status}` produced
        // "TripInProgress", a type no catalog on either side of the wire has ever known.
        await writer.TransitionTripAsync(
            current.TripId,
            accountId,
            update.Status,
            TripEventTypes.ForTransition(current.Status, update.Status),
            TripEventSources.ServiceClient,
            $"trip-external-{update.Status.ToLowerInvariant()}:{current.TripId:N}",
            null,
            update.Reason,
            force: false,
            measuredAt: null,
            cancellationToken);

        return new TripImportResultVm(update.ExternalReference, true, current.TripId, null, null);
    }
}

public sealed class UpdateTripStatusValidator : AbstractValidator<UpdateTripStatusCommand>
{
    public UpdateTripStatusValidator()
    {
        RuleFor(v => v.AccountId).NotEmpty();
        RuleFor(v => v.Updates).NotEmpty();
        RuleForEach(v => v.Updates).ChildRules(update =>
        {
            update.RuleFor(u => u.ExternalReference).NotEmpty().MaximumLength(80);
            update.RuleFor(u => u.Status).Must(TripStatuses.IsValid).WithMessage("Unknown trip status.");
            update.RuleFor(u => u.Reason).MaximumLength(500);
        });
    }
}
