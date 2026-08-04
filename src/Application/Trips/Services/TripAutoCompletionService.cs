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

namespace TrackHub.TripManagement.Application.Trips.Services;

/// <summary>
/// Auto-completion (spec 11a §5.2). It goes through the SAME writer funnel and the SAME idempotency
/// key as a dispatcher's Complete, so the two can race and exactly one closes the trip — automation
/// gets no private back door into the transition matrix (acceptance 11).
/// </summary>
public sealed class TripAutoCompletionService(
    IAccountFeatureReader accountFeatureReader,
    ITripDetectionReader detectionReader,
    ITripWriter tripWriter,
    IAlertEmitter alertEmitter,
    ILogger<TripAutoCompletionService> logger) : ITripAutoCompletionService
{
    public async Task<bool> TryCompleteAsync(
        Guid accountId,
        Guid tripId,
        DateTimeOffset evaluatedAt,
        int finalStopCompletionMinutes,
        CancellationToken cancellationToken)
    {
        var candidate = await detectionReader.GetCompletionStateAsync(accountId, tripId, cancellationToken);

        if (candidate is not { } trip
            || !string.Equals(trip.Status, TripStatuses.InProgress, StringComparison.Ordinal)
            || trip.Stops.Count == 0)
        {
            // A trip with no stops has no measurable end. It stays open for a dispatcher to close —
            // inventing a completion instant for it would be a guess, not a measurement.
            return false;
        }

        var (endsAt, forced) = Resolve(trip, evaluatedAt, finalStopCompletionMinutes);
        if (endsAt is not { } measuredEnd)
        {
            return false;
        }

        // The same key manual Complete uses, so the two paths converge on one event and one alert.
        var completed = await tripWriter.TransitionTripAsync(
            tripId,
            accountId,
            TripStatuses.Completed,
            TripEventTypes.TripCompleted,
            TripEventSources.Detection,
            $"trip-complete:{tripId:N}",
            null,
            reason: null,
            force: forced,
            measuredAt: measuredEnd,
            cancellationToken);

        if (!completed)
        {
            return false;
        }

        try
        {
            await alertEmitter.EmitAsync(
                TripEventTypes.TripCompleted,
                TripAlertSeverities.Info,
                $"trip-{TripEventTypes.TripCompleted.ToLowerInvariant()}:{tripId:N}",
                new TripAlertDto(accountId, tripId, null, trip.Code, trip.TransporterId, trip.DriverId, null,
                    measuredEnd, null, null, null, null, null),
                cancellationToken);
        }
        catch (Exception ex)
        {
            // Best-effort and isolated, exactly as on the manual path: the trip IS completed and a
            // Manager outage must not undo a measurement.
            logger.LogError(ex, "Failed to emit TripCompleted alert for trip {TripId}", tripId);
        }

        return true;
    }

    public async Task<int> SweepAsync(CancellationToken cancellationToken)
    {
        var completed = 0;
        var accountIds = await accountFeatureReader.GetEnabledAccountIdsAsync(FeatureKeys.TripManagement, cancellationToken);

        foreach (var accountId in accountIds)
        {
            var config = await accountFeatureReader.GetAccountConfigAsync(accountId, cancellationToken);

            // The kill switch is honoured here too: an account running the manual flow must not have
            // its trips closed behind the dispatcher's back.
            if (!config.AutoLifecycle)
            {
                continue;
            }

            var candidates = await detectionReader.GetCompletionCandidatesAsync(accountId, cancellationToken);

            foreach (var tripId in candidates)
            {
                try
                {
                    if (await TryCompleteAsync(accountId, tripId, DateTimeOffset.UtcNow, config.FinalStopCompletionMinutes, cancellationToken))
                    {
                        completed++;
                    }
                }
                catch (Exception ex)
                {
                    logger.LogError(ex, "Auto-completion failed for trip {TripId}; it will be retried next cycle", tripId);
                }
            }
        }

        return completed;
    }

    /// <summary>
    /// The measured end, and whether closing needs <c>force</c>.
    /// <para>
    /// The dwell branch closes a trip whose final stop is still <c>Arrived</c>, which the writer's
    /// open-stop guard would otherwise reject — so it passes <c>force</c> deliberately, and only it
    /// does. The all-closed branch has nothing to force.
    /// </para>
    /// </summary>
    private static (DateTimeOffset? EndsAt, bool Forced) Resolve(
        TripCompletionCandidateVm trip, DateTimeOffset evaluatedAt, int finalStopCompletionMinutes)
    {
        if (trip.Stops.All(s => TripStopStatuses.IsClosed(s.Status)))
        {
            // The LAST measured departure, not the latest sequence: a resequenced route can close its
            // highest-numbered stop first, and the trip ended when the vehicle actually left.
            // A route of nothing but skipped stops has no departure at all — the evaluation instant
            // is then the only honest answer.
            var lastDeparture = trip.Stops
                .Where(s => s.ActualDepartureAt.HasValue)
                .Max(s => s.ActualDepartureAt);

            return (lastDeparture ?? evaluatedAt, false);
        }

        var final = trip.Stops.OrderBy(s => s.Sequence).Last();

        var othersClosed = trip.Stops
            .Where(s => s.TripStopId != final.TripStopId)
            .All(s => TripStopStatuses.IsClosed(s.Status));

        if (!othersClosed
            || !string.Equals(final.Status, TripStopStatuses.Arrived, StringComparison.Ordinal)
            || final.ActualArrivalAt is not { } arrivedAt
            || evaluatedAt - arrivedAt < TimeSpan.FromMinutes(finalStopCompletionMinutes))
        {
            return (null, false);
        }

        // The vehicle is already outside the final stop's zone and only the exit debounce is holding
        // the departure back. This branch's whole claim — "it parked here and will never depart" —
        // is contradicted by evidence we are holding in this very fix, so the departure wins.
        //
        // Without this the two rules RACE, and the dwell rule wins every time the dwell outruns
        // `finalStopCompletionMinutes` — which an ordinary 45-minute unload does. The trip closed
        // mid-unload, stamped ActualEndAt with the ARRIVAL instant, and then dropped out of the
        // working set, so the real departure was never recorded at all: every such trip lost its
        // entire final dwell and under-reported its own duration.
        if (final.OutsideSinceAt is not null)
        {
            return (null, false);
        }

        return (arrivedAt, true);
    }
}
