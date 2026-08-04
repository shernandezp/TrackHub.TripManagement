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
using TrackHub.TripManagement.Application.Trips.Services.Interfaces;

namespace TrackHub.TripManagement.Application.TripStops.Commands.Progress;

/// <summary>
/// Dispatcher-side manual arrival, the override for weak GPS, indoor docks and devices that were
/// off. Idempotent on <c>trip-arrive:{tripStopId:N}:{clientEventId:N}</c>: a duplicate submission
/// returns success and creates no second row (acceptance 15) — the guarantee spec 10's offline
/// outbox is built on.
/// <para>
/// This spec declares no <c>PrincipalTypes = "Driver"</c> operation (acceptance 6). Spec 10 widens
/// this command to drivers by adding <c>Driver</c> to the attribute below plus an assignment check
/// and <c>[RequireFeature(FeatureKeys.DriverMobile)]</c> — additively, one line, no fork.
/// </para>
/// </summary>
[Authorize(Resource = Resources.Trips, Action = Actions.Edit)]
[RequireFeature(FeatureKeys.TripManagement)]
// Enforcement: the handler derives the caller's own account and passes it to the reader/writer,
// which filters every row on it (TripVisibility is the single visibility resolver - spec 11).
[AccountScopeEnforcedInHandler]
public readonly record struct RecordStopArrivalCommand(
    Guid TripId,
    Guid TripStopId,
    DateTimeOffset OccurredAt,
    double? Latitude,
    double? Longitude,
    Guid ClientEventId) : IRequest<bool>;

public sealed class RecordStopArrivalCommandHandler(
    ITripStopWriter stopWriter,
    ITripEventWriter tripEventWriter,
    ITripReader reader,
    IAlertEmitter alertEmitter,
    ITripAutoCompletionService autoCompletion,
    IAccountFeatureReader accountFeatureReader,
    IUserReader userReader,
    IUser user,
    ILogger<RecordStopArrivalCommandHandler> logger) : IRequestHandler<RecordStopArrivalCommand, bool>
{
    private Guid UserId { get; } = TripVisibility.RequireUserId(user);

    public async Task<bool> Handle(RecordStopArrivalCommand request, CancellationToken cancellationToken)
    {
        var caller = await userReader.GetUserAsync(UserId, cancellationToken);
        return await TripStopProgress.ExecuteAsync(
            reader, stopWriter, tripEventWriter, alertEmitter, autoCompletion, accountFeatureReader, logger,
            request.TripId, request.TripStopId, caller.AccountId, TripVisibility.ResolveScopeUserId(user, UserId),
            TripStopStatuses.Arrived, TripEventTypes.TripStopArrived, TripAlertSeverities.Info,
            request.OccurredAt, request.Latitude, request.Longitude,
            $"trip-arrive:{request.TripStopId:N}:{request.ClientEventId:N}",
            reason: null,
            cancellationToken);
    }
}

public sealed class RecordStopArrivalValidator : AbstractValidator<RecordStopArrivalCommand>
{
    public RecordStopArrivalValidator()
    {
        RuleFor(v => v.TripId).NotEmpty();
        RuleFor(v => v.TripStopId).NotEmpty();
        RuleFor(v => v.ClientEventId).NotEmpty();
        RuleFor(v => v.OccurredAt).NotEqual(default(DateTimeOffset));
        RuleFor(v => v.Latitude).InclusiveBetween(-90d, 90d).When(v => v.Latitude.HasValue);
        RuleFor(v => v.Longitude).InclusiveBetween(-180d, 180d).When(v => v.Longitude.HasValue);
    }
}
