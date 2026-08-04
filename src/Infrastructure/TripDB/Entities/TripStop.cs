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
/// An ordered stop on a trip. <see cref="ArrivalGeom"/> is a SNAPSHOT taken when the trip starts
/// (spec 11 section 18.4): either the linked geofence polygon or the stop point buffered by
/// <see cref="ArrivalRadiusMeters"/>. Snapshotting is what makes a running trip immune to a
/// geofence being edited mid-execution. It is null until the trip transitions to InProgress.
/// </summary>
public sealed class TripStop : BaseAuditableEntity
{
    public Guid TripStopId { get; set; } = Guid.NewGuid();
    public Guid AccountId { get; set; }
    public Guid TripId { get; set; }
    public int Sequence { get; set; }
    public string Name { get; set; } = string.Empty;
    /// <summary>
    /// The full reverse-geocoded street label. INTERNAL: never projected into a public snapshot —
    /// see <see cref="City"/>.
    /// </summary>
    public string? Address { get; set; }

    /// <summary>
    /// Coarse locality, and the ONLY location label §7.8 permits in the anonymous tracking
    /// snapshot. Held separately from <see cref="Address"/> on purpose: a link holder on a
    /// multi-drop trip may learn that a stop is in Chia, never that it is at Cra 7 #71-52.
    /// Sourced from the same Router reverse-geocode response the portal already calls when a stop
    /// is placed (<c>AddressVm.City</c>).
    /// </summary>
    public string? City { get; set; }
    public Point Point { get; set; } = default!;

    /// <summary>Source only - the live geofence is never consulted once the trip is running.</summary>
    public Guid? GeofenceId { get; set; }
    public Polygon? ArrivalGeom { get; set; }
    public int ArrivalRadiusMeters { get; set; } = TripGeometryDefaults.ArrivalRadiusMeters;
    public DateTimeOffset? PlannedArrivalFrom { get; set; }
    public DateTimeOffset? PlannedArrivalTo { get; set; }
    /// <summary>
    /// What the vehicle does here — <c>Load</c>, <c>Unload</c> or <c>Other</c>. This is what gives
    /// dwell a meaning: the dwell of a <c>Load</c> stop IS loading time, of an <c>Unload</c> stop
    /// unloading time. A round trip's return-to-depot stop defaults to <c>Other</c>, because parking
    /// at home is neither.
    /// </summary>
    public string Activity { get; set; } = TripStopActivities.Unload;
    public string Status { get; set; } = TripStopStatuses.Pending;
    public DateTimeOffset? ActualArrivalAt { get; set; }
    public DateTimeOffset? ActualDepartureAt { get; set; }
    public DateTimeOffset? EtaAt { get; set; }
    public string EtaSource { get; set; } = EtaSources.Unavailable;
    public DateTimeOffset? DelayAlertedAt { get; set; }

    /// <summary>
    /// When this <c>Arrived</c> stop was first seen OUTSIDE <see cref="ArrivalGeom"/>. The 30 s
    /// departure debounce is measured from here and the value is PERSISTED, because Router pushes
    /// one position per transporter per call — a per-request clock is reset before the window can
    /// ever elapse, which is why departure never fired (spec 11 section 7.4). Cleared by a fix back
    /// inside the geometry and by arrival.
    /// </summary>
    public DateTimeOffset? OutsideSinceAt { get; set; }
    public bool RequiresPod { get; set; }
    public short Priority { get; set; }
    public string? Observations { get; set; }

    public Trip? Trip { get; set; }

    // ---------------------------------------------------------------- behaviour

    /// <summary>The persisted exit-debounce clock; null restarts it from zero.</summary>
    public bool SetOutsideSince(DateTimeOffset? outsideSinceAt)
    {
        if (OutsideSinceAt == outsideSinceAt)
        {
            return false;
        }

        OutsideSinceAt = outsideSinceAt;
        return true;
    }

    /// <summary>
    /// Applies a stop progression and hands back everything needed to undo it.
    /// <para>
    /// <b>Timestamps once written are never overwritten</b> by a later detection or manual override
    /// (acceptance 12) — the status still advances, but only the first recorded instant survives.
    /// </para>
    /// <para>
    /// The revert token is the same reason the trip has one: the row may be carrying a pending
    /// arrival-geometry snapshot from arming in this very batch, and detaching it to undo a
    /// duplicate progress event — which the writer used to do — discarded that silently.
    /// </para>
    /// </summary>
    public TripStopProgressRevert ApplyProgress(string toStatus, DateTimeOffset occurredAt, string? reason)
    {
        var revert = new TripStopProgressRevert(Status, ActualArrivalAt, ActualDepartureAt, OutsideSinceAt, Observations);

        switch (toStatus)
        {
            case TripStopStatuses.Arrived:
                ActualArrivalAt ??= occurredAt;

                // Arriving restarts the departure debounce from scratch: a clock left over from an
                // earlier excursion would otherwise already be 30 s "old" when the vehicle rolls out.
                OutsideSinceAt = null;
                break;
            case TripStopStatuses.Departed:
                ActualDepartureAt ??= occurredAt;
                OutsideSinceAt = null;
                break;
            case TripStopStatuses.Skipped:
                Observations = reason ?? Observations;
                OutsideSinceAt = null;
                break;
            default:
                return revert;
        }

        if (!TripStopStatuses.IsClosed(Status))
        {
            Status = toStatus;
        }

        return revert;
    }

    /// <summary>Undoes exactly the fields <see cref="ApplyProgress"/> moved.</summary>
    public void RevertProgress(TripStopProgressRevert revert)
    {
        Status = revert.Status;
        ActualArrivalAt = revert.ActualArrivalAt;
        ActualDepartureAt = revert.ActualDepartureAt;
        OutsideSinceAt = revert.OutsideSinceAt;
        Observations = revert.Observations;
    }
}

/// <summary>The stop fields a progression moves, captured so it can be undone in place.</summary>
public readonly record struct TripStopProgressRevert(
    string Status,
    DateTimeOffset? ActualArrivalAt,
    DateTimeOffset? ActualDepartureAt,
    DateTimeOffset? OutsideSinceAt,
    string? Observations);
