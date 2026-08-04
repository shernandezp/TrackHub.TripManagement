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
using TrackHub.TripManagement.Application.Trips.Commands.Lifecycle;
using TrackHub.TripManagement.Application.Trips.Services.Interfaces;

namespace TrackHub.TripManagement.Application.Trips.Services;

/// <summary>See <see cref="ITripStartBackfillService"/>.</summary>
public sealed class TripStartBackfillService(
    ITripReader reader,
    ITripWriter writer,
    ITripStopWriter stopWriter,
    IGeofenceVisitReader visitReader,
    IAccountFeatureReader accountFeatureReader,
    IAlertEmitter alertEmitter,
    ILogger<TripStartBackfillService> logger) : ITripStartBackfillService
{
    public async Task<TripStartBackfillResultVm> ApplyAsync(
        Guid tripId,
        Guid accountId,
        Guid? scopeUserId,
        DateTimeOffset? declaredStartAt,
        CancellationToken cancellationToken)
    {
        var detail = await reader.GetTripDetailAsync(tripId, accountId, scopeUserId, cancellationToken);

        // Already running (a second submit, a retried CSV row): nothing to declare. Reported as
        // "not started by this call" rather than as a conflict — re-declaring is harmless.
        if (!string.Equals(detail.Trip.Status, TripStatuses.Created, StringComparison.Ordinal))
        {
            return new TripStartBackfillResultVm(false, false, detail.Trip.ActualStartAt, 0);
        }

        // Arm first: replaying a visit only makes sense against frozen geometry, and it is also what
        // lets live detection pick the trip up from here without a further cycle of setup.
        await writer.ArmTripAsync(tripId, accountId, cancellationToken);

        var config = await accountFeatureReader.GetAccountConfigAsync(accountId, cancellationToken);
        var since = DateTimeOffset.UtcNow.AddHours(-config.BackfillLookbackHours);

        var originVisit = await FindOriginVisitAsync(detail, accountId, since, cancellationToken);

        DateTimeOffset startedAt;
        string source;

        if (originVisit is { } visit)
        {
            await writer.SetOriginVisitAsync(tripId, accountId, visit.EnteredAt, visit.DepartedAt, cancellationToken);
            startedAt = visit.EnteredAt;
            source = TripEventSources.Detection;
        }
        else if (declaredStartAt is { } declared)
        {
            // No evidence: the declared instant becomes the origin DEPARTURE, not the arrival.
            // OriginArrivedAt stays null on purpose — loading was never measured, and a fabricated
            // arrival would put an invented loading duration into the reports.
            await writer.SetOriginVisitAsync(tripId, accountId, null, declared, cancellationToken);
            startedAt = declared;
            source = TripEventSources.Portal;
        }
        else
        {
            throw TripValidationFailure.Create(nameof(TripVm.ActualStartAt), TripErrorCodes.StartEvidenceRequired);
        }

        var replayed = originVisit is { DepartedAt: not null } departed
            ? await ReplayStopsAsync(detail, accountId, departed.DepartedAt!.Value, since, cancellationToken)
            : 0;

        // The normal funnel, the normal idempotency key: live detection reaching the same trip a
        // moment later finds the start already recorded and does nothing.
        await TripLifecycleTransition.ExecuteAsync(
            reader, writer, alertEmitter, logger,
            tripId, accountId, scopeUserId,
            TripStatuses.InProgress, TripEventTypes.TripStarted, TripAlertSeverities.Info,
            source,
            reason: null, force: false,
            $"trip-start:{tripId:N}",
            measuredAt: startedAt,
            cancellationToken);

        return new TripStartBackfillResultVm(true, originVisit is not null, startedAt, replayed);
    }

    /// <summary>
    /// The vehicle's most recent COMPLETED visit to the trip's origin geofence inside the lookback
    /// window. Completed, because an open visit means the truck is still standing at the origin —
    /// which is not a trip already in transit, it is a trip live detection is about to start on its
    /// own.
    /// </summary>
    private async Task<GeofenceVisitVm?> FindOriginVisitAsync(
        TripDetailVm detail, Guid accountId, DateTimeOffset since, CancellationToken cancellationToken)
    {
        if (detail.Trip.OriginGeofenceId is not { } originGeofenceId)
        {
            // A POI or ad-hoc point origin leaves no visit trail: Geofencing only records zones it
            // owns. The declared start is the only honest source there.
            return null;
        }

        var visits = await visitReader.GetVisitsAsync(
            accountId, detail.Trip.TransporterId, [originGeofenceId], since, cancellationToken);

        return visits
            .Where(v => v.DepartedAt.HasValue)
            .OrderByDescending(v => v.EnteredAt)
            .Select(v => (GeofenceVisitVm?)v)
            .FirstOrDefault();
    }

    /// <summary>
    /// Replays the stop visits that happened after the vehicle left the origin, in sequence order.
    /// <para>
    /// Each stop is matched to the FIRST visit that starts at or after the previous stop's departure,
    /// so the replay follows the planned order rather than picking whichever visit happens to be
    /// closest. A stop with no geofence, or no visit after its predecessor, is simply left Pending —
    /// live detection carries on from there.
    /// </para>
    /// <para>
    /// No alerts. These are recorded facts being written down hours late; paging a dispatcher about
    /// a delivery that already happened is noise, and the timeline carries the truth either way.
    /// </para>
    /// </summary>
    private async Task<int> ReplayStopsAsync(
        TripDetailVm detail,
        Guid accountId,
        DateTimeOffset originDepartedAt,
        DateTimeOffset since,
        CancellationToken cancellationToken)
    {
        var geofenceIds = detail.Stops
            .Where(s => s.GeofenceId.HasValue)
            .Select(s => s.GeofenceId!.Value)
            .Distinct()
            .ToList();

        if (geofenceIds.Count == 0)
        {
            return 0;
        }

        var visits = await visitReader.GetVisitsAsync(
            accountId, detail.Trip.TransporterId, geofenceIds, since, cancellationToken);

        if (visits.Count == 0)
        {
            return 0;
        }

        var replayed = 0;
        var cursor = originDepartedAt;

        foreach (var stop in detail.Stops.OrderBy(s => s.Sequence))
        {
            if (stop.GeofenceId is not { } geofenceId)
            {
                continue;
            }

            var visit = visits
                .Where(v => v.GeofenceId == geofenceId && v.EnteredAt >= cursor)
                .OrderBy(v => v.EnteredAt)
                .Select(v => (GeofenceVisitVm?)v)
                .FirstOrDefault();

            if (visit is not { } matched)
            {
                continue;
            }

            // The SAME idempotency keys live detection uses, so a fix arriving for this stop a
            // moment later is recognised as the event already recorded rather than a second one.
            var arrived = await stopWriter.RecordStopProgressAsync(
                detail.Trip.TripId, stop.TripStopId, accountId, TripStopStatuses.Arrived, matched.EnteredAt,
                null, null, TripEventSources.Detection, $"trip-arrive:{stop.TripStopId:N}", null, cancellationToken);

            if (arrived)
            {
                replayed++;
            }

            if (matched.DepartedAt is not { } departedAt)
            {
                // Still inside: this is where the vehicle is now, so the replay stops here and live
                // detection takes over.
                break;
            }

            await stopWriter.RecordStopProgressAsync(
                detail.Trip.TripId, stop.TripStopId, accountId, TripStopStatuses.Departed, departedAt,
                null, null, TripEventSources.Detection, $"trip-depart:{stop.TripStopId:N}", null, cancellationToken);

            cursor = departedAt;
        }

        return replayed;
    }
}
