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

namespace TrackHub.TripManagement.Infrastructure.TripDB.Writers;

/// <summary>
/// See <see cref="ITripDetectionUnitOfWork"/>. The working set is loaded ONCE per batch as tracked
/// entities and held here; every mutation is delegated to the entity that owns the rule, and one
/// <c>SaveChangesAsync</c> commits the lot.
/// <para>
/// Because the rows are tracked from the load, nothing in this class re-queries and nothing
/// attaches. That is what makes the whole class of "second write to the same row in one request"
/// failures structurally impossible on the detection path, rather than avoided by remembering to
/// write <c>AsTracking()</c> on every future query.
/// </para>
/// <para>
/// The selection rules for the working set live here and nowhere else — this is the single
/// definition of "which trips is detection watching this cycle" (spec 11a §6.1, §7).
/// </para>
/// </summary>
public sealed class TripDetectionUnitOfWork(
    IApplicationDbContext context,
    ILogger<TripDetectionUnitOfWork> logger) : ITripDetectionUnitOfWork
{
    /// <summary>
    /// Defensive per-cycle cap. The in-flight set is naturally small, but a pathological backlog
    /// must not materialize unbounded (the Geofencing dwell-candidate precedent).
    /// </summary>
    private const int MaxTripsPerCycle = 1000;

    private readonly Dictionary<Guid, Trip> trips = [];
    private readonly Dictionary<Guid, List<TripStop>> stopsByTrip = [];
    private bool dirty;

    public async Task<IReadOnlyCollection<OpenTripVm>> LoadAsync(
        Guid accountId,
        IReadOnlyCollection<Guid> transporterIds,
        DateTimeOffset? armableUntil,
        CancellationToken cancellationToken)
    {
        trips.Clear();
        stopsByTrip.Clear();
        dirty = false;

        if (transporterIds.Count == 0)
        {
            return [];
        }

        var ids = transporterIds.ToList();

        // InProgress AND Paused, because they answer two different questions. Only the InProgress
        // ones are processed — a paused trip is explicitly out of automation's hands (§5.2) — but
        // BOTH occupy their vehicle, so both have to be visible here or the queue jumps the moment a
        // dispatcher pauses something.
        var occupied = await context.Trips
            .AsTracking()
            .Where(t => (t.Status == TripStatuses.InProgress || t.Status == TripStatuses.Paused)
                && t.AccountId == accountId
                && ids.Contains(t.TransporterId))
            .OrderBy(t => t.PlannedStartAt)
            .Take(MaxTripsPerCycle)
            .ToListAsync(cancellationToken);

        var working = occupied.FindAll(t => string.Equals(t.Status, TripStatuses.InProgress, StringComparison.Ordinal));

        if (armableUntil is { } until)
        {
            // Only transporters with nothing open: one physical unit runs one trip at a time, so a
            // committed vehicle's queue stays untouched until its current trip actually closes.
            // Pausing is not closing — the truck is still out there with the load on it.
            var busy = occupied.Select(t => t.TransporterId).ToHashSet();
            var idle = ids.FindAll(id => !busy.Contains(id));

            if (idle.Count > 0)
            {
                // The per-vehicle queue (§7) is enforced TWICE, and both are deliberate.
                //
                // The NOT EXISTS keeps the materialized set small: a Created trip qualifies only when
                // no EARLIER-planned Created trip exists for the same transporter, so a fleet with a
                // year of backlog still reads a handful of rows. It does NOT settle ties — two trips
                // planned for the same instant both survive it.
                //
                // The GroupBy after it is what makes "one per vehicle" a guarantee rather than a
                // consequence of the data, and it settles those ties deterministically.
                //
                // Either one alone happens to produce the right answer today. Do not delete BOTH on
                // the strength of that: together they are what stops trip N+1 arming while trip N is
                // still Created — including when trip N is overdue and nobody has cancelled it.
                var armable = await context.Trips
                    .AsTracking()
                    .Where(t => t.AccountId == accountId
                        && t.Status == TripStatuses.Created
                        && idle.Contains(t.TransporterId)
                        && t.PlannedStartAt <= until
                        && !context.Trips.Any(other => other.AccountId == accountId
                            && other.Status == TripStatuses.Created
                            && other.TransporterId == t.TransporterId
                            && other.PlannedStartAt < t.PlannedStartAt))
                    .OrderBy(t => t.PlannedStartAt)
                    .ThenBy(t => t.Code)
                    .Take(MaxTripsPerCycle)
                    .ToListAsync(cancellationToken);

                working.AddRange(armable
                    .GroupBy(t => t.TransporterId)
                    .Select(g => g.First()));
            }
        }

        if (working.Count == 0)
        {
            return [];
        }

        var tripIds = working.ConvertAll(t => t.TripId);

        // EVERY stop, not only the open ones. Auto-completion has to tell "all stops closed" from
        // "this trip has no stops", and it has to find the FINAL stop by sequence — neither question
        // is answerable from a list that silently omits the closed ones (§5.2).
        var stops = await context.TripStops
            .AsTracking()
            .Where(s => tripIds.Contains(s.TripId))
            .OrderBy(s => s.TripId)
            .ThenBy(s => s.Sequence)
            .ToListAsync(cancellationToken);

        var readyPlanTripIds = await context.RoutePlans
            .Where(p => tripIds.Contains(p.TripId) && p.Status == RoutePlanStatuses.Ready)
            .Select(p => p.TripId)
            .Distinct()
            .ToListAsync(cancellationToken);

        foreach (var trip in working)
        {
            trips[trip.TripId] = trip;
            stopsByTrip[trip.TripId] = stops.FindAll(s => s.TripId == trip.TripId);
        }

        return [.. working.Select(t => new OpenTripVm(
            t.TripId,
            t.AccountId,
            t.Code,
            t.Status,
            t.TransporterId,
            t.DriverId,
            t.RoutePlanId,
            readyPlanTripIds.Contains(t.TripId),
            t.PlannedStartAt,
            t.ArmedAt,
            t.OriginGeom is not null,
            t.OriginArrivedAt,
            t.OriginDepartedAt,
            t.OriginOutsideSinceAt,
            t.DeviationOpenedAt,
            t.ConsecutiveOutsideFixes,
            t.ActualDistanceMeters,
            t.LastPoint?.Y,
            t.LastPoint?.X,
            t.LastPositionAt,
            [.. stopsByTrip[t.TripId].Select(s => new OpenTripStopVm(
                s.TripStopId,
                s.Sequence,
                s.Name,
                s.Status,
                s.ActualArrivalAt,
                s.PlannedArrivalTo,
                s.DelayAlertedAt,
                s.OutsideSinceAt,
                s.Point.Y,
                s.Point.X))]))];
    }

    public async Task<bool> ArmAsync(Guid tripId, CancellationToken cancellationToken)
    {
        if (!trips.TryGetValue(tripId, out var trip) || trip.ArmedAt.HasValue)
        {
            return false;
        }

        var originGeom = await ResolveOriginGeomAsync(trip, cancellationToken);
        await SnapshotArrivalGeometryAsync(trip, cancellationToken);

        // The server clock deliberately: arming is the system noticing a trip, not a measurement of
        // anything the vehicle did. Every MEASURED stamp takes the fix's own DeviceDateTime.
        dirty |= trip.Arm(originGeom, DateTimeOffset.UtcNow);
        return true;
    }

    public bool IsInsideOrigin(Guid tripId, double latitude, double longitude)
        => trips.TryGetValue(tripId, out var trip)
            && trip.OriginGeom is { } geom
            && geom.Contains(NewPoint(latitude, longitude));

    public IReadOnlyCollection<Guid> StopsContainingPoint(Guid tripId, double latitude, double longitude)
    {
        if (!stopsByTrip.TryGetValue(tripId, out var stops))
        {
            return [];
        }

        var point = NewPoint(latitude, longitude);
        return [.. stops
            .Where(s => s.ArrivalGeom is not null && s.ArrivalGeom.Contains(point))
            .OrderBy(s => s.Sequence)
            .Select(s => s.TripStopId)];
    }

    public void RecordOriginVisit(Guid tripId, DateTimeOffset? arrivedAt, DateTimeOffset? departedAt)
    {
        if (trips.TryGetValue(tripId, out var trip))
        {
            dirty |= trip.RecordOriginVisit(arrivedAt, departedAt);
        }
    }

    public void SetOriginOutsideSince(Guid tripId, DateTimeOffset? outsideSinceAt)
    {
        if (trips.TryGetValue(tripId, out var trip))
        {
            dirty |= trip.SetOriginOutsideSince(outsideSinceAt);
        }
    }

    public bool TryRecordOriginDeparture(Guid tripId, DateTimeOffset departedAt)
    {
        if (!trips.TryGetValue(tripId, out var trip) || !trip.TryRecordOriginDeparture(departedAt))
        {
            return false;
        }

        dirty = true;
        return true;
    }

    public bool TryAdvanceProgress(
        Guid tripId, double latitude, double longitude, DateTimeOffset positionAt, double addedDistanceMeters)
    {
        if (!trips.TryGetValue(tripId, out var trip)
            || !trip.TryAdvanceProgress(TripGeometryFactory.Point(latitude, longitude), positionAt, addedDistanceMeters))
        {
            return false;
        }

        dirty = true;
        return true;
    }

    public void SetDeviationState(Guid tripId, DateTimeOffset? deviationOpenedAt, int consecutiveOutsideFixes)
    {
        if (trips.TryGetValue(tripId, out var trip))
        {
            dirty |= trip.SetDeviationState(deviationOpenedAt, consecutiveOutsideFixes);
        }
    }

    public async Task FlushAsync(CancellationToken cancellationToken)
    {
        // `dirty` tracks what THIS unit buffered. The tracker check catches the rest: an
        // event-producing writer sharing this request may already have committed our changes as part
        // of its own save, and it may equally have left its own pending.
        if (!dirty && !context.ChangeTracker.HasChanges())
        {
            return;
        }

        try
        {
            await context.SaveChangesAsync(cancellationToken);
            dirty = false;
        }
        catch (DbUpdateConcurrencyException exception)
        {
            // Another writer moved one of these rows — a dispatcher override landing mid-batch is the
            // ordinary cause. Position processing is best-effort by contract, so the buffered state
            // is dropped and the next cycle re-reads.
            //
            // Reloading and retrying here would be worse: it would replay this fix's measurements on
            // top of whatever the other writer just wrote, which is the lost update the concurrency
            // token exists to prevent.
            logger.LogWarning(
                exception, "Trip state changed concurrently mid-batch; the buffered fix is dropped and re-read next cycle");

            // Only the rows the database actually rejected. EF names them, so there is no need to
            // guess — and guessing wide would take the rest of the batch down with them.
            foreach (var conflicted in exception.Entries.Select(e => e.Entity).OfType<Trip>().Select(t => t.TripId).Distinct().ToList())
            {
                Discard(conflicted);
            }

            foreach (var entry in exception.Entries.Where(e => e.State != EntityState.Detached))
            {
                entry.State = EntityState.Detached;
            }

            dirty = false;
        }
    }

    /// <summary>
    /// Drops ONE trip's buffered state from the change tracker and from the working set. The context
    /// is request-scoped, so anything left tracked after a failed fix is replayed by the next save —
    /// including by a completely unrelated trip later in the same batch.
    /// <para>
    /// Removing it from the dictionaries is what retires it for the rest of the batch: every mutator
    /// looks the trip up first, so a discarded trip's later fixes become no-ops while its fleetmates
    /// carry on untouched.
    /// </para>
    /// </summary>
    public void Discard(Guid tripId)
    {
        if (trips.Remove(tripId, out var trip))
        {
            trip.ClearDomainEvents();
            context.Trips.Entry(trip).State = EntityState.Detached;
        }

        if (stopsByTrip.Remove(tripId, out var stops))
        {
            foreach (var stop in stops)
            {
                stop.ClearDomainEvents();
                context.TripStops.Entry(stop).State = EntityState.Detached;
            }
        }

        // Another trip in this batch may still be holding buffered changes of its own.
        dirty = context.ChangeTracker.HasChanges();
    }

    private static NetTopologySuite.Geometries.Point NewPoint(double latitude, double longitude)
        => new(longitude, latitude) { SRID = TripGeometryDefaults.Srid };

    /// <summary>
    /// The origin zone: the linked geofence's real shape, or the origin point buffered by
    /// <c>OriginRadiusMeters</c>. Read ONCE, which is what makes a watched trip immune to a geofence
    /// edited between arming and the vehicle's arrival (spec 11a §2).
    /// </summary>
    private async Task<NetTopologySuite.Geometries.Polygon> ResolveOriginGeomAsync(Trip trip, CancellationToken cancellationToken)
    {
        if (trip.OriginGeofenceId is { } geofenceId)
        {
            var geom = await context.Geofences
                .Where(g => g.GeofenceId == geofenceId && g.AccountId == trip.AccountId)
                .Select(g => g.Geom)
                .FirstOrDefaultAsync(cancellationToken);

            if (geom is not null)
            {
                return geom;
            }
        }

        return TripGeometryFactory.Buffer(trip.OriginPoint, trip.OriginRadiusMeters);
    }

    /// <summary>
    /// Freezes each stop's arrival geometry. Only NULL geometries are filled: a stop already
    /// snapshotted keeps the shape its detection has been judged against.
    /// </summary>
    private async Task SnapshotArrivalGeometryAsync(Trip trip, CancellationToken cancellationToken)
    {
        var pending = stopsByTrip.TryGetValue(trip.TripId, out var loaded)
            ? loaded.FindAll(s => s.ArrivalGeom is null)
            : [];

        if (pending.Count == 0)
        {
            return;
        }

        var geofenceIds = pending
            .Where(s => s.GeofenceId.HasValue)
            .Select(s => s.GeofenceId!.Value)
            .Distinct()
            .ToList();

        var geofences = geofenceIds.Count == 0
            ? []
            : await context.Geofences
                .Where(g => geofenceIds.Contains(g.GeofenceId) && g.AccountId == trip.AccountId)
                .ToDictionaryAsync(g => g.GeofenceId, g => g.Geom, cancellationToken);

        foreach (var stop in pending)
        {
            stop.ArrivalGeom = stop.GeofenceId is { } geofenceId && geofences.TryGetValue(geofenceId, out var geom)
                ? geom
                : TripGeometryFactory.Buffer(stop.Point, stop.ArrivalRadiusMeters);
            dirty = true;
        }
    }
}
