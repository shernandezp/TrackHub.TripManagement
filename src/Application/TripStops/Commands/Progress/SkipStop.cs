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
/// Marks a stop <c>Skipped</c> with a reason (customer closed, refused, no access). Idempotent on
/// <c>trip-skip:{tripStopId:N}:{clientEventId:N}</c> (acceptance 15).
/// <para>
/// No <c>PrincipalTypes = "Driver"</c> here (acceptance 6); spec 10 widens the attribute additively.
/// </para>
/// </summary>
[Authorize(Resource = Resources.Trips, Action = Actions.Edit)]
[RequireFeature(FeatureKeys.TripManagement)]
// Enforcement: the handler derives the caller's own account and passes it to the reader/writer,
// which filters every row on it (TripVisibility is the single visibility resolver - spec 11).
[AccountScopeEnforcedInHandler]
public readonly record struct SkipStopCommand(
    Guid TripId,
    Guid TripStopId,
    DateTimeOffset OccurredAt,
    string Reason,
    Guid ClientEventId) : IRequest<bool>;

public sealed class SkipStopCommandHandler(
    ITripStopWriter stopWriter,
    ITripEventWriter tripEventWriter,
    ITripReader reader,
    IAlertEmitter alertEmitter,
    ITripAutoCompletionService autoCompletion,
    IAccountFeatureReader accountFeatureReader,
    IUserReader userReader,
    IUser user,
    ILogger<SkipStopCommandHandler> logger) : IRequestHandler<SkipStopCommand, bool>
{
    private Guid UserId { get; } = TripVisibility.RequireUserId(user);

    public async Task<bool> Handle(SkipStopCommand request, CancellationToken cancellationToken)
    {
        var caller = await userReader.GetUserAsync(UserId, cancellationToken);
        return await TripStopProgress.ExecuteAsync(
            reader, stopWriter, tripEventWriter, alertEmitter, autoCompletion, accountFeatureReader, logger,
            request.TripId, request.TripStopId, caller.AccountId, TripVisibility.ResolveScopeUserId(user, UserId),
            TripStopStatuses.Skipped, TripEventTypes.TripStopSkipped, alertSeverity: null,
            request.OccurredAt, latitude: null, longitude: null,
            $"trip-skip:{request.TripStopId:N}:{request.ClientEventId:N}",
            request.Reason,
            cancellationToken);
    }
}

public sealed class SkipStopValidator : AbstractValidator<SkipStopCommand>
{
    public SkipStopValidator()
    {
        RuleFor(v => v.TripId).NotEmpty();
        RuleFor(v => v.TripStopId).NotEmpty();
        RuleFor(v => v.ClientEventId).NotEmpty();
        RuleFor(v => v.Reason).NotEmpty().MaximumLength(500);
        RuleFor(v => v.OccurredAt).NotEqual(default(DateTimeOffset));
    }
}
