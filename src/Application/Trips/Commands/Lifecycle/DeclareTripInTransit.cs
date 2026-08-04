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
using TrackHub.TripManagement.Application.Common;
using TrackHub.TripManagement.Application.Trips.Services.Interfaces;

namespace TrackHub.TripManagement.Application.Trips.Commands.Lifecycle;

/// <summary>
/// "This trip is already under way." The planning step for a trip whose vehicle left before anyone
/// wrote the trip down — bulk uploads on Monday for trucks that rolled on Sunday, and the ordinary
/// case of a dispatcher recording something in progress (spec 11a §5.4).
/// <para>
/// It is a separate verb rather than a flag on create because backfill replays the ROUTE, and the
/// route only exists once the stops have been added. Every planning input calls it last:
/// the portal after its destinations land, the CSV import after each row's stops, the partner
/// import when a row carries <c>startedAt</c>.
/// </para>
/// <para>
/// <paramref name="StartedAt"/> is the fallback, not the input: when Geofencing recorded the
/// vehicle's departure from the origin zone, those measurements win and the declared time is
/// ignored. It is REQUIRED only when there is no such evidence — and then it is honestly labelled
/// <c>Portal</c> on the timeline.
/// </para>
/// </summary>
[Authorize(Resource = Resources.Trips, Action = Actions.Edit)]
[RequireFeature(FeatureKeys.TripManagement)]
// Enforcement: the handler derives the caller's own account and passes it to the reader/writer,
// which filters every row on it (TripVisibility is the single visibility resolver - spec 11).
[AccountScopeEnforcedInHandler]
public readonly record struct DeclareTripInTransitCommand(Guid TripId, DateTimeOffset? StartedAt)
    : IRequest<TripStartBackfillResultVm>;

public sealed class DeclareTripInTransitCommandHandler(
    ITripStartBackfillService backfillService,
    IUserReader userReader,
    IUser user) : IRequestHandler<DeclareTripInTransitCommand, TripStartBackfillResultVm>
{
    private Guid UserId { get; } = TripVisibility.RequireUserId(user);

    public async Task<TripStartBackfillResultVm> Handle(DeclareTripInTransitCommand request, CancellationToken cancellationToken)
    {
        var caller = await userReader.GetUserAsync(UserId, cancellationToken);

        return await backfillService.ApplyAsync(
            request.TripId,
            caller.AccountId,
            TripVisibility.ResolveScopeUserId(user, UserId),
            request.StartedAt,
            cancellationToken);
    }
}

public sealed class DeclareTripInTransitValidator : AbstractValidator<DeclareTripInTransitCommand>
{
    public DeclareTripInTransitValidator()
    {
        RuleFor(v => v.TripId).NotEmpty();

        // A start in the future is not a trip already under way; it is a planning mistake, and
        // accepting it would stamp an ActualStartAt the vehicle cannot have reached yet.
        RuleFor(v => v.StartedAt)
            .LessThanOrEqualTo(_ => DateTimeOffset.UtcNow)
            .When(v => v.StartedAt.HasValue);
    }
}
