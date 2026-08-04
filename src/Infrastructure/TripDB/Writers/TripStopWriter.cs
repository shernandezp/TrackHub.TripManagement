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

using Common.Application.Exceptions;
using TrackHub.TripManagement.Infrastructure.TripDB.Events;
using TrackHub.TripManagement.Infrastructure.TripDB.Readers;

namespace TrackHub.TripManagement.Infrastructure.TripDB.Writers;

/// <summary>
/// Stop mutation. Sequences are re-normalized server-side on every structural change, so the
/// unique <c>(TripId, Sequence)</c> index can never be violated by a client's ordering.
/// <para>
/// Queries that lead to a mutation are <c>AsTracking()</c> and nothing here calls <c>Attach</c> —
/// see <see cref="TripWriter"/> for why. The paths that hit it here are the ETA sweep (an ETA
/// update followed by the delay stamp on the SAME stop), detection across a multi-fix batch, and
/// the partner importer (a header update followed by a stop replacement on the same trip).
/// </para>
/// </summary>
public sealed class TripStopWriter(IApplicationDbContext context) : ITripStopWriter
{
    public async Task<TripStopVm> AddStopAsync(Guid tripId, Guid accountId, TripStopDto stop, CancellationToken cancellationToken)
    {
        var trip = await context.Trips.FirstOrDefaultAsync(t => t.TripId == tripId && t.AccountId == accountId, cancellationToken)
            ?? throw new NotFoundException($"{tripId}", nameof(Trip));

        if (TripStatuses.IsTerminal(trip.Status))
        {
            throw ConflictException.WithCode(TripErrorCodes.TripAlreadyTerminal);
        }

        var lastSequence = await context.TripStops
            .Where(s => s.TripId == tripId)
            .Select(s => (int?)s.Sequence)
            .MaxAsync(cancellationToken) ?? 0;

        var entity = new TripStop
        {
            AccountId = accountId,
            TripId = tripId,
            Sequence = lastSequence + 1,
            Name = stop.Name,
            Address = stop.Address,
            City = stop.City,
            Point = TripGeometryFactory.Point(stop.Latitude, stop.Longitude),
            GeofenceId = stop.GeofenceId,

            // Normalized, not taken raw: the portal path is already validated to 50-5000, but
            // ImportTripsValidator checks only trip-level fields, so a partner sending 0 (or
            // nothing) would otherwise get a zero-radius arrival ring that can never contain a fix.
            ArrivalRadiusMeters = TripGeometry.NormalizeRadius(stop.ArrivalRadiusMeters),
            Activity = TripStopActivities.Normalize(stop.Activity),
            PlannedArrivalFrom = stop.PlannedArrivalFrom,
            PlannedArrivalTo = stop.PlannedArrivalTo,
            Status = TripStopStatuses.Pending,
            EtaSource = EtaSources.Unavailable,
            RequiresPod = stop.RequiresPod,
            Priority = stop.Priority,
            Observations = stop.Observations,
        };

        // A stop added to a trip detection is already WATCHING gets its arrival snapshot
        // immediately - otherwise detection would never fire for it (the bulk snapshot has passed).
        //
        // "Watching" is armed OR started, not just started. Paused counts because Resume does not
        // re-snapshot; ARMED counts because arming now takes the bulk snapshot for a Created trip,
        // and a stop added afterwards would otherwise be the one stop on the route with no geometry.
        if (IsWatched(trip))
        {
            entity.ArrivalGeom = await ResolveArrivalGeomAsync(entity, accountId, cancellationToken);
        }

        entity.AddDomainEvent(new TripDomainEvent(TripEventTypes.TripUpdated, accountId, tripId, entity.TripStopId));
        await context.TripStops.AddAsync(entity, cancellationToken);
        await context.SaveChangesAsync(cancellationToken);

        return TripMapper.ToVm(entity, []);
    }

    public async Task UpdateStopAsync(Guid tripStopId, Guid accountId, TripStopDto stop, CancellationToken cancellationToken)
    {
        var entity = await FindAsync(tripStopId, accountId, null, cancellationToken);

        if (TripStopStatuses.IsClosed(entity.Status))
        {
            throw ConflictException.WithCode(TripErrorCodes.StopAlreadyDeparted);
        }

        entity.Name = stop.Name;
        entity.Address = stop.Address;
        entity.City = stop.City;
        entity.Point = TripGeometryFactory.Point(stop.Latitude, stop.Longitude);
        entity.GeofenceId = stop.GeofenceId;
        entity.ArrivalRadiusMeters = TripGeometry.NormalizeRadius(stop.ArrivalRadiusMeters);
        entity.Activity = TripStopActivities.Normalize(stop.Activity);
        entity.PlannedArrivalFrom = stop.PlannedArrivalFrom;
        entity.PlannedArrivalTo = stop.PlannedArrivalTo;
        entity.RequiresPod = stop.RequiresPod;
        entity.Priority = stop.Priority;
        entity.Observations = stop.Observations;

        // Re-snapshot only while the snapshot is still live for this stop; a stop already Arrived
        // keeps the geometry its arrival was judged against.
        //
        // Keyed on the TRIP being watched, not on ArrivalGeom already being non-null. The old
        // condition could only ever refresh a geometry that existed, so a stop left null-geometry
        // (see AddStopAsync) stayed undetectable no matter how many times it was edited — editing
        // was the one obvious way a dispatcher would have tried to fix it.
        //
        // ARMED counts as watched: a Created trip carries a snapshot from arming now, and the
        // start-time fill only touches NULL geometries — so moving a stop on an armed trip would
        // otherwise leave detection judging arrivals against the place the stop used to be.
        if (string.Equals(entity.Status, TripStopStatuses.Pending, StringComparison.Ordinal))
        {
            var watched = await context.Trips
                .Where(t => t.TripId == entity.TripId && t.AccountId == accountId)
                .Select(t => new { t.Status, t.ArmedAt })
                .FirstOrDefaultAsync(cancellationToken);

            if (watched is not null && (TripStatuses.HasStarted(watched.Status) || watched.ArmedAt.HasValue))
            {
                entity.ArrivalGeom = await ResolveArrivalGeomAsync(entity, accountId, cancellationToken);
            }
        }

        entity.AddDomainEvent(new TripDomainEvent(TripEventTypes.TripUpdated, accountId, entity.TripId, entity.TripStopId));
        await context.SaveChangesAsync(cancellationToken);
    }

    public async Task RemoveStopAsync(Guid tripStopId, Guid accountId, CancellationToken cancellationToken)
    {
        var entity = await FindAsync(tripStopId, accountId, null, cancellationToken);

        // An Arrived or Departed stop is recorded history and cannot be removed (spec 11 7.3).
        if (!string.Equals(entity.Status, TripStopStatuses.Pending, StringComparison.Ordinal))
        {
            throw ConflictException.WithCode(TripErrorCodes.StopAlreadyDeparted);
        }

        var tripId = entity.TripId;
        context.TripStops.Remove(entity);
        await context.SaveChangesAsync(cancellationToken);

        await RenumberAsync(tripId, cancellationToken);
    }

    public async Task ReplaceStopsAsync(Guid tripId, Guid accountId, IReadOnlyCollection<TripStopDto> stops, CancellationToken cancellationToken)
    {
        // Tracking: this path stamps a domain event on the trip, and the partner importer reaches it
        // straight after UpdateTripAsync has already tracked the same row.
        var trip = await context.Trips.AsTracking().FirstOrDefaultAsync(t => t.TripId == tripId && t.AccountId == accountId, cancellationToken)
            ?? throw new NotFoundException($"{tripId}", nameof(Trip));

        // A started trip's stops are recorded history — arrivals, departures, deliveries, POD.
        // Replacing them would delete measurements, so the route is only replaceable while the trip
        // is still a plan.
        if (!string.Equals(trip.Status, TripStatuses.Created, StringComparison.Ordinal))
        {
            throw ConflictException.WithCode(TripErrorCodes.TripNotActive);
        }

        var existing = await context.TripStops
            .AsTracking()
            .Where(s => s.TripId == tripId && s.AccountId == accountId)
            .ToListAsync(cancellationToken);

        foreach (var stop in existing)
        {
            context.TripStops.Remove(stop);
        }

        // The old route is deleted in a save of its OWN, before the new one is inserted. Batching
        // both would hand the database a delete of (trip, 1) and an insert of (trip, 1) in one
        // command batch, and `ux_trip_stops_tripid_sequence` is checked per statement — the same
        // hazard `ReorderStopsAsync` avoids with its negative-range two-pass.
        if (existing.Count > 0)
        {
            await context.SaveChangesAsync(cancellationToken);
        }

        var sequence = 1;
        foreach (var stop in stops)
        {
            await context.TripStops.AddAsync(
                new TripStop
                {
                    AccountId = accountId,
                    TripId = tripId,
                    Sequence = sequence++,
                    Name = stop.Name,
                    Address = stop.Address,
                    City = stop.City,
                    Point = TripGeometryFactory.Point(stop.Latitude, stop.Longitude),
                    GeofenceId = stop.GeofenceId,
                    ArrivalRadiusMeters = TripGeometry.NormalizeRadius(stop.ArrivalRadiusMeters),
                    Activity = TripStopActivities.Normalize(stop.Activity),
                    PlannedArrivalFrom = stop.PlannedArrivalFrom,
                    PlannedArrivalTo = stop.PlannedArrivalTo,
                    Status = TripStopStatuses.Pending,
                    EtaSource = EtaSources.Unavailable,
                    RequiresPod = stop.RequiresPod,
                    Priority = stop.Priority,
                    Observations = stop.Observations,
                },
                cancellationToken);
        }

        trip.AddDomainEvent(new TripDomainEvent(TripEventTypes.TripUpdated, accountId, tripId));
        await context.SaveChangesAsync(cancellationToken);
    }

    public async Task ReorderStopsAsync(Guid tripId, Guid accountId, IReadOnlyCollection<Guid> orderedStopIds, CancellationToken cancellationToken)
    {
        var stops = await context.TripStops
            .AsTracking()
            .Where(s => s.TripId == tripId && s.AccountId == accountId)
            .OrderBy(s => s.Sequence)
            .ToListAsync(cancellationToken);

        var ordered = orderedStopIds.ToList();
        if (stops.Count != ordered.Count || stops.Exists(s => !ordered.Contains(s.TripStopId)))
        {
            throw ConflictException.WithCode(TripErrorCodes.StopsNotComplete);
        }

        // A stop that has already progressed (Arrived/Departed/Skipped) may not be pushed below a
        // stop that has not - a reorder can never rewrite history.
        //
        // The rule is "no progressed stop may appear AFTER an unprogressed one", so the only
        // state needed is whether an unprogressed stop has been seen yet. The previous version
        // tracked `lastClosedIndex` and tested `i < lastClosedIndex`, which is unsatisfiable for
        // an ascending loop index - the guard could never throw for any input.
        var seenUnprogressedIndex = -1;
        for (var i = 0; i < ordered.Count; i++)
        {
            var stop = stops.Find(s => s.TripStopId == ordered[i])!;
            var progressed = TripStopStatuses.IsClosed(stop.Status)
                || string.Equals(stop.Status, TripStopStatuses.Arrived, StringComparison.Ordinal);

            if (progressed)
            {
                if (seenUnprogressedIndex >= 0)
                {
                    throw ConflictException.WithCode(TripErrorCodes.StopAlreadyDeparted);
                }
            }
            else if (seenUnprogressedIndex < 0)
            {
                seenUnprogressedIndex = i;
            }
        }

        // Two passes through a negative range: the unique (TripId, Sequence) index would trip on
        // any straight swap.
        for (var i = 0; i < ordered.Count; i++)
        {
            var stop = stops.Find(s => s.TripStopId == ordered[i])!;
            stop.Sequence = -(i + 1);
        }

        await context.SaveChangesAsync(cancellationToken);

        foreach (var stop in stops)
        {
            stop.Sequence = -stop.Sequence;
        }

        await context.SaveChangesAsync(cancellationToken);
    }

    public async Task<bool> RecordStopProgressAsync(
        Guid tripId,
        Guid tripStopId,
        Guid accountId,
        string toStatus,
        DateTimeOffset occurredAt,
        double? latitude,
        double? longitude,
        string source,
        string idempotencyKey,
        string? reason,
        CancellationToken cancellationToken)
    {
        // Resolved by (stop, account, TRIP): without the trip the caller's active-trip check and
        // the row actually written could belong to two different trips.
        var entity = await FindAsync(tripStopId, accountId, tripId, cancellationToken);

        // IDEMPOTENCY IS CHECKED BEFORE THE TRANSITION GUARD, and the order is the whole point.
        //
        // The guard used to run first, so a replayed arrival threw STOP_ALREADY_DEPARTED once the
        // stop had moved on. That is exactly what spec 10's offline outbox produces: a driver
        // captures arrival and departure with no signal, the departure syncs first (or the arrival
        // is retried after both landed), and the queued arrival then fails permanently with an
        // error the device can only surface as a hard failure. Acceptance 15 requires a duplicate
        // to be a SUCCESS with no second row — a state guard must never fire on an event the
        // server has already accepted.
        if (await context.TripEvents.AnyAsync(
                e => e.AccountId == accountId && e.IdempotencyKey == idempotencyKey, cancellationToken))
        {
            return false;
        }

        GuardStopTransition(entity.Status, toStatus);

        var tripEvent = new TripEvent
        {
            AccountId = accountId,
            TripId = entity.TripId,
            TripStopId = tripStopId,
            EventType = EventTypeFor(toStatus),
            OccurredAt = occurredAt,
            Source = source,
            IdempotencyKey = idempotencyKey,
        };

        // Timestamps once written are NEVER overwritten by a later detection or manual override
        // (acceptance 12). The status still advances; only the first recorded instant survives.
        switch (toStatus)
        {
            case TripStopStatuses.Arrived:
                entity.ActualArrivalAt ??= occurredAt;

                // Arriving restarts the departure debounce from scratch: a clock left over from a
                // previous excursion would otherwise be 30 s "old" the moment the vehicle rolls out.
                entity.OutsideSinceAt = null;
                break;
            case TripStopStatuses.Departed:
                entity.ActualDepartureAt ??= occurredAt;
                entity.OutsideSinceAt = null;
                break;
            case TripStopStatuses.Skipped:
                entity.Observations = reason ?? entity.Observations;
                entity.OutsideSinceAt = null;
                break;
            default:
                throw ConflictException.WithCode(TripErrorCodes.InvalidTransition);
        }

        if (!TripStopStatuses.IsClosed(entity.Status))
        {
            entity.Status = toStatus;
        }

        if (latitude.HasValue && longitude.HasValue)
        {
            tripEvent.PayloadJson = $"{{\"latitude\":{latitude.Value.ToString(System.Globalization.CultureInfo.InvariantCulture)},\"longitude\":{longitude.Value.ToString(System.Globalization.CultureInfo.InvariantCulture)}}}";
        }

        await context.TripEvents.AddAsync(tripEvent, cancellationToken);
        entity.AddDomainEvent(new TripDomainEvent(tripEvent.EventType, accountId, entity.TripId, tripStopId));

        try
        {
            await context.SaveChangesAsync(cancellationToken);
            return true;
        }
        catch (DbUpdateException exception) when (UniqueViolation.Matches(exception, "ux_trip_events_idempotencykey"))
        {
            // Duplicate submission: report "nothing written" without a second row (acceptance 15).
            //
            // Both the dead insert AND the status/timestamp mutations must be dropped from the
            // change tracker. The context is scoped to the request: leaving them tracked makes the
            // NEXT SaveChangesAsync replay the failing insert, so a later genuine event in the same
            // request is lost to a duplicate that was already handled.
            context.TripEvents.Entry(tripEvent).State = EntityState.Detached;
            context.TripStops.Entry(entity).State = EntityState.Detached;
            return false;
        }
    }

    public async Task SetStopOutsideSinceAsync(Guid tripStopId, Guid accountId, DateTimeOffset? outsideSinceAt, CancellationToken cancellationToken)
    {
        var entity = await context.TripStops
            .AsTracking()
            .FirstOrDefaultAsync(s => s.TripStopId == tripStopId && s.AccountId == accountId, cancellationToken);

        if (entity is null || entity.OutsideSinceAt == outsideSinceAt)
        {
            return;
        }

        entity.OutsideSinceAt = outsideSinceAt;
        await context.SaveChangesAsync(cancellationToken);
    }

    public async Task UpdateStopEtaAsync(Guid tripStopId, Guid accountId, DateTimeOffset? etaAt, string etaSource, CancellationToken cancellationToken)
    {
        var entity = await FindAsync(tripStopId, accountId, null, cancellationToken);
        entity.EtaAt = etaAt;
        entity.EtaSource = etaSource;
        await context.SaveChangesAsync(cancellationToken);
    }

    public async Task MarkStopDelayAlertedAsync(Guid tripStopId, Guid accountId, DateTimeOffset alertedAt, CancellationToken cancellationToken)
    {
        var entity = await FindAsync(tripStopId, accountId, null, cancellationToken);

        // One-shot marker, stamped only AFTER the alert was successfully emitted (the geofence
        // dwell precedent) so a failed emission is retried rather than lost.
        entity.DelayAlertedAt ??= alertedAt;
        await context.SaveChangesAsync(cancellationToken);
    }

    private static string EventTypeFor(string toStatus) => toStatus switch
    {
        TripStopStatuses.Arrived => TripEventTypes.TripStopArrived,
        TripStopStatuses.Departed => TripEventTypes.TripStopDeparted,
        TripStopStatuses.Skipped => TripEventTypes.TripStopSkipped,
        _ => TripEventTypes.TripUpdated,
    };

    /// <summary>
    /// Whether detection is already judging this trip's stops against frozen geometry — armed, or
    /// running/paused. A stop touched while the trip is watched must carry a snapshot of its own.
    /// </summary>
    private static bool IsWatched(Trip trip)
        => TripStatuses.HasStarted(trip.Status) || trip.ArmedAt.HasValue;

    private async Task<NetTopologySuite.Geometries.Polygon> ResolveArrivalGeomAsync(TripStop stop, Guid accountId, CancellationToken cancellationToken)
    {
        if (stop.GeofenceId is { } geofenceId)
        {
            var geom = await context.Geofences
                .Where(g => g.GeofenceId == geofenceId && g.AccountId == accountId)
                .Select(g => g.Geom)
                .FirstOrDefaultAsync(cancellationToken);

            if (geom is not null)
            {
                return geom;
            }
        }

        return TripGeometryFactory.Buffer(stop.Point, stop.ArrivalRadiusMeters);
    }

    private async Task RenumberAsync(Guid tripId, CancellationToken cancellationToken)
    {
        var stops = await context.TripStops
            .AsTracking()
            .Where(s => s.TripId == tripId)
            .OrderBy(s => s.Sequence)
            .ToListAsync(cancellationToken);

        var next = 1;
        var changed = false;
        foreach (var stop in stops)
        {
            if (stop.Sequence != next)
            {
                stop.Sequence = next;
                changed = true;
            }

            next++;
        }

        if (changed)
        {
            await context.SaveChangesAsync(cancellationToken);
        }
    }

    /// <summary>
    /// Stop progression is <c>Pending → Arrived → Departed</c>, or <c>Pending → Skipped</c>
    /// (acceptance 12). Re-submitting the status the stop already holds is NOT a conflict: that is
    /// the idempotent retry path, and the unique idempotency index — not this guard — is what makes
    /// it a no-op (acceptance 15).
    /// </summary>
    private static void GuardStopTransition(string fromStatus, string toStatus)
    {
        if (string.Equals(fromStatus, toStatus, StringComparison.Ordinal))
        {
            return;
        }

        if (string.Equals(fromStatus, TripStopStatuses.Departed, StringComparison.Ordinal))
        {
            throw ConflictException.WithCode(TripErrorCodes.StopAlreadyDeparted);
        }

        if (string.Equals(fromStatus, TripStopStatuses.Skipped, StringComparison.Ordinal))
        {
            throw ConflictException.WithCode(TripErrorCodes.StopAlreadySkipped);
        }

        // Departing a stop that never arrived would stamp ActualDepartureAt with no ActualArrivalAt
        // and silently invent a visit that never happened.
        if (string.Equals(fromStatus, TripStopStatuses.Pending, StringComparison.Ordinal)
            && string.Equals(toStatus, TripStopStatuses.Departed, StringComparison.Ordinal))
        {
            throw ConflictException.WithCode(TripErrorCodes.StopNotArrived);
        }

        // Arrived → Skipped is not in the matrix: the visit already happened, so the outcome is
        // recorded as a delivery outcome, not by erasing the stop from the route.
        if (string.Equals(fromStatus, TripStopStatuses.Arrived, StringComparison.Ordinal)
            && string.Equals(toStatus, TripStopStatuses.Skipped, StringComparison.Ordinal))
        {
            throw ConflictException.WithCode(TripErrorCodes.InvalidTransition);
        }
    }

    private async Task<TripStop> FindAsync(Guid tripStopId, Guid accountId, Guid? tripId, CancellationToken cancellationToken)
        => await context.TripStops.AsTracking().FirstOrDefaultAsync(
                s => s.TripStopId == tripStopId
                    && s.AccountId == accountId
                    && (tripId == null || s.TripId == tripId),
                cancellationToken)
            ?? throw new NotFoundException($"{tripStopId}", nameof(TripStop));
}
