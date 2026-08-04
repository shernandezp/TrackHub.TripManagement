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

namespace TrackHub.TripManagement.Domain.Interfaces;

/// <summary>Write side of the trip aggregate: CRUD plus the lifecycle transitions.</summary>
public interface ITripWriter
{
    Task<TripVm> CreateTripAsync(TripDto trip, Guid accountId, CancellationToken cancellationToken);

    Task UpdateTripAsync(Guid tripId, TripDto trip, Guid accountId, CancellationToken cancellationToken);

    /// <summary>
    /// Permitted only for a <c>Created</c> trip with no <c>TripEvent</c> rows; anything else is a
    /// conflict and the caller is told to cancel instead (spec 11 §5, acceptance 16).
    /// </summary>
    Task DeleteTripAsync(Guid tripId, Guid accountId, CancellationToken cancellationToken);

    /// <summary>
    /// Applies a lifecycle transition after validating it against the matrix, stamping
    /// <c>ActualStartAt</c>/<c>ActualEndAt</c> and appending the timeline event — <b>all in one
    /// save</b>.
    /// <para>
    /// Returns <c>false</c> when the transition was ALREADY recorded under
    /// <paramref name="idempotencyKey"/>: nothing is written and the caller must skip its side
    /// effects (alert emission). Auto-start and manual Start share a key on purpose, so the two can
    /// race and exactly one wins — which only works because the status change and the event insert
    /// commit together (spec 11a §12.1).
    /// </para>
    /// <para>
    /// <paramref name="measuredAt"/> is the instant the transition actually happened as the FIELD
    /// saw it — a position's <c>DeviceDateTime</c> on the detection path. Manual overrides pass
    /// null and get the command time. It is what <c>ActualStartAt</c>/<c>ActualEndAt</c> are
    /// stamped with, so a measured lifecycle never carries a server clock (§12.3).
    /// </para>
    /// </summary>
    Task<bool> TransitionTripAsync(
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
        CancellationToken cancellationToken);

    /// <summary>
    /// ARMS a trip: snapshots <c>OriginGeom</c> and every stop's <c>ArrivalGeom</c> and stamps
    /// <c>ArmedAt</c>. Idempotent, and it deliberately writes NO <c>TripEvent</c> — an armed trip
    /// that never ran must stay deletable (acceptance 16, spec 11a §6.2).
    /// <para>
    /// Snapshotting HERE rather than at Start is what makes auto-start possible at all: origin
    /// arrival is the trigger, so the geometry it is judged against has to exist first. The immunity
    /// property is unchanged — geometry is frozen before execution and never re-read mid-trip.
    /// </para>
    /// Returns <c>false</c> when the trip was already armed.
    /// </summary>
    Task<bool> ArmTripAsync(Guid tripId, Guid accountId, CancellationToken cancellationToken);

    /// <summary>
    /// Replays a recorded origin visit onto a late-created trip (spec 11a §5.4). These are
    /// MEASUREMENTS, not declarations, so they are written before the trip transitions and the
    /// transition then stamps <c>ActualStartAt</c> from them.
    /// </summary>
    Task SetOriginVisitAsync(
        Guid tripId,
        Guid accountId,
        DateTimeOffset? arrivedAt,
        DateTimeOffset? departedAt,
        CancellationToken cancellationToken);

    Task<TripAssignmentVm> AssignTripAsync(Guid tripId, Guid accountId, Guid driverId, Guid? transporterId, CancellationToken cancellationToken);

}
