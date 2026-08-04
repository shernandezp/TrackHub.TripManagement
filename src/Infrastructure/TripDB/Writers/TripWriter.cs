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
using Common.Application.Interfaces;
using TrackHub.TripManagement.Infrastructure.TripDB.Events;
using TrackHub.TripManagement.Infrastructure.TripDB.Readers;

namespace TrackHub.TripManagement.Infrastructure.TripDB.Writers;

/// <summary>
/// Write side of the trip aggregate: CRUD plus the lifecycle transitions.
/// <para>
/// <b>Every query that leads to a mutation is <c>AsTracking()</c>, and nothing here calls
/// <c>Attach</c>.</b> The context is registered <c>NoTracking</c>, so a plain query hands back a
/// FRESH instance each time; attaching a second instance of a row the change tracker already holds
/// throws. That is not a rare race — it is the shape of every multi-write request this module has:
/// detection arms a trip and then starts it, the odometer runs and then the deviation state moves,
/// the backfill arms and then replays, both importers create and then declare. Each of those failed
/// on its second write, and Router swallows the exception, so zero-touch simply never happened.
/// <c>AsTracking()</c> resolves identity instead: an already-tracked row comes back as the SAME
/// instance carrying the writes this request has already made, and a fresh one starts being tracked.
/// </para>
/// </summary>
public sealed class TripWriter(IApplicationDbContext context, IUser user) : ITripWriter
{
    public async Task<TripVm> CreateTripAsync(TripDto trip, Guid accountId, CancellationToken cancellationToken)
    {
        await GuardUniqueCodeAsync(accountId, trip.Code, null, cancellationToken);
        await GuardUniqueExternalReferenceAsync(accountId, trip.ExternalReference, null, cancellationToken);

        var entity = new Trip
        {
            AccountId = accountId,
            Code = trip.Code,
            Status = TripStatuses.Created,
            TransporterId = trip.TransporterId,
            DriverId = trip.DriverId,
            ServiceOrderId = trip.ServiceOrderId,
            ExternalReference = trip.ExternalReference,
            CustomerName = trip.CustomerName,
            OriginName = trip.OriginName,
            OriginPoint = TripGeometryFactory.Point(trip.OriginLatitude, trip.OriginLongitude),
            OriginGeofenceId = trip.OriginGeofenceId,
            OriginRadiusMeters = trip.OriginRadiusMeters,
            PlannedStartAt = trip.PlannedStartAt,
            PlannedEndAt = trip.PlannedEndAt,
            Notes = trip.Notes,
            TollVehicleClass = trip.TollVehicleClass,
        };

        entity.AddDomainEvent(new TripDomainEvent(TripEventTypes.TripCreated, accountId, entity.TripId));
        await context.Trips.AddAsync(entity, cancellationToken);
        AddAuditEvent(accountId, "CreateTrip", entity.TripId);
        await context.SaveChangesAsync(cancellationToken);

        return TripMapper.ToVm(entity, 0);
    }

    public async Task UpdateTripAsync(Guid tripId, TripDto trip, Guid accountId, CancellationToken cancellationToken)
    {
        var entity = await FindAsync(tripId, accountId, cancellationToken);

        // A terminal trip is history: it is cancelled or completed and must not be re-edited.
        if (TripStatuses.IsTerminal(entity.Status))
        {
            throw ConflictException.WithCode(TripErrorCodes.TripAlreadyTerminal);
        }

        await GuardUniqueCodeAsync(accountId, trip.Code, tripId, cancellationToken);
        await GuardUniqueExternalReferenceAsync(accountId, trip.ExternalReference, tripId, cancellationToken);

        // What detection is measuring against must not move underneath it (spec 11a §12.4). Which
        // unit runs the trip and where its origin zone is are the two inputs to the arm/auto-start
        // decision, so once execution has begun they are frozen: re-pointing a running trip would
        // silently change the meaning of measurements already taken.
        var repointed = trip.TransporterId != entity.TransporterId
            || trip.OriginGeofenceId != entity.OriginGeofenceId
            || trip.OriginRadiusMeters != entity.OriginRadiusMeters
            || !CoordinatesMatch(entity.OriginPoint, trip.OriginLatitude, trip.OriginLongitude);

        if (repointed && TripStatuses.HasStarted(entity.Status))
        {
            throw ConflictException.WithCode(TripErrorCodes.TripArmed);
        }

        entity.Code = trip.Code;
        entity.TransporterId = trip.TransporterId;
        entity.DriverId = trip.DriverId;
        entity.ServiceOrderId = trip.ServiceOrderId;
        entity.ExternalReference = trip.ExternalReference;
        entity.CustomerName = trip.CustomerName;
        entity.OriginName = trip.OriginName;
        entity.OriginPoint = TripGeometryFactory.Point(trip.OriginLatitude, trip.OriginLongitude);
        entity.OriginGeofenceId = trip.OriginGeofenceId;
        entity.OriginRadiusMeters = trip.OriginRadiusMeters;
        entity.PlannedStartAt = trip.PlannedStartAt;
        entity.PlannedEndAt = trip.PlannedEndAt;
        entity.Notes = trip.Notes;
        entity.TollVehicleClass = trip.TollVehicleClass;

        // A still-Created trip that was merely armed is safe to re-point — nothing has been measured
        // yet. Disarming it here is what makes the edit take effect: the next detection cycle
        // re-arms it against the new unit and the new origin, so an armed trip is never left
        // watching geometry the dispatcher has already replaced.
        if (repointed)
        {
            await DisarmAsync(entity, cancellationToken);
        }

        entity.AddDomainEvent(new TripDomainEvent(TripEventTypes.TripUpdated, accountId, entity.TripId));
        AddAuditEvent(accountId, "UpdateTrip", entity.TripId);

        try
        {
            await context.SaveChangesAsync(cancellationToken);
        }
        catch (DbUpdateConcurrencyException)
        {
            // xmin moved between the read and the write: detection auto-started this trip while the
            // dispatcher was editing it. The status guard above was decided against a row that no
            // longer exists in that state — so the edit is refused with the same conflict a started
            // trip would have raised outright, rather than surfacing as a 500.
            throw ConflictException.WithCode(TripErrorCodes.TripArmed);
        }
    }

    public async Task DeleteTripAsync(Guid tripId, Guid accountId, CancellationToken cancellationToken)
    {
        var entity = await FindAsync(tripId, accountId, cancellationToken);

        // Trip history is permanent (spec 11 section 5, acceptance 16): only a Created trip that
        // never produced an event may be deleted. Anything else must be cancelled instead, so
        // stops, events, POD and documents are never orphaned.
        //
        // Job-sourced events are excluded for the same reason as in TripEventWriter.HasEventsAsync:
        // a trip-schedule-reminder orphans nothing, so counting it would block deletion of a trip
        // that never actually ran.
        var hasHistory = !string.Equals(entity.Status, TripStatuses.Created, StringComparison.Ordinal)
            || await context.TripEvents.AnyAsync(
                e => e.TripId == tripId && e.Source != TripEventSources.Job, cancellationToken);

        if (hasHistory)
        {
            throw ConflictException.WithCode(TripErrorCodes.TripHasHistory);
        }

        AddAuditEvent(accountId, "DeleteTrip", entity.TripId);
        context.Trips.Remove(entity);
        await context.SaveChangesAsync(cancellationToken);
    }

    /// <summary>
    /// The single lifecycle funnel. Status change, timestamps and the timeline event are ONE save:
    /// automation and a dispatcher can now transition the same trip at the same instant, and the
    /// unique idempotency index is what makes exactly one of them win. Split across two saves, a
    /// duplicate transition wrote the status change and only then discovered the event was already
    /// there — leaving a trip started twice with one event to show for it (spec 11a §12.1).
    /// </summary>
    public async Task<bool> TransitionTripAsync(
        Guid tripId,
        Guid accountId,
        string toStatus,
        string eventType,
        string source,
        string idempotencyKey,
        string? payloadJson,
        string? reason,
        bool force,
        DateTimeOffset? measuredAt,
        CancellationToken cancellationToken)
    {
        var entity = await FindAsync(tripId, accountId, cancellationToken);

        // Idempotency BEFORE the matrix guard, the RecordStopProgressAsync shape: a replayed
        // transition must be a silent success, not TRIP_INVALID_TRANSITION raised against a status
        // the server itself already moved the trip to.
        if (await context.TripEvents.AnyAsync(
                e => e.AccountId == accountId && e.IdempotencyKey == idempotencyKey, cancellationToken))
        {
            return false;
        }

        if (!TripStatuses.CanTransition(entity.Status, toStatus))
        {
            throw ConflictException.WithCode(TripErrorCodes.InvalidTransition);
        }

        var startingNow = string.Equals(toStatus, TripStatuses.InProgress, StringComparison.Ordinal)
            && string.Equals(entity.Status, TripStatuses.Created, StringComparison.Ordinal);

        if (string.Equals(toStatus, TripStatuses.Completed, StringComparison.Ordinal) && !force)
        {
            var openStops = await context.TripStops.AnyAsync(
                s => s.TripId == tripId
                    && s.Status != TripStopStatuses.Departed
                    && s.Status != TripStopStatuses.Skipped,
                cancellationToken);

            if (openStops)
            {
                throw ConflictException.WithCode(TripErrorCodes.StopsNotComplete);
            }
        }

        // Snapshotted BEFORE the transition is applied, and restored field-by-field if the save is
        // refused. Which stops it filled has to be remembered too — see RevertAsync.
        var filledStops = startingNow
            ? await SnapshotArrivalGeometryAsync(entity, cancellationToken)
            : [];

        var revert = entity.ApplyTransition(toStatus, measuredAt, reason);

        var tripEvent = new TripEvent
        {
            AccountId = accountId,
            TripId = tripId,
            EventType = eventType,
            OccurredAt = measuredAt ?? DateTimeOffset.UtcNow,
            Source = source,
            PayloadJson = payloadJson,
            IdempotencyKey = idempotencyKey,
        };

        await context.TripEvents.AddAsync(tripEvent, cancellationToken);
        var domainEvent = new TripDomainEvent(eventType, accountId, entity.TripId);
        entity.AddDomainEvent(domainEvent);
        var auditEvent = AddAuditEvent(accountId, $"TransitionTrip:{toStatus}", entity.TripId, reason);

        try
        {
            await context.SaveChangesAsync(cancellationToken);
            return true;
        }
        catch (DbUpdateException exception) when (UniqueViolation.Matches(exception, "ux_trip_events_idempotencykey"))
        {
            // The other writer got there first: this transition wrote nothing and must leave nothing.
            Undo(tripEvent, entity, domainEvent, auditEvent, revert, filledStops);
            return false;
        }
        catch (DbUpdateException exception) when (UniqueViolation.Matches(exception, "ux_trips_transporterid_inprogress"))
        {
            // One physical unit, one trip at a time. Reachable from a manual Start against a unit
            // already running something else, and from two auto-starts racing on one transporter.
            Undo(tripEvent, entity, domainEvent, auditEvent, revert, filledStops);
            throw ConflictException.WithCode(TripErrorCodes.TransporterBusy);
        }
        catch (DbUpdateConcurrencyException)
        {
            // xmin moved: the trip is no longer in the status the matrix was checked against. The
            // caller must re-read rather than be told a stale decision succeeded.
            Undo(tripEvent, entity, domainEvent, auditEvent, revert, filledStops);
            throw ConflictException.WithCode(TripErrorCodes.InvalidTransition);
        }
    }

    public async Task<bool> ArmTripAsync(Guid tripId, Guid accountId, CancellationToken cancellationToken)
    {
        var entity = await context.Trips
            .AsTracking()
            .FirstOrDefaultAsync(t => t.TripId == tripId && t.AccountId == accountId, cancellationToken);

        if (entity is null || entity.ArmedAt.HasValue)
        {
            return false;
        }

        var originGeom = await ResolveOriginGeomAsync(entity, cancellationToken);
        await SnapshotArrivalGeometryAsync(entity, cancellationToken);

        if (!entity.Arm(originGeom, DateTimeOffset.UtcNow))
        {
            return false;
        }

        // No TripEvent and no domain event on purpose: arming is the system noticing a trip, not the
        // trip doing anything. A row here would make every armed-and-never-run trip undeletable.
        await context.SaveChangesAsync(cancellationToken);
        return true;
    }

    public async Task SetOriginVisitAsync(
        Guid tripId,
        Guid accountId,
        DateTimeOffset? arrivedAt,
        DateTimeOffset? departedAt,
        CancellationToken cancellationToken)
    {
        var entity = await context.Trips
            .AsTracking()
            .FirstOrDefaultAsync(t => t.TripId == tripId && t.AccountId == accountId, cancellationToken);

        if (entity is null || !entity.RecordOriginVisit(arrivedAt, departedAt))
        {
            return;
        }

        await context.SaveChangesAsync(cancellationToken);
    }

    public async Task<TripAssignmentVm> AssignTripAsync(Guid tripId, Guid accountId, Guid driverId, Guid? transporterId, CancellationToken cancellationToken)
    {
        var entity = await FindAsync(tripId, accountId, cancellationToken);

        if (TripStatuses.IsTerminal(entity.Status))
        {
            throw ConflictException.WithCode(TripErrorCodes.TripAlreadyTerminal);
        }

        var now = DateTimeOffset.UtcNow;

        // Exactly one Active assignment per trip: the prior one is ENDED, never deleted, so the
        // handover history survives.
        var current = await context.TripAssignments
            .AsTracking()
            .Where(a => a.TripId == tripId && a.Status == TripAssignmentStatuses.Active)
            .ToListAsync(cancellationToken);

        foreach (var previous in current)
        {
            previous.Status = TripAssignmentStatuses.Ended;
            previous.EndedAt = now;
        }

        var assignment = new TripAssignment
        {
            AccountId = accountId,
            TripId = tripId,
            DriverId = driverId,
            TransporterId = transporterId ?? entity.TransporterId,
            Status = TripAssignmentStatuses.Active,
            AssignedAt = now,
        };

        entity.DriverId = driverId;
        if (transporterId is { } newTransporterId)
        {
            entity.TransporterId = newTransporterId;
        }

        assignment.AddDomainEvent(new TripDomainEvent(
            TripEventTypes.TripAssigned, accountId, tripId, assignment.TripAssignmentId));

        await context.TripAssignments.AddAsync(assignment, cancellationToken);
        AddAuditEvent(accountId, "AssignTrip", tripId);

        try
        {
            await context.SaveChangesAsync(cancellationToken);
        }
        catch (DbUpdateException exception) when (UniqueViolation.Matches(exception, "ux_trip_assignments_active_per_trip"))
        {
            // ux_trip_assignments_active_per_trip: a concurrent assign already won.
            throw ConflictException.WithCode(TripErrorCodes.DriverNotAssignable);
        }

        return TripMapper.ToVm(assignment);
    }

    /// <summary>
    /// Freezes each stop's arrival geometry (spec 11 §18.4): the linked geofence's polygon when
    /// <c>GeofenceId</c> is set, otherwise the stop point buffered by its radius. Reading the
    /// geofence ONCE is exactly what makes a running trip immune to a geofence edited mid-execution.
    /// <para>
    /// Only null geometries are filled. A stop already snapshotted at arming keeps the shape its
    /// detection has been judged against — re-reading it at start would reintroduce the very
    /// mid-flight mutability the snapshot exists to prevent.
    /// </para>
    /// </summary>
    private async Task<IReadOnlyCollection<TripStop>> SnapshotArrivalGeometryAsync(Trip trip, CancellationToken cancellationToken)
    {
        var stops = await context.TripStops
            .AsTracking()
            .Where(s => s.TripId == trip.TripId && s.AccountId == trip.AccountId && s.ArrivalGeom == null)
            .ToListAsync(cancellationToken);

        if (stops.Count == 0)
        {
            return [];
        }

        var geofenceIds = stops.Where(s => s.GeofenceId.HasValue)
            .Select(s => s.GeofenceId!.Value)
            .Distinct()
            .ToList();

        var geofences = geofenceIds.Count == 0
            ? []
            : await context.Geofences
                .Where(g => geofenceIds.Contains(g.GeofenceId) && g.AccountId == trip.AccountId)
                .ToDictionaryAsync(g => g.GeofenceId, g => g.Geom, cancellationToken);

        foreach (var stop in stops)
        {
            stop.ArrivalGeom = stop.GeofenceId is { } geofenceId && geofences.TryGetValue(geofenceId, out var geom)
                ? geom
                : TripGeometryFactory.Buffer(stop.Point, stop.ArrivalRadiusMeters);
        }

        return stops;
    }

    /// <summary>
    /// The origin zone: the linked geofence's real shape, or the origin point buffered by
    /// <c>OriginRadiusMeters</c> for a POI/ad-hoc point. Same rule as a stop, so a trip's ends are
    /// measured the same way as its middle.
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
    /// Drops the arming snapshots so the next detection cycle rebuilds them against the edited plan.
    /// Only ever reached for a trip that has not started, so no measurement is being discarded.
    /// </summary>
    private async Task DisarmAsync(Trip trip, CancellationToken cancellationToken)
    {
        trip.Disarm();

        var stops = await context.TripStops
            .AsTracking()
            .Where(s => s.TripId == trip.TripId && s.AccountId == trip.AccountId && s.ArrivalGeom != null)
            .ToListAsync(cancellationToken);

        foreach (var stop in stops)
        {
            stop.ArrivalGeom = null;
        }
    }

    /// <summary>
    /// Compared at the precision the portal actually sends (six decimals, ~0.1 m): a float-exact
    /// test would read every save as a re-point and disarm trips nobody edited.
    /// </summary>
    private static bool CoordinatesMatch(NetTopologySuite.Geometries.Point origin, double latitude, double longitude)
        => Math.Abs(origin.Y - latitude) < 1e-6 && Math.Abs(origin.X - longitude) < 1e-6;

    /// <summary>
    /// Undoes exactly what the rejected transition staged, and nothing else.
    /// <para>
    /// The rows this operation INSERTED are detached — they were never persisted, and the context is
    /// request-scoped, so leaving them would replay a dead insert and an audit row for a transition
    /// the database refused. The rows it MUTATED are reverted field-by-field instead.
    /// </para>
    /// <para>
    /// That distinction is the whole point. Detaching the trip was simpler and wrong: the same row
    /// carries the odometer, the debounce clocks and the deviation counter that the detection pass
    /// buffered for this very fix, so undoing a losing transition by detaching silently discarded
    /// measurements that had nothing to do with it. Reverting only <see cref="TripTransitionRevert"/>
    /// leaves those pending, and the next save commits them as it should.
    /// </para>
    /// </summary>
    private void Undo(
        TripEvent tripEvent,
        Trip trip,
        TripDomainEvent domainEvent,
        AuditEvent auditEvent,
        TripTransitionRevert revert,
        IReadOnlyCollection<TripStop> filledStops)
    {
        context.TripEvents.Entry(tripEvent).State = EntityState.Detached;
        context.AuditEvents.Entry(auditEvent).State = EntityState.Detached;

        // Only the event THIS call queued: another operation in the same request may legitimately
        // have queued its own on the same row.
        trip.RemoveDomainEvent(domainEvent);
        trip.RevertTransition(revert);

        // A start that did not happen must not leave the route armed. Only the stops this call
        // actually filled are cleared — one snapshotted earlier, by arming, is a measurement in its
        // own right and detection is already judging arrivals against it.
        foreach (var stop in filledStops)
        {
            stop.ArrivalGeom = null;
        }
    }

    private async Task<Trip> FindAsync(Guid tripId, Guid accountId, CancellationToken cancellationToken)
        => await context.Trips.AsTracking().FirstOrDefaultAsync(t => t.TripId == tripId && t.AccountId == accountId, cancellationToken)
            ?? throw new NotFoundException($"{tripId}", nameof(Trip));

    private async Task GuardUniqueCodeAsync(Guid accountId, string code, Guid? excludeTripId, CancellationToken cancellationToken)
    {
        var duplicate = await context.Trips.AnyAsync(
            t => t.AccountId == accountId && t.Code == code && (excludeTripId == null || t.TripId != excludeTripId),
            cancellationToken);

        if (duplicate)
        {
            throw ConflictException.WithCode(TripErrorCodes.DuplicateTripCode);
        }
    }

    private async Task GuardUniqueExternalReferenceAsync(Guid accountId, string? externalReference, Guid? excludeTripId, CancellationToken cancellationToken)
    {
        if (string.IsNullOrWhiteSpace(externalReference))
        {
            return;
        }

        var duplicate = await context.Trips.AnyAsync(
            t => t.AccountId == accountId
                && t.ExternalReference == externalReference
                && (excludeTripId == null || t.TripId != excludeTripId),
            cancellationToken);

        if (duplicate)
        {
            throw ConflictException.WithCode(TripErrorCodes.DuplicateExternalReference);
        }
    }

    private AuditEvent AddAuditEvent(Guid accountId, string action, Guid tripId, string? reason = null)
        => context.AuditEvents.Add(new AuditEvent(
            accountId,
            user.PrincipalType.ToString(),
            user.UserId?.ToString() ?? user.ClientId ?? user.SubjectId ?? string.Empty,
            action,
            TripSharing.ResourceType,
            tripId.ToString(),
            "Success",
            null,
            null,
            reason,
            null,
            null,
            user.CorrelationId)).Entity;
}
