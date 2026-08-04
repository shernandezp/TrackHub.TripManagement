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
using TrackHub.TripManagement.Application.Trips.Services.Interfaces;

namespace TrackHub.TripManagement.Application.TripStops.Commands.Progress;

/// <summary>
/// The shared body of the three dispatcher-side progress overrides (arrive, depart, skip).
/// <para>
/// <b>Idempotency is server-side by design, not by caller discipline.</b> Every call builds
/// <c>TripEvent.IdempotencyKey = trip-{verb}:{tripStopId:N}:{clientEventId:N}</c>; a duplicate
/// submission returns success and writes no second row (acceptance 15). This is precisely what
/// makes spec 10's offline outbox safe to layer on later without reopening these handlers — the
/// server never assumes a client will refrain from retrying.
/// </para>
/// <para>
/// Manual override always beats automatic detection (spec 11 §18.13): both land in the same event
/// log, discriminated only by <c>Source</c>. Timestamps once written are never overwritten.
/// </para>
/// </summary>
public static class TripStopProgress
{
    public static async Task<bool> ExecuteAsync(
        ITripReader reader,
        ITripStopWriter stopWriter,
        ITripEventWriter tripEventWriter,
        IAlertEmitter alertEmitter,
        ITripAutoCompletionService autoCompletion,
        IAccountFeatureReader accountFeatureReader,
        ILogger logger,
        Guid tripId,
        Guid tripStopId,
        Guid accountId,
        Guid? scopeUserId,
        string toStatus,
        string eventType,
        string? alertSeverity,
        DateTimeOffset occurredAt,
        double? latitude,
        double? longitude,
        string idempotencyKey,
        string? reason,
        CancellationToken cancellationToken)
    {
        var trip = await reader.GetTripAsync(tripId, accountId, scopeUserId, cancellationToken);

        // IDEMPOTENCY BEFORE THE TRIP-STATUS GUARD, for exactly the reason RecordStopProgressAsync
        // checks it before the STOP-status guard: a state guard must never fire on an event the
        // server has already accepted (acceptance 15).
        //
        // The trip moves on. It auto-completes the moment its last stop closes (§5.2), and a
        // dispatcher can complete or abort it at any time. A device replaying an arrival the server
        // already recorded — spec 10's offline outbox doing precisely what it is designed to do —
        // then met TRIP_NOT_ACTIVE, a hard error it can only surface as a permanent failure and
        // retry forever. The event exists; the answer is yes.
        if (await tripEventWriter.HasEventAsync(accountId, idempotencyKey, cancellationToken))
            return true;

        // InProgress OR Paused. Pause is the dispatcher's "I am taking control" switch — it removes
        // the trip from detection — so rejecting manual progress on a paused trip closed the manual
        // hatch at exactly the moment it is the only way to record anything (spec 11a §5.5).
        if (!TripStatuses.HasStarted(trip.Status))
            throw TripValidationFailure.Create(nameof(TripVm.Status), TripErrorCodes.TripNotActive);

        // The trip id travels to the writer so the stop is resolved by (stop, account, TRIP): the
        // active-trip check above and the row actually written must concern the SAME trip.
        var recorded = await stopWriter.RecordStopProgressAsync(
            tripId,
            tripStopId,
            accountId,
            toStatus,
            occurredAt,
            latitude,
            longitude,
            TripEventSources.Portal,
            idempotencyKey,
            reason,
            cancellationToken);

        // A duplicate is a success with no side effects: no second row and no second alert.
        //
        // `recorded` is the WRITER's contract ("did this insert"), which the detection service needs
        // so it can count arrivals and skip a second alert. It is NOT this mutation's contract.
        // Returning it verbatim answered `false` to a duplicate, which a client cannot tell from a
        // failure — spec 10's offline outbox would keep the event queued and retry it forever.
        // Acceptance 15 says a duplicate submission RETURNS SUCCESS, so the caller can drop it.
        if (!recorded)
            return true;

        if (alertSeverity is not null)
        {
            try
            {
                await alertEmitter.EmitAsync(
                    eventType,
                    alertSeverity,
                    $"trip-{eventType.ToLowerInvariant()}:{tripStopId:N}",
                    new TripAlertDto(accountId, tripId, tripStopId, trip.Code, trip.TransporterId, trip.DriverId, null, occurredAt, null, null, null, latitude, longitude),
                    cancellationToken);
            }
            catch (Exception ex)
            {
                logger.LogError(ex, "Failed to emit {EventType} alert for stop {TripStopId}", eventType, tripStopId);
            }
        }

        await TryCompleteAsync(autoCompletion, accountFeatureReader, logger, accountId, tripId, trip.Status, occurredAt, cancellationToken);

        // Reached only when the event WAS recorded, so this is unconditionally a success.
        return true;
    }

    /// <summary>
    /// Closing the LAST open stop closes the trip, whoever closed it (spec 11a §5.2 lists the
    /// all-stops-closed rule as a trigger, not as a detection-only one).
    /// <para>
    /// Without this, a dispatcher who departs the final stop by hand watched the trip sit
    /// <c>InProgress</c> until the next position fix or the five-minute sweep caught up — which on a
    /// route whose tracker is dead, the exact case the manual override exists for, means five
    /// minutes of a board that contradicts what the dispatcher just recorded.
    /// </para>
    /// <para>
    /// <c>Paused</c> is excluded deliberately: pause suspends automation, and closing a trip behind
    /// the dispatcher's back is the one thing that switch is there to prevent. On a paused trip the
    /// override records the stop and stops there — the dispatcher completes it explicitly.
    /// </para>
    /// </summary>
    private static async Task TryCompleteAsync(
        ITripAutoCompletionService autoCompletion,
        IAccountFeatureReader accountFeatureReader,
        ILogger logger,
        Guid accountId,
        Guid tripId,
        string tripStatus,
        DateTimeOffset occurredAt,
        CancellationToken cancellationToken)
    {
        if (!string.Equals(tripStatus, TripStatuses.InProgress, StringComparison.Ordinal))
            return;

        try
        {
            var config = await accountFeatureReader.GetAccountConfigAsync(accountId, cancellationToken);
            if (!config.AutoLifecycle)
                return;

            await autoCompletion.TryCompleteAsync(accountId, tripId, occurredAt, config.FinalStopCompletionMinutes, cancellationToken);
        }
        catch (Exception ex)
        {
            // The stop progress is already recorded and returned as a success. Completion is a
            // follow-on convenience here — the sweep retries it — so it must never turn the
            // dispatcher's accepted override into an error.
            logger.LogError(ex, "Auto-completion after manual stop progress failed for trip {TripId}; the sweep will retry it", tripId);
        }
    }
}
