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
/// Dispatcher-side manual departure. Idempotent on
/// <c>trip-depart:{tripStopId:N}:{clientEventId:N}</c>: a duplicate submission returns success and
/// creates no second row (acceptance 15) — the guarantee spec 10's offline outbox is built on.
/// <para>
/// No <c>PrincipalTypes = "Driver"</c> here (acceptance 6); spec 10 widens the attribute additively.
/// </para>
/// </summary>
[Authorize(Resource = Resources.Trips, Action = Actions.Edit)]
[RequireFeature(FeatureKeys.TripManagement)]
// Enforcement: the handler derives the caller's own account and passes it to the reader/writer,
// which filters every row on it (TripVisibility is the single visibility resolver - spec 11).
[AccountScopeEnforcedInHandler]
public readonly record struct RecordStopDepartureCommand(
    Guid TripId,
    Guid TripStopId,
    DateTimeOffset OccurredAt,
    double? Latitude,
    double? Longitude,
    Guid ClientEventId) : IRequest<bool>;

public sealed class RecordStopDepartureCommandHandler(
    ITripStopWriter stopWriter,
    ITripEventWriter tripEventWriter,
    ITripReader reader,
    IAlertEmitter alertEmitter,
    ITripAutoCompletionService autoCompletion,
    IAccountFeatureReader accountFeatureReader,
    IUserReader userReader,
    IUser user,
    ILogger<RecordStopDepartureCommandHandler> logger) : IRequestHandler<RecordStopDepartureCommand, bool>
{
    private Guid UserId { get; } = TripVisibility.RequireUserId(user);

    public async Task<bool> Handle(RecordStopDepartureCommand request, CancellationToken cancellationToken)
    {
        var caller = await userReader.GetUserAsync(UserId, cancellationToken);
        return await TripStopProgress.ExecuteAsync(
            reader, stopWriter, tripEventWriter, alertEmitter, autoCompletion, accountFeatureReader, logger,
            request.TripId, request.TripStopId, caller.AccountId, TripVisibility.ResolveScopeUserId(user, UserId),
            TripStopStatuses.Departed, TripEventTypes.TripStopDeparted, TripAlertSeverities.Info,
            request.OccurredAt, request.Latitude, request.Longitude,
            $"trip-depart:{request.TripStopId:N}:{request.ClientEventId:N}",
            reason: null,
            cancellationToken);
    }
}

public sealed class RecordStopDepartureValidator : AbstractValidator<RecordStopDepartureCommand>
{
    public RecordStopDepartureValidator()
    {
        RuleFor(v => v.TripId).NotEmpty();
        RuleFor(v => v.TripStopId).NotEmpty();
        RuleFor(v => v.ClientEventId).NotEmpty();
        RuleFor(v => v.OccurredAt).NotEqual(default(DateTimeOffset));
        RuleFor(v => v.Latitude).InclusiveBetween(-90d, 90d).When(v => v.Latitude.HasValue);
        RuleFor(v => v.Longitude).InclusiveBetween(-180d, 180d).When(v => v.Longitude.HasValue);
    }
}
