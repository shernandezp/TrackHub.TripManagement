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
/// Aborts a trip that started but cannot finish (breakdown, refusal, incident). Reachable only
/// from <c>InProgress</c> or <c>Paused</c> — a trip that never started is cancelled, not aborted.
/// </summary>
[Authorize(Resource = Resources.Trips, Action = Actions.Edit)]
[RequireFeature(FeatureKeys.TripManagement)]
// Enforcement: the handler derives the caller's own account and passes it to the reader/writer,
// which filters every row on it (TripVisibility is the single visibility resolver - spec 11).
[AccountScopeEnforcedInHandler]
public readonly record struct AbortTripCommand(Guid TripId, string Reason) : IRequest;

public sealed class AbortTripCommandHandler(
    ITripWriter writer,
    ITripReader reader,
    IAlertEmitter alertEmitter,
    IUserReader userReader,
    IUser user,
    ILogger<AbortTripCommandHandler> logger) : IRequestHandler<AbortTripCommand>
{
    private Guid UserId { get; } = TripVisibility.RequireUserId(user);

    public async Task Handle(AbortTripCommand request, CancellationToken cancellationToken)
    {
        var caller = await userReader.GetUserAsync(UserId, cancellationToken);
        await TripLifecycleTransition.ExecuteAsync(
            reader, writer, alertEmitter, logger,
            request.TripId, caller.AccountId, TripVisibility.ResolveScopeUserId(user, UserId),
            TripStatuses.Aborted, TripEventTypes.TripAborted, alertSeverity: null,
            TripEventSources.Portal,
            reason: request.Reason, force: false,
            $"trip-abort:{request.TripId:N}",
            measuredAt: null,
            cancellationToken);
    }
}

public sealed class AbortTripValidator : AbstractValidator<AbortTripCommand>
{
    public AbortTripValidator()
    {
        RuleFor(v => v.TripId).NotEmpty();
        RuleFor(v => v.Reason).NotEmpty().MaximumLength(500);
    }
}
