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

/// <summary>
/// Stop mutation. Sequences are re-normalized server-side on every structural change so the
/// unique <c>(TripId, Sequence)</c> index can never be violated by a client's ordering.
/// </summary>
public interface ITripStopWriter
{
    Task<TripStopVm> AddStopAsync(Guid tripId, Guid accountId, TripStopDto stop, CancellationToken cancellationToken);

    Task UpdateStopAsync(Guid tripStopId, Guid accountId, TripStopDto stop, CancellationToken cancellationToken);

    /// <summary>Rejected for a stop already <c>Arrived</c> or <c>Departed</c>.</summary>
    Task RemoveStopAsync(Guid tripStopId, Guid accountId, CancellationToken cancellationToken);

    /// <summary>
    /// Replaces a trip's whole route in one go — the re-plan a partner's weekly re-upload performs
    /// (spec 11a §9.2).
    /// <para>
    /// Permitted only while the trip is still <c>Created</c>. A running trip's stops carry
    /// measurements and deliveries, so replacing them would erase recorded history; the caller must
    /// reject that case before reaching here, and this method enforces it too.
    /// </para>
    /// </summary>
    Task ReplaceStopsAsync(Guid tripId, Guid accountId, IReadOnlyCollection<TripStopDto> stops, CancellationToken cancellationToken);

    /// <summary>
    /// Re-sequences a trip's stops. A completed stop may not be pushed below an uncompleted one,
    /// so a reorder can never rewrite history.
    /// </summary>
    Task ReorderStopsAsync(Guid tripId, Guid accountId, IReadOnlyCollection<Guid> orderedStopIds, CancellationToken cancellationToken);

    /// <summary>
    /// Records arrival/departure/skip. Idempotent on the caller's
    /// <c>TripEvent.IdempotencyKey</c>: a duplicate submission returns the same result and writes
    /// no second row. Timestamps once written are never overwritten by a later detection or
    /// manual override (acceptance 12, 15).
    /// <para>
    /// <paramref name="tripId"/> is REQUIRED and verified against the stop: resolving a stop by id
    /// and account alone let a caller pass trip X's active-status check and then write against a
    /// stop belonging to trip Y. A stop that does not belong to the named trip is a
    /// <c>NotFoundException</c>.
    /// </para>
    /// <para>
    /// The order <c>Pending → Arrived → Departed</c> (or <c>Pending → Skipped</c>) is enforced here
    /// (acceptance 12): departing a stop that never arrived is
    /// <see cref="TripErrorCodes.StopNotArrived"/>, not a silently stamped departure timestamp.
    /// Re-submitting the status a stop already holds stays permitted — that is the idempotent
    /// retry path, resolved by the unique idempotency index rather than by a conflict.
    /// </para>
    /// </summary>
    Task<bool> RecordStopProgressAsync(
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
        CancellationToken cancellationToken);

    /// <summary>
    /// Persists the departure-debounce clock: the instant an <c>Arrived</c> stop was first seen
    /// outside its <c>ArrivalGeom</c>, or <c>null</c> to clear it on re-entry.
    /// <para>
    /// This is state, not a timestamp of record. It has to be persisted because Router pushes one
    /// position per transporter per call — a debounce measured against a per-request field can
    /// never elapse, which is exactly why departure detection never fired (spec 11 section 7.4).
    /// </para>
    /// </summary>
    Task SetStopOutsideSinceAsync(Guid tripStopId, Guid accountId, DateTimeOffset? outsideSinceAt, CancellationToken cancellationToken);

    /// <summary>Writes the ETA a refresh cycle computed, along with where it came from.</summary>
    Task UpdateStopEtaAsync(Guid tripStopId, Guid accountId, DateTimeOffset? etaAt, string etaSource, CancellationToken cancellationToken);

    /// <summary>Stamps the one-shot delay-alert marker after the alert was successfully emitted.</summary>
    Task MarkStopDelayAlertedAsync(Guid tripStopId, Guid accountId, DateTimeOffset alertedAt, CancellationToken cancellationToken);
}
