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

using Common.Infrastructure;
using NetTopologySuite.Geometries;

namespace TrackHub.TripManagement.Infrastructure.TripDB.Entities;

/// <summary>
/// The dispatch aggregate root (spec 11 section 6.1, 18.3). A dispatch code is a field on this
/// entity, not a parent aggregate.
/// </summary>
public sealed class Trip : BaseAuditableEntity
{
    public Guid TripId { get; set; } = Guid.NewGuid();
    public Guid AccountId { get; set; }
    public string Code { get; set; } = string.Empty;
    public string Status { get; set; } = TripStatuses.Created;
    public Guid TransporterId { get; set; }
    public Guid? DriverId { get; set; }
    public Guid? RoutePlanId { get; set; }
    public Guid? ServiceOrderId { get; set; }
    public string? ExternalReference { get; set; }
    public string? CustomerName { get; set; }
    public string OriginName { get; set; } = string.Empty;
    public Point OriginPoint { get; set; } = default!;

    /// <summary>
    /// Source only, like <see cref="TripStop.GeofenceId"/>: the live geofence is read exactly once,
    /// at arming, to build <see cref="OriginGeom"/>.
    /// </summary>
    public Guid? OriginGeofenceId { get; set; }

    /// <summary>Buffer radius for a POI/point origin — symmetric with <c>TripStop.ArrivalRadiusMeters</c>.</summary>
    public int OriginRadiusMeters { get; set; } = TripGeometryDefaults.ArrivalRadiusMeters;

    /// <summary>
    /// SNAPSHOT taken when the trip is ARMED (spec 11a §6.1), not when it starts: origin arrival is
    /// what CAUSES the start, so the geometry has to be frozen before execution begins. Geofence
    /// polygon when <see cref="OriginGeofenceId"/> is set, otherwise <see cref="OriginPoint"/>
    /// buffered by <see cref="OriginRadiusMeters"/>.
    /// </summary>
    public Polygon? OriginGeom { get; set; }

    /// <summary>
    /// When the trip entered the detection working set. Stamped once, and deliberately writes NO
    /// <c>TripEvent</c>: a trip that was armed and never ran must stay deletable (acceptance 16).
    /// </summary>
    public DateTimeOffset? ArmedAt { get; set; }

    /// <summary>
    /// MEASURED: the first fix inside <see cref="OriginGeom"/>, which is also the auto-start instant
    /// and the moment loading begins. Always a <c>DeviceDateTime</c>, never the server clock.
    /// </summary>
    public DateTimeOffset? OriginArrivedAt { get; set; }

    /// <summary>MEASURED: debounced exit from <see cref="OriginGeom"/> — loading ends, transit begins.</summary>
    public DateTimeOffset? OriginDepartedAt { get; set; }

    /// <summary>
    /// Persisted 30 s exit-debounce clock for the origin, under the same law as
    /// <see cref="TripStop.OutsideSinceAt"/>: Router pushes one fix per transporter per call, so a
    /// clock held in memory is discarded before the window can ever elapse.
    /// </summary>
    public DateTimeOffset? OriginOutsideSinceAt { get; set; }

    public DateTimeOffset PlannedStartAt { get; set; }
    public DateTimeOffset? PlannedEndAt { get; set; }
    public DateTimeOffset? ActualStartAt { get; set; }
    public DateTimeOffset? ActualEndAt { get; set; }
    public string? Notes { get; set; }
    public DateTimeOffset? LastPositionAt { get; set; }
    public Point? LastPoint { get; set; }
    public double ActualDistanceMeters { get; set; }
    public string? TollVehicleClass { get; set; }

    /// <summary>
    /// Stamped only after a TripRouteDeviation alert was successfully emitted, so a failed
    /// emission is retried on the next cycle rather than silently swallowed (spec 11 section 7.4).
    /// </summary>
    public DateTimeOffset? DeviationOpenedAt { get; set; }

    /// <summary>
    /// Run length of consecutive out-of-corridor fixes, PERSISTED because Router pushes exactly one
    /// position per transporter per call: an in-memory counter rebuilt per request could never reach
    /// the three-fix threshold, which is why corridor deviation never fired (spec 11 section 7.4).
    /// Reset to 0 by any fix inside the corridor.
    /// </summary>
    public int ConsecutiveOutsideFixes { get; set; }
    public string? CancellationReason { get; set; }

    // ---------------------------------------------------------------- behaviour
    //
    // The state transitions live HERE rather than as field assignments spread across the writers,
    // and that is what lets two very different callers share one set of rules: the command writers,
    // which load-mutate-save one row per request, and the detection unit of work, which loads a
    // batch once and commits per fix. Every rule below — first-write-wins on a measurement, the
    // out-of-order guard, "an episode must be able to close" — used to exist only as a convention
    // inside whichever writer happened to touch the column.
    //
    // Each mutator answers whether it CHANGED anything, so a caller batching writes can tell an
    // idle fix (a truck parked inside its origin zone moves nothing at all) from a real one and
    // skip the save entirely.

    /// <summary>
    /// Freezes the origin zone and marks the trip watched. Idempotent, and deliberately writes no
    /// event: an armed-but-never-run trip must stay deletable (acceptance 16).
    /// </summary>
    public bool Arm(Polygon originGeom, DateTimeOffset armedAt)
    {
        if (ArmedAt.HasValue)
        {
            return false;
        }

        OriginGeom = originGeom;
        ArmedAt = armedAt;
        return true;
    }

    /// <summary>
    /// The measured origin visit. <b>First write wins</b> (acceptance 12): a visit replayed from
    /// Geofencing's history must never overwrite what the live pipeline already measured.
    /// </summary>
    public bool RecordOriginVisit(DateTimeOffset? arrivedAt, DateTimeOffset? departedAt)
    {
        var changed = false;

        if (arrivedAt.HasValue && OriginArrivedAt is null)
        {
            OriginArrivedAt = arrivedAt;
            changed = true;
        }

        if (departedAt.HasValue && OriginDepartedAt is null)
        {
            OriginDepartedAt = departedAt;
            changed = true;
        }

        return changed;
    }

    /// <summary>The persisted origin exit-debounce clock; null restarts it from zero.</summary>
    public bool SetOriginOutsideSince(DateTimeOffset? outsideSinceAt)
    {
        if (OriginOutsideSinceAt == outsideSinceAt)
        {
            return false;
        }

        OriginOutsideSinceAt = outsideSinceAt;
        return true;
    }

    /// <summary>Stamps the debounced origin exit — loading ends, transit begins — and clears the clock.</summary>
    public bool TryRecordOriginDeparture(DateTimeOffset departedAt)
    {
        if (OriginDepartedAt.HasValue)
        {
            return false;
        }

        OriginDepartedAt = departedAt;
        OriginOutsideSinceAt = null;
        return true;
    }

    /// <summary>
    /// Odometer and last-seen point. Rejects an out-of-order or replayed fix rather than rewinding,
    /// and REPORTS the rejection: the deviation run length is a plain counter, so a redelivered
    /// out-of-corridor fix would otherwise climb to the threshold by itself and open a false episode.
    /// </summary>
    public bool TryAdvanceProgress(Point point, DateTimeOffset positionAt, double addedDistanceMeters)
    {
        if (LastPositionAt is { } last && positionAt <= last)
        {
            return false;
        }

        LastPoint = point;
        LastPositionAt = positionAt;
        ActualDistanceMeters += Math.Max(addedDistanceMeters, 0d);
        return true;
    }

    /// <summary>
    /// Deviation episode state. A plain assignment, never first-write-wins: an episode has to be able
    /// to CLOSE on re-entry so that a later departure opens a new one with a new key (acceptance 14).
    /// </summary>
    public bool SetDeviationState(DateTimeOffset? deviationOpenedAt, int consecutiveOutsideFixes)
    {
        if (DeviationOpenedAt == deviationOpenedAt && ConsecutiveOutsideFixes == consecutiveOutsideFixes)
        {
            return false;
        }

        DeviationOpenedAt = deviationOpenedAt;
        ConsecutiveOutsideFixes = consecutiveOutsideFixes;
        return true;
    }

    /// <summary>
    /// Applies a lifecycle transition and hands back everything needed to undo it.
    /// <para>
    /// The caller owns the matrix check and the persistence; this owns which fields move. The revert
    /// token exists because the unique idempotency index can still refuse the save, and the trip row
    /// may be carrying OTHER pending changes from the same fix — the odometer, a debounce clock.
    /// Detaching the row to undo a rejected transition, which is what the writer used to do, threw
    /// those away silently.
    /// </para>
    /// </summary>
    public TripTransitionRevert ApplyTransition(string toStatus, DateTimeOffset? measuredAt, string? reason)
    {
        var revert = new TripTransitionRevert(Status, ActualStartAt, ActualEndAt, CancellationReason);

        if (string.Equals(toStatus, TripStatuses.InProgress, StringComparison.Ordinal)
            && string.Equals(Status, TripStatuses.Created, StringComparison.Ordinal))
        {
            // The MEASURED start when there is one: OriginArrivedAt on the detection path, the
            // replayed visit on the backfill path. First write wins (acceptance 12).
            ActualStartAt ??= OriginArrivedAt ?? measuredAt ?? DateTimeOffset.UtcNow;
        }

        if (TripStatuses.IsTerminal(toStatus))
        {
            ActualEndAt ??= measuredAt ?? DateTimeOffset.UtcNow;
        }

        // The reason reaches the audit row and the timeline event for EVERY transition, but it only
        // becomes a CANCELLATION reason when the trip is actually cancelled or aborted. A forced
        // completion passes a reason too, and stamping it here labelled a completed trip with a
        // cancellation it never had.
        if (!string.IsNullOrWhiteSpace(reason)
            && (string.Equals(toStatus, TripStatuses.Cancelled, StringComparison.Ordinal)
                || string.Equals(toStatus, TripStatuses.Aborted, StringComparison.Ordinal)))
        {
            CancellationReason = reason;
        }

        Status = toStatus;
        return revert;
    }

    /// <summary>
    /// Undoes exactly the fields <see cref="ApplyTransition"/> moved, leaving every other pending
    /// change on this row intact.
    /// </summary>
    public void RevertTransition(TripTransitionRevert revert)
    {
        Status = revert.Status;
        ActualStartAt = revert.ActualStartAt;
        ActualEndAt = revert.ActualEndAt;
        CancellationReason = revert.CancellationReason;
    }

    /// <summary>Drops the arming snapshots so the next detection cycle rebuilds them against the edited plan.</summary>
    public void Disarm()
    {
        ArmedAt = null;
        OriginGeom = null;
        OriginOutsideSinceAt = null;
    }
}

/// <summary>The trip fields a lifecycle transition moves, captured so it can be undone in place.</summary>
public readonly record struct TripTransitionRevert(
    string Status,
    DateTimeOffset? ActualStartAt,
    DateTimeOffset? ActualEndAt,
    string? CancellationReason);
