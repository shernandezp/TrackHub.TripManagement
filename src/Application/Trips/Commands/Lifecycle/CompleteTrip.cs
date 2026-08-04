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

namespace TrackHub.TripManagement.Application.Trips.Commands.Lifecycle;

/// <summary>
/// <c>InProgress → Completed</c>. Every stop must be <c>Departed</c> or <c>Skipped</c> first,
/// unless <paramref name="Force"/> is passed — a forced completion is a deliberate operator
/// override, so it is recorded on the timeline as forced and audited (spec 11 §7.3, acceptance 26).
/// </summary>
[Authorize(Resource = Resources.Trips, Action = Actions.Edit)]
[RequireFeature(FeatureKeys.TripManagement)]
// Enforcement: the handler derives the caller's own account and passes it to the reader/writer,
// which filters every row on it (TripVisibility is the single visibility resolver - spec 11).
[AccountScopeEnforcedInHandler]
public readonly record struct CompleteTripCommand(Guid TripId, bool Force) : IRequest;

public sealed class CompleteTripCommandHandler(
    ITripWriter writer,
    ITripReader reader,
    IAlertEmitter alertEmitter,
    IUserReader userReader,
    IUser user,
    ILogger<CompleteTripCommandHandler> logger) : IRequestHandler<CompleteTripCommand>
{
    private Guid UserId { get; } = TripVisibility.RequireUserId(user);

    public async Task Handle(CompleteTripCommand request, CancellationToken cancellationToken)
    {
        var caller = await userReader.GetUserAsync(UserId, cancellationToken);

        if (!request.Force)
        {
            var detail = await reader.GetTripDetailAsync(request.TripId, caller.AccountId, TripVisibility.ResolveScopeUserId(user, UserId), cancellationToken);
            foreach (var stop in detail.Stops)
            {
                if (!TripStopStatuses.IsClosed(stop.Status))
                    throw TripValidationFailure.Create(nameof(TripStopVm.Status), TripErrorCodes.StopsNotComplete);
            }
        }

        await TripLifecycleTransition.ExecuteAsync(
            reader, writer, alertEmitter, logger,
            request.TripId, caller.AccountId, TripVisibility.ResolveScopeUserId(user, UserId),
            TripStatuses.Completed, TripEventTypes.TripCompleted, TripAlertSeverities.Info,
            TripEventSources.Portal,
            reason: request.Force ? "forced" : null, force: request.Force,
            $"trip-complete:{request.TripId:N}",
            measuredAt: null,
            cancellationToken);
    }
}

public sealed class CompleteTripValidator : AbstractValidator<CompleteTripCommand>
{
    public CompleteTripValidator()
        => RuleFor(v => v.TripId).NotEmpty();
}
