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
/// The detection working set as a unit of work: loaded ONCE per position batch, mutated in memory,
/// committed ONCE per fix.
/// <para>
/// It replaces six fine-grained writer calls that each re-read the trip row the request was already
/// holding and then saved on their own. That shape cost five round trips and five transactions per
/// fix and — worse — made a single fix <b>non-atomic</b>: the odometer committed, then the origin
/// debounce clock committed separately, so a process that died between them left measured state
/// contradicting itself with no way to tell which half had landed.
/// </para>
/// <para>
/// <b>The commit boundary is the idempotency boundary.</b> Everything here is trip STATE — clocks,
/// counters, the odometer, the origin measurements — none of which writes a <c>TripEvent</c> and
/// none of which can therefore lose a race on the unique idempotency index. Event-producing
/// operations (a transition, a stop arrival) deliberately stay OUTSIDE this unit and keep committing
/// individually: batching them in would let one duplicate event roll back an odometer update that
/// was perfectly valid.
/// </para>
/// <para>
/// The mutators are synchronous because the rows are already in hand, and each reports whether it
/// changed anything so an idle fix — a truck parked inside its origin zone moves nothing — costs no
/// save at all. Only <see cref="ArmAsync"/> is asynchronous: it reads the linked geofence once to
/// build the snapshot. It still does not save.
/// </para>
/// </summary>
public interface ITripDetectionUnitOfWork
{
    /// <summary>
    /// The account's <c>InProgress</c> trips for these transporters, plus at most one armable
    /// <c>Created</c> trip per otherwise-idle transporter (spec 11a §6.1, §7). The rows stay loaded
    /// and tracked for the life of the batch, so every mutation below lands on the instance this
    /// returned rather than on a re-read copy.
    /// </summary>
    Task<IReadOnlyCollection<OpenTripVm>> LoadAsync(
        Guid accountId,
        IReadOnlyCollection<Guid> transporterIds,
        DateTimeOffset? armableUntil,
        CancellationToken cancellationToken);

    /// <summary>
    /// Freezes the origin zone and every stop's arrival geometry and marks the trip watched.
    /// Idempotent and event-free, so an armed-but-never-run trip stays deletable (acceptance 16).
    /// </summary>
    Task<bool> ArmAsync(Guid tripId, CancellationToken cancellationToken);

    /// <summary>
    /// Whether the fix falls inside the trip's snapshotted origin zone.
    /// <para>
    /// Answered IN MEMORY, against the polygon the load already materialized. It used to be a PostGIS
    /// round trip per fix per trip, which bought nothing — the query filtered to a single row by
    /// primary key, so no index was doing any work — and it broke outright once arming was buffered:
    /// the geometry a trip is judged against is written by the same fix that tests it, and the
    /// database had not seen it yet. Both problems are the same mistake, asking the database about a
    /// value already in hand.
    /// </para>
    /// <para>
    /// Corridor containment deliberately stays in the database: those polygons buffer an entire route
    /// and are far too large to load per batch. The rule is geometry we hold, we test here.
    /// </para>
    /// </summary>
    bool IsInsideOrigin(Guid tripId, double latitude, double longitude);

    /// <summary>
    /// The stops whose snapshotted arrival geometry contains the fix, in sequence order. In memory,
    /// for the same reason as <see cref="IsInsideOrigin"/> — the stops and their rings were loaded
    /// with the working set.
    /// </summary>
    IReadOnlyCollection<Guid> StopsContainingPoint(Guid tripId, double latitude, double longitude);

    /// <summary>First write wins: a replayed visit never overwrites a live measurement (acceptance 12).</summary>
    void RecordOriginVisit(Guid tripId, DateTimeOffset? arrivedAt, DateTimeOffset? departedAt);

    /// <summary>The persisted origin exit-debounce clock; null restarts it.</summary>
    void SetOriginOutsideSince(Guid tripId, DateTimeOffset? outsideSinceAt);

    /// <summary>Stamps the debounced origin exit and clears the clock. False when already stamped.</summary>
    bool TryRecordOriginDeparture(Guid tripId, DateTimeOffset departedAt);

    /// <summary>
    /// Odometer and last-seen point. Returns false for an out-of-order or replayed fix, which the
    /// caller must treat as "skip this fix entirely" — the deviation run length is a plain counter,
    /// so a redelivered out-of-corridor fix would otherwise climb to the threshold on its own.
    /// </summary>
    bool TryAdvanceProgress(Guid tripId, double latitude, double longitude, DateTimeOffset positionAt, double addedDistanceMeters);

    /// <summary>
    /// Deviation episode state. A plain assignment, never first-write-wins: an episode must be able
    /// to CLOSE on re-entry so a later departure opens a new one (acceptance 14).
    /// </summary>
    void SetDeviationState(Guid tripId, DateTimeOffset? deviationOpenedAt, int consecutiveOutsideFixes);

    /// <summary>
    /// Commits everything buffered so far, and is a no-op when nothing moved. Called once per fix,
    /// and again before any operation that writes an event, so the measurement a transition reads
    /// (<c>OriginArrivedAt</c>, say) is durable before the event that depends on it.
    /// </summary>
    Task FlushAsync(CancellationToken cancellationToken);

    /// <summary>
    /// Throws away everything buffered for ONE trip and drops it from the working set, without
    /// saving.
    /// <para>
    /// Called when that trip's fix fails part-way through. Committing what it managed to buffer would
    /// publish exactly the half-applied state this unit exists to prevent — and leaving it tracked is
    /// worse still, because the context is request-scoped and the NEXT trip's flush would carry the
    /// broken fix in with it.
    /// </para>
    /// <para>
    /// <b>One trip, not the batch.</b> Dropping the whole working set here would mean a single bad
    /// trip silently switched detection off for every other vehicle in the same batch — the failure
    /// would be invisible, because the remaining trips would just quietly measure nothing.
    /// </para>
    /// </summary>
    void Discard(Guid tripId);
}
