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
/// The detection working set. Unlike Geofencing — which must consider an account's whole zone
/// catalog — trip detection looks at a small, bounded set: the account's <c>InProgress</c> trips
/// plus at most one armable <c>Created</c> trip per transporter (spec 11a §6.1). That is what makes
/// arrival belong here rather than to the general-purpose zone engine (spec 11 §18.4).
/// </summary>
public interface ITripDetectionReader
{
    /// <summary>
    /// <c>InProgress</c> trips whose route has no <c>Pending</c> stop left — the only trips
    /// auto-completion could possibly act on. A cheap pre-filter for the fallback sweep, so the
    /// hosted loop does not re-examine every running trip every cycle.
    /// </summary>
    Task<IReadOnlyCollection<Guid>> GetCompletionCandidatesAsync(Guid accountId, CancellationToken cancellationToken);

    /// <summary>
    /// The trip's stops in sequence order with just what completion needs, plus the identity fields
    /// the <c>TripCompleted</c> alert carries.
    /// </summary>
    Task<TripCompletionCandidateVm?> GetCompletionStateAsync(Guid accountId, Guid tripId, CancellationToken cancellationToken);

    /// <summary>
    /// True inside the plan's corridor polygon, false outside — and <b>null when the question
    /// cannot be answered</b>, because the plan row is gone or carries no <c>CorridorGeom</c>.
    /// <para>
    /// The three-way result is deliberate. Collapsing "unevaluable" into false made a missing
    /// corridor indistinguishable from a vehicle off route, so three fixes on a plan with no
    /// corridor raised a <c>TripRouteDeviation</c> that re-entry could never clear (spec 11 §7.4,
    /// acceptance 14). Callers must skip deviation detection on null rather than counting it.
    /// </para>
    /// </summary>
    Task<bool?> IsInsideCorridorAsync(Guid accountId, Guid routePlanId, double latitude, double longitude, CancellationToken cancellationToken);

    /// <summary>
    /// In-progress trips due an ETA refresh, with the next pending stop resolved.
    /// <para>
    /// <b>Trips with a stale or absent position are returned too</b>, flagged by
    /// <see cref="EtaCandidateVm.HasFreshPosition"/>. Filtering them out here was a real defect:
    /// the fallback to <c>Planned</c>/<c>Unavailable</c> lives in the ETA service, so a tracker
    /// going dark left the stop's last ORS-derived <c>EtaAt</c> in place with
    /// <c>EtaSource = Ors</c> — the UI then presented a hours-old guess as a live estimate, which
    /// is exactly the dishonesty the source field exists to prevent (spec 11 §10, §18.11).
    /// Staleness is derived from <paramref name="positionFreshnessCutoff"/>; no column stores it.
    /// </para>
    /// </summary>
    Task<IReadOnlyCollection<EtaCandidateVm>> GetEtaCandidatesAsync(Guid accountId, DateTimeOffset positionFreshnessCutoff, CancellationToken cancellationToken);

    /// <summary>
    /// <c>Created</c> trips whose planned start falls inside the account's lead window
    /// <c>[dueAfter, dueBefore]</c>. The lower bound is not optional: without it the first run
    /// after a deployment reminds about every historical trip that was never started (spec 11 §10).
    /// </summary>
    Task<IReadOnlyCollection<TripVm>> GetTripsDueToStartAsync(Guid accountId, DateTimeOffset dueAfter, DateTimeOffset dueBefore, CancellationToken cancellationToken);
}

/// <summary>
/// A watched trip plus its stops. "Watched" now spans two statuses — a <c>Created</c> trip that is
/// armable and an <c>InProgress</c> one — so <see cref="Status"/> is carried and every automatic
/// step is gated on it.
/// </summary>
/// <param name="HasOriginGeom">
/// Whether the origin snapshot exists. Without it there is nothing to auto-start from — which is
/// exactly how trips created before zero-touch stay inert (§14).
/// </param>
/// <param name="OriginOutsideSinceAt">The persisted origin exit-debounce clock; null while inside.</param>
public readonly record struct OpenTripVm(
    Guid TripId,
    Guid AccountId,
    string Code,
    string Status,
    Guid TransporterId,
    Guid? DriverId,
    Guid? RoutePlanId,
    bool HasReadyRoutePlan,
    DateTimeOffset PlannedStartAt,
    DateTimeOffset? ArmedAt,
    bool HasOriginGeom,
    DateTimeOffset? OriginArrivedAt,
    DateTimeOffset? OriginDepartedAt,
    DateTimeOffset? OriginOutsideSinceAt,
    DateTimeOffset? DeviationOpenedAt,
    int ConsecutiveOutsideFixes,
    double ActualDistanceMeters,
    double? LastLatitude,
    double? LastLongitude,
    DateTimeOffset? LastPositionAt,
    IReadOnlyCollection<OpenTripStopVm> Stops);

public readonly record struct OpenTripStopVm(
    Guid TripStopId,
    int Sequence,
    string Name,
    string Status,
    DateTimeOffset? ActualArrivalAt,
    DateTimeOffset? PlannedArrivalTo,
    DateTimeOffset? DelayAlertedAt,

    // The persisted departure-debounce clock; null while the vehicle is inside the geometry.
    DateTimeOffset? OutsideSinceAt,
    double Latitude,
    double Longitude);

/// <summary>
/// What auto-completion needs to decide whether a trip is finished: the stops in sequence order,
/// and the identity fields the <c>TripCompleted</c> alert carries.
/// </summary>
public readonly record struct TripCompletionCandidateVm(
    Guid TripId,
    string Code,
    string Status,
    Guid TransporterId,
    Guid? DriverId,
    IReadOnlyCollection<CompletionStopVm> Stops);

public readonly record struct CompletionStopVm(
    Guid TripStopId,
    int Sequence,
    string Status,
    DateTimeOffset? ActualArrivalAt,
    DateTimeOffset? ActualDepartureAt,

    // The persisted exit-debounce clock. Dwell-based completion means "the vehicle parked here and
    // is never going to depart", and that claim is false while this is set — we are holding
    // positive evidence it is already outside the zone. Without it the completion rule could not
    // see the departure it was racing.
    DateTimeOffset? OutsideSinceAt);

/// <summary>
/// A trip whose next pending stop needs an ETA recomputed.
/// <para>
/// The position fields are nullable and paired with <see cref="HasFreshPosition"/> so the service
/// can tell "here is where the vehicle is" from "here is where it was two hours ago" instead of
/// treating both as live evidence. <see cref="CurrentEtaAt"/>/<see cref="CurrentEtaSource"/> carry
/// what is already persisted, so a cycle that would write the identical value can skip the write
/// and stay a no-op — which is what keeps the job an on-work-only recorder (SVD-11).
/// </para>
/// </summary>
public readonly record struct EtaCandidateVm(
    Guid TripId,
    Guid AccountId,
    string Code,
    Guid TransporterId,
    Guid? DriverId,
    double? LastLatitude,
    double? LastLongitude,
    DateTimeOffset? LastPositionAt,
    bool HasFreshPosition,
    Guid NextStopId,
    string NextStopName,
    double NextStopLatitude,
    double NextStopLongitude,
    DateTimeOffset? PlannedArrivalTo,
    DateTimeOffset? DelayAlertedAt,
    DateTimeOffset? CurrentEtaAt,
    string CurrentEtaSource);
