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
using TrackHub.TripManagement.Application.TripEvents.Services.Interfaces;
using TrackHub.TripManagement.Application.Trips.Services.Interfaces;

namespace TrackHub.TripManagement.Application.TripEvents.Services;

/// <summary>
/// The zero-touch lifecycle, measured from the pushed position feed: arming, auto-start at the
/// origin, origin departure, stop arrivals and departures, corridor deviation and auto-completion
/// (spec 11a §6).
/// <para>
/// Detection is an <b>assist, not the record of truth</b> (spec 11 §18.13): weak GPS, indoor docks
/// and devices that were off are field reality, so a manual override always wins and both land in
/// the same idempotent event log discriminated by <c>Source</c>. What changed with zero-touch is
/// which side does the ordinary work — automation runs the lifecycle, the dispatcher overrides it.
/// </para>
/// <para>
/// Alert emission and job recording are best-effort and isolated — a Manager outage logs and never
/// fails position processing, because the Router batch that fed us must not flip to FAILED over a
/// downstream notification problem.
/// </para>
/// <para>
/// <b>All state that spans fixes is PERSISTED</b> (<c>Trip.OriginOutsideSinceAt</c>,
/// <c>TripStop.OutsideSinceAt</c>, <c>Trip.ConsecutiveOutsideFixes</c>,
/// <c>Trip.DeviationOpenedAt</c>). Router calls this with exactly one position per transporter, so a
/// debounce clock or run length held in memory is rebuilt from scratch every call and can never
/// mature. <see cref="TripDetectionState"/> remains a within-batch cache in front of those columns.
/// </para>
/// </summary>
public sealed class TripDetectionService(
    ITripDetectionReader detectionReader,
    ITripDetectionUnitOfWork unitOfWork,
    IAccountFeatureReader accountFeatureReader,
    ITripWriter tripWriter,
    ITripStopWriter stopWriter,
    ITripEventWriter tripEventWriter,
    ITripAutoCompletionService autoCompletionService,
    IAlertEmitter alertEmitter,
    ILogger<TripDetectionService> logger) : ITripDetectionService
{
    /// <summary>
    /// Same debounce as geofence exit, and the same one the origin uses: a zone is not "left" until
    /// the vehicle has been outside it for this long.
    /// </summary>
    private static readonly TimeSpan DepartureDebounce = TimeSpan.FromSeconds(30);

    /// <summary>Consecutive out-of-corridor fixes before an episode opens. Three, so one bad fix is not a deviation.</summary>
    private const int DeviationFixThreshold = 3;

    public async Task<TripProcessingResultVm> ProcessPositionsAsync(
        IEnumerable<TransporterPositionDto> positions,
        Guid accountId,
        CancellationToken cancellationToken)
    {
        var ordered = positions
            .OrderBy(p => p.TransporterId)
            .ThenBy(p => p.DeviceDateTime)
            .ToList();

        if (ordered.Count == 0)
            return new TripProcessingResultVm(0, 0, 0, 0);

        var config = await accountFeatureReader.GetAccountConfigAsync(accountId, cancellationToken);

        // With autoLifecycle off the working set is exactly what it was before zero-touch — only
        // InProgress trips — so an account without reliable GPS runs the manual flow untouched (§8).
        DateTimeOffset? armableUntil = config.AutoLifecycle
            ? DateTimeOffset.UtcNow.AddMinutes(config.ActivationLeadMinutes)
            : null;

        var transporterIds = ordered.Select(p => p.TransporterId).Distinct().ToList();
        var openTrips = await unitOfWork.LoadAsync(accountId, transporterIds, armableUntil, cancellationToken);
        if (openTrips.Count == 0)
            return new TripProcessingResultVm(ordered.Count, 0, 0, 0);

        var states = openTrips.ToDictionary(t => t.TripId, t => new TripDetectionState(t));
        var tripsByTransporter = openTrips
            .GroupBy(t => t.TransporterId)
            .ToDictionary(g => g.Key, g => g.Select(t => t.TripId).ToList());

        var arrived = 0;
        var departed = 0;
        var deviations = 0;

        foreach (var position in ordered)
        {
            if (!tripsByTransporter.TryGetValue(position.TransporterId, out var tripIds))
                continue;

            foreach (var tripId in tripIds)
            {
                var state = states[tripId];

                // ONE commit per (fix, trip) — or none.
                //
                // Everything the pipeline buffers for this fix — the odometer, both debounce clocks,
                // the deviation run length, the arming snapshot — lands together or not at all. It
                // used to be five separate saves, which is not just five round trips: a process that
                // died between them left the odometer advanced and the departure unrecorded, with
                // nothing to say which half had happened (§6).
                //
                // An event-producing write inside the fix (a transition, a stop arrival) commits the
                // buffer along with itself, which is exactly right — the start and the origin arrival
                // that caused it belong in the same transaction. This flush then finds nothing left.
                //
                // On failure the buffer is DISCARDED, not flushed. Flushing from a `finally` was the
                // obvious shape and the wrong one twice over: it publishes the half-applied fix this
                // unit exists to prevent, and a save that fails while unwinding replaces the original
                // exception with its own, hiding what actually went wrong.
                //
                // One bad trip does not stop the fleet. Router treats the whole batch as best-effort,
                // so a trip that throws is dropped with its buffer and re-read next cycle while the
                // other vehicles in the same batch carry on.
                try
                {
                    await ProcessFixAsync(state, position, config, accountId, cancellationToken);
                    await unitOfWork.FlushAsync(cancellationToken);
                }
                catch (Exception ex)
                {
                    logger.LogError(
                        ex, "Fix processing failed for trip {TripId}; the buffered state is dropped and re-read next cycle", tripId);

                    unitOfWork.Discard(tripId);
                }
            }
        }

        return new TripProcessingResultVm(ordered.Count, arrived, departed, deviations);

        async Task ProcessFixAsync(
            TripDetectionState state,
            TransporterPositionDto position,
            TripAccountConfigVm config,
            Guid accountId,
            CancellationToken cancellationToken)
        {
            // 1-3. Arm, then auto-start — BEFORE the odometer. The progress writer ignores
            //      anything that is not InProgress, so a trip sitting in the queue accrues no
            //      distance baseline, no LastPoint and no LastPositionAt: the very hazard spec
            //      11 §10 refused to accept cannot occur here (spec 11a §2).
            //
            //      Arming comes FIRST because it is what creates the geometry the containment
            //      test needs. Asking before arming answered "not at the origin" for every trip
            //      on the fix that armed it, costing a whole cycle of latency for a truck that
            //      was already standing at the gate.
            if (state.IsCreated)
            {
                await ArmAsync(state, cancellationToken);
            }

            // Origin containment is resolved ONCE per (fix, trip) and reused by the start check,
            // the departure debounce and the arrival suppression below — three questions, one
            // PostGIS round trip.
            //
            // And only while one of those three still cares. Every consumer is gated on the trip
            // not having left its origin yet, so once OriginDepartedAt is stamped the answer is
            // read by nobody — asking anyway spent a spatial query per fix per trip for the
            // entire rest of the run, which on a large fleet is most of the pipeline's cost.
            state.InsideOrigin = state.HasOriginGeom
                && state.OriginDepartedAt is null
                && unitOfWork.IsInsideOrigin(state.TripId, position.Latitude, position.Longitude);

            if (state.IsCreated)
            {
                await TryAutoStartAsync(state, accountId, position, cancellationToken);
            }

            // Everything below measures a RUNNING trip. Gating on the status rather than on
            // "did auto-start just fire" also covers the trip this batch already completed: a
            // caller that sends several fixes for one transporter would otherwise keep pushing
            // the odometer of a trip that is closed.
            if (!state.IsInProgress)
            {
                return;
            }

            // A rejected fix is out-of-order or a replay. Detection is skipped entirely for it
            // — see Trip.TryAdvanceProgress: without this, a redelivered out-of-corridor fix
            // advances the deviation counter every time it arrives.
            if (!UpdateProgress(state, position))
                return;

            await DetectOriginDepartureAsync(state, accountId, position, cancellationToken);
            arrived += await DetectArrivalsAsync(state, accountId, position, cancellationToken);
            departed += await DetectDeparturesAsync(state, accountId, position, cancellationToken);
            deviations += await DetectDeviationAsync(state, accountId, position, cancellationToken);

            if (config.AutoLifecycle && HasNothingLeftToVisit(state))
            {
                await TryCompleteAsync(state, accountId, position, config, cancellationToken);
            }
        }
    }

    // 1. Arming. Freezes the origin zone and every stop's arrival geometry and marks the trip as
    //    watched. Idempotent, event-free and reached at most once per trip: a trip armed and never
    //    run leaves no history and stays deletable (acceptance 16).
    private async Task ArmAsync(TripDetectionState state, CancellationToken cancellationToken)
    {
        if (state.ArmedAt.HasValue)
            return;

        try
        {
            if (!await unitOfWork.ArmAsync(state.TripId, cancellationToken))
                return;

            state.ArmedAt = DateTimeOffset.UtcNow;

            // The snapshot now exists, so the very next fix in this same batch can start the trip.
            state.HasOriginGeom = true;
        }
        catch (Exception ex)
        {
            // Arming is preparation, not a measurement: a failure here costs one cycle of latency
            // and must not take the whole position batch down with it.
            logger.LogError(ex, "Failed to arm trip {TripId}; it will be retried next cycle", state.TripId);
        }
    }

    // 2. Auto-start. The trigger is POSITIONAL — the vehicle standing in its origin zone — never a
    //    clock, which is the distinction spec 11a §2 draws when it reverses spec 11 §10 for this
    //    path only. OriginArrivedAt is stamped first so the transition can adopt it as the measured
    //    ActualStartAt rather than reaching for the server clock.
    //
    //    Leaves the trip's status on the state; the caller gates the rest of the pipeline on it.
    private async Task TryAutoStartAsync(
        TripDetectionState state, Guid accountId, TransporterPositionDto position, CancellationToken cancellationToken)
    {
        if (!state.InsideOrigin)
            return;

        unitOfWork.RecordOriginVisit(state.TripId, position.DeviceDateTime, null);
        state.OriginArrivedAt ??= position.DeviceDateTime;

        bool started;
        try
        {
            // The SAME idempotency key manual Start uses. That is what makes the two paths race
            // safely: whichever commits first wins, and the loser writes nothing and emits nothing.
            started = await tripWriter.TransitionTripAsync(
                state.TripId,
                accountId,
                TripStatuses.InProgress,
                TripEventTypes.TripStarted,
                TripEventSources.Detection,
                $"trip-start:{state.TripId:N}",
                null,
                reason: null,
                force: false,
                measuredAt: position.DeviceDateTime,
                cancellationToken);
        }
        catch (Exception ex)
        {
            // TRIP_TRANSPORTER_BUSY or a lost concurrency race: the vehicle is running something
            // else, so this trip simply waits its turn.
            logger.LogWarning(ex, "Auto-start rejected for trip {TripId}; it stays queued", state.TripId);
            return;
        }

        if (!started)
            return;

        state.Status = TripStatuses.InProgress;
        await EmitTripAlertAsync(state, accountId, TripEventTypes.TripStarted, position.DeviceDateTime, cancellationToken);
    }

    // 1. Odometer and last-seen point. Distance accumulates from the previous fix, so a trip's
    //    actual distance is measured, never inferred from the plan.
    private bool UpdateProgress(TripDetectionState state, TransporterPositionDto position)
    {
        var added = 0d;
        if (state.LastLatitude is { } lastLat && state.LastLongitude is { } lastLng)
            added = GeoDistance.HaversineMeters(lastLat, lastLng, position.Latitude, position.Longitude);

        var accepted = unitOfWork.TryAdvanceProgress(
            state.TripId, position.Latitude, position.Longitude, position.DeviceDateTime, added);

        if (!accepted)
            return false;

        state.LastLatitude = position.Latitude;
        state.LastLongitude = position.Longitude;

        return true;
    }

    // 4. Origin departure. The same persisted-clock debounce the stops use: one bad fix bouncing off
    //    the edge of the zone must not end a loading window the truck is still sitting in.
    //
    //    This is the boundary between the two measured phases — everything before it is loading,
    //    everything after it is transit (spec 11a §4.3).
    private async Task DetectOriginDepartureAsync(
        TripDetectionState state, Guid accountId, TransporterPositionDto position, CancellationToken cancellationToken)
    {
        if (!state.HasOriginGeom || state.OriginDepartedAt.HasValue)
            return;

        if (state.InsideOrigin)
        {
            // Back inside: the debounce restarts from zero, in the database as well as here.
            if (state.OriginOutsideSince is not null)
            {
                state.OriginOutsideSince = null;
                unitOfWork.SetOriginOutsideSince(state.TripId, null);
            }

            return;
        }

        if (state.OriginOutsideSince is not { } outsideSince)
        {
            state.OriginOutsideSince = position.DeviceDateTime;
            unitOfWork.SetOriginOutsideSince(state.TripId, position.DeviceDateTime);
            return;
        }

        if (position.DeviceDateTime - outsideSince < DepartureDebounce)
            return;

        if (!unitOfWork.TryRecordOriginDeparture(state.TripId, position.DeviceDateTime))
            return;

        state.OriginDepartedAt = position.DeviceDateTime;
        state.OriginOutsideSince = null;

        // Timeline only. Origin departure is a measurement the dispatch board reads as "in transit";
        // it is deliberately absent from Manager's alert catalog, so nothing is paged (§11).
        await tripEventWriter.AppendAsync(
            accountId, state.TripId, null, TripEventTypes.TripOriginDeparted, position.DeviceDateTime,
            TripEventSources.Detection, null, $"trip-origin-depart:{state.TripId:N}", cancellationToken);
    }

    // 5. Arrival. Containment is evaluated in the database against the snapshotted ArrivalGeom.
    //    Every Pending stop containing the point is considered, not just the lowest-sequence one,
    //    so an out-of-order arrival is RECORDED rather than lost — real routes get resequenced by
    //    traffic and a dispatcher would rather see the truth than a tidy fiction.
    private async Task<int> DetectArrivalsAsync(
        TripDetectionState state, Guid accountId, TransporterPositionDto position, CancellationToken cancellationToken)
    {
        // A vehicle still standing in its origin zone has not arrived anywhere. Without this a round
        // trip's return-to-depot stop — whose zone IS the origin — registered as Arrived on the very
        // fix that started the trip, and the route was "complete" before the truck moved.
        //
        // Gated on the trip not yet having departed its origin, so the genuine return at the END of
        // the round trip still detects normally.
        if (state.HasOriginGeom && state.OriginDepartedAt is null && state.InsideOrigin)
        {
            state.ContainingStops = [];
            return 0;
        }

        var containing = unitOfWork.StopsContainingPoint(state.TripId, position.Latitude, position.Longitude);

        state.ContainingStops = containing.ToHashSet();
        if (containing.Count == 0)
            return 0;

        var count = 0;
        foreach (var stop in state.Stops.Where(s => string.Equals(s.Status, TripStopStatuses.Pending, StringComparison.Ordinal)).ToList())
        {
            if (!state.ContainingStops.Contains(stop.TripStopId))
                continue;

            // Keyed WITHOUT a client event id: detection may see the same stop across many
            // batches, and exactly one arrival must ever be recorded (acceptance 13).
            var recorded = await stopWriter.RecordStopProgressAsync(
                state.TripId, stop.TripStopId, accountId, TripStopStatuses.Arrived, position.DeviceDateTime,
                position.Latitude, position.Longitude, TripEventSources.Detection,
                $"trip-arrive:{stop.TripStopId:N}", null, cancellationToken);

            if (!recorded)
                continue;

            state.MarkStopStatus(stop.TripStopId, TripStopStatuses.Arrived);

            // The writer clears the persisted debounce clock on arrival; keep the cache in step.
            state.OutsideSince.Remove(stop.TripStopId);
            count++;
            await EmitStopAlertAsync(state, accountId, stop, TripEventTypes.TripStopArrived, position, cancellationToken);
        }

        return count;
    }

    // 6. Departure. An Arrived stop whose position has been outside its arrival geometry for the
    //    debounce window. Without the debounce a single fix bouncing off the edge of the polygon
    //    would close a stop the vehicle is still sitting in.
    //
    //    The clock is PERSISTED (TripStop.OutsideSinceAt). Router delivers one fix per call, so the
    //    comparison below is only ever reachable from a LATER call — with a per-request clock the
    //    first outside fix stored the instant and the second call reset it, so no stop ever
    //    departed and an auto-tracked trip could only be completed with `force`.
    private async Task<int> DetectDeparturesAsync(
        TripDetectionState state, Guid accountId, TransporterPositionDto position, CancellationToken cancellationToken)
    {
        var count = 0;
        foreach (var stop in state.Stops.Where(s => string.Equals(s.Status, TripStopStatuses.Arrived, StringComparison.Ordinal)).ToList())
        {
            if (state.ContainingStops.Contains(stop.TripStopId))
            {
                // Back inside: the debounce restarts from zero, in the database as well as here.
                if (state.OutsideSince.Remove(stop.TripStopId))
                {
                    await stopWriter.SetStopOutsideSinceAsync(stop.TripStopId, accountId, null, cancellationToken);
                }

                continue;
            }

            if (!state.OutsideSince.TryGetValue(stop.TripStopId, out var outsideSince))
            {
                state.OutsideSince[stop.TripStopId] = position.DeviceDateTime;
                await stopWriter.SetStopOutsideSinceAsync(stop.TripStopId, accountId, position.DeviceDateTime, cancellationToken);
                continue;
            }

            if (position.DeviceDateTime - outsideSince < DepartureDebounce)
                continue;

            var recorded = await stopWriter.RecordStopProgressAsync(
                state.TripId, stop.TripStopId, accountId, TripStopStatuses.Departed, position.DeviceDateTime,
                position.Latitude, position.Longitude, TripEventSources.Detection,
                $"trip-depart:{stop.TripStopId:N}", null, cancellationToken);

            if (!recorded)
                continue;

            state.MarkStopStatus(stop.TripStopId, TripStopStatuses.Departed);

            // The writer already cleared the persisted clock as part of recording the departure.
            state.OutsideSince.Remove(stop.TripStopId);
            count++;
            await EmitStopAlertAsync(state, accountId, stop, TripEventTypes.TripStopDeparted, position, cancellationToken);
        }

        return count;
    }

    // 7. Deviation. Three consecutive fixes outside the corridor open an episode; re-entry clears
    //    it so a later departure can open a new one (acceptance 14).
    //
    //    The run length is PERSISTED (Trip.ConsecutiveOutsideFixes). Router delivers one fix per
    //    call, so an in-memory counter went 0 → 1 and was thrown away every batch and the threshold
    //    of three was unreachable — corridor deviation was completely inert.
    private async Task<int> DetectDeviationAsync(
        TripDetectionState state, Guid accountId, TransporterPositionDto position, CancellationToken cancellationToken)
    {
        if (!state.HasReadyRoutePlan || state.RoutePlanId is not { } routePlanId)
            return 0;

        bool? inside;
        try
        {
            inside = await detectionReader.IsInsideCorridorAsync(accountId, routePlanId, position.Latitude, position.Longitude, cancellationToken);
        }
        catch (Exception ex)
        {
            logger.LogError(ex, "Corridor check failed for trip {TripId}; skipping deviation detection for this fix", state.TripId);
            return 0;
        }

        if (inside is null)
        {
            // No corridor to test against. NOT a deviation: counting it would climb to the
            // three-fix threshold on a vehicle driving the route perfectly, and re-entry could
            // never clear it because there is nothing to re-enter. Leave the run length untouched.
            logger.LogWarning(
                "Route plan {RoutePlanId} on trip {TripId} has no corridor geometry; deviation detection is skipped for this fix",
                routePlanId, state.TripId);
            return 0;
        }

        if (inside.Value)
        {
            // Re-entry closes the episode — in the database, so a LATER departure opens a new one
            // with a new episode start and therefore a new idempotency key (acceptance 14).
            if (state.ConsecutiveOutside != 0 || state.DeviationOpenedAt is not null)
            {
                state.ConsecutiveOutside = 0;
                state.DeviationOpenedAt = null;
                unitOfWork.SetDeviationState(state.TripId, null, 0);
            }

            return 0;
        }

        state.ConsecutiveOutside++;

        if (state.ConsecutiveOutside < DeviationFixThreshold || state.DeviationOpenedAt is not null)
        {
            // Still short of the threshold, or the episode is already open: only the run length
            // moves, and it MUST be persisted or the next call starts counting from zero again.
            unitOfWork.SetDeviationState(state.TripId, state.DeviationOpenedAt, state.ConsecutiveOutside);
            return 0;
        }

        // The episode's identity is the instant it OPENS, which is what gets persisted as
        // DeviationOpenedAt — so the idempotency key below is stable across batches and restarts.
        var episodeStart = position.DeviceDateTime;
        try
        {
            await alertEmitter.EmitAsync(
                TripEventTypes.TripRouteDeviation,
                TripAlertSeverities.Warning,
                $"trip-deviation:{state.TripId:N}",
                new TripAlertDto(accountId, state.TripId, null, state.Code, state.TransporterId, state.DriverId, null,
                    position.DeviceDateTime, null, null, null, position.Latitude, position.Longitude),
                cancellationToken);
        }
        catch (Exception ex)
        {
            // The geofence dwell precedent: DeviationOpenedAt is not stamped, so the episode is
            // retried on the next cycle rather than being lost to a transient Manager failure. The
            // run length is still persisted — the vehicle really is outside, and losing the count
            // would restart the three-fix climb from zero on every failed emission.
            logger.LogError(ex, "Failed to emit TripRouteDeviation alert for trip {TripId}; it will be retried", state.TripId);
            unitOfWork.SetDeviationState(state.TripId, null, state.ConsecutiveOutside);
            return 0;
        }

        // Stamped ONLY after a successful emission, and persisted: it is both the "an episode is
        // open" flag every later batch reads and the episode key's source.
        state.DeviationOpenedAt = episodeStart;
        unitOfWork.SetDeviationState(state.TripId, episodeStart, state.ConsecutiveOutside);

        await tripEventWriter.AppendAsync(
            accountId, state.TripId, null, TripEventTypes.TripRouteDeviation, position.DeviceDateTime,
            TripEventSources.Detection, null,
            $"trip-deviation:{state.TripId:N}:{episodeStart.UtcTicks}", cancellationToken);

        return 1;
    }

    /// <summary>
    /// The cheap pre-filter that decides whether completion is even worth asking about: a running
    /// trip with a route and no <c>Pending</c> stop left.
    /// <para>
    /// Gating on "a stop just closed" instead would have made the DWELL rule unreachable from the
    /// position feed. That rule fires on time passing at an <c>Arrived</c> final stop — no departure
    /// ever happens — so a parked truck that keeps reporting would have waited for the sweep, which
    /// exists for the opposite case (a truck that has stopped reporting entirely, §5.2).
    /// </para>
    /// </summary>
    private static bool HasNothingLeftToVisit(TripDetectionState state)
        => state.IsInProgress
            && state.Stops.Count > 0
            && !state.Stops.Any(s => string.Equals(s.Status, TripStopStatuses.Pending, StringComparison.Ordinal));

    // 8. Completion, evaluated on every fix once the route has nothing pending left. The
    //    trip-eta-refresh sweep runs the same check for the devices that go dark, which is the only
    //    case the position feed can never reach (§5.2).
    private async Task TryCompleteAsync(
        TripDetectionState state, Guid accountId, TransporterPositionDto position,
        TripAccountConfigVm config, CancellationToken cancellationToken)
    {
        try
        {
            if (await autoCompletionService.TryCompleteAsync(
                    accountId, state.TripId, position.DeviceDateTime, config.FinalStopCompletionMinutes, cancellationToken))
            {
                state.Status = TripStatuses.Completed;
            }
        }
        catch (Exception ex)
        {
            // The stop closure it followed is already recorded; the sweep retries the completion.
            logger.LogError(ex, "Auto-completion failed for trip {TripId}; the sweep will retry it", state.TripId);
        }
    }

    private async Task EmitTripAlertAsync(
        TripDetectionState state, Guid accountId, string eventType, DateTimeOffset occurredAt, CancellationToken cancellationToken)
    {
        try
        {
            // The SAME alert and the same dedup key the manual path emits — spec 11a §11: the
            // timeline's Source, not a separate alert type, is what says a start was measured.
            await alertEmitter.EmitAsync(
                eventType,
                TripAlertSeverities.Info,
                $"trip-{eventType.ToLowerInvariant()}:{state.TripId:N}",
                new TripAlertDto(accountId, state.TripId, null, state.Code, state.TransporterId, state.DriverId,
                    null, occurredAt, null, null, null, null, null),
                cancellationToken);
        }
        catch (Exception ex)
        {
            logger.LogError(ex, "Failed to emit {EventType} alert for trip {TripId}", eventType, state.TripId);
        }
    }

    private async Task EmitStopAlertAsync(
        TripDetectionState state, Guid accountId, OpenTripStopVm stop, string eventType,
        TransporterPositionDto position, CancellationToken cancellationToken)
    {
        try
        {
            await alertEmitter.EmitAsync(
                eventType,
                TripAlertSeverities.Info,
                $"trip-{eventType.ToLowerInvariant()}:{stop.TripStopId:N}",
                new TripAlertDto(accountId, state.TripId, stop.TripStopId, state.Code, state.TransporterId, state.DriverId,
                    stop.Name, position.DeviceDateTime, null, stop.PlannedArrivalTo, null, position.Latitude, position.Longitude),
                cancellationToken);
        }
        catch (Exception ex)
        {
            logger.LogError(ex, "Failed to emit {EventType} alert for stop {TripStopId}", eventType, stop.TripStopId);
        }
    }
}
