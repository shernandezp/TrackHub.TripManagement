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

namespace TrackHub.TripManagement.Domain.Models;

/// <summary>
/// Dispatch-board projection of a trip. Carries no stop or route detail — except the derived
/// <see cref="Phase"/>, which is the one thing the board leads with (spec 11a §10).
/// <para>
/// <see cref="ActualStartAt"/> and <see cref="ActualEndAt"/> are MEASURED by default: the instant
/// the vehicle reached its origin zone and the instant its route closed. A manual override still
/// writes them, and the timeline's <c>Source</c> is the permanent record of which happened (§5.3).
/// </para>
/// </summary>
public readonly record struct TripVm(
    Guid TripId,
    Guid AccountId,
    string Code,
    string Status,
    Guid TransporterId,
    Guid? DriverId,
    Guid? RoutePlanId,
    Guid? ServiceOrderId,
    string? ExternalReference,
    string? CustomerName,
    string OriginName,
    double OriginLatitude,
    double OriginLongitude,
    Guid? OriginGeofenceId,
    int OriginRadiusMeters,
    DateTimeOffset PlannedStartAt,
    DateTimeOffset? PlannedEndAt,
    DateTimeOffset? ActualStartAt,
    DateTimeOffset? ActualEndAt,
    DateTimeOffset? ArmedAt,
    DateTimeOffset? OriginArrivedAt,
    DateTimeOffset? OriginDepartedAt,
    string Phase,
    string? PhaseStopName,
    string? PhaseStopActivity,
    DateTimeOffset? PhaseEtaAt,

    // True when the stop the phase names has already raised TripDelayed. One definition of
    // "delayed" for the alert, the board badge and the exception filter alike.
    bool PhaseDelayed,
    string? Notes,
    DateTimeOffset? LastPositionAt,
    double? LastLatitude,
    double? LastLongitude,
    double ActualDistanceMeters,
    string? TollVehicleClass,
    DateTimeOffset? DeviationOpenedAt,
    string? CancellationReason,
    int StopCount,

    // How much of the route is still ahead. The board's "stalled at final stop" filter needs to tell
    // a truck unloading at stop 2 of 5 from one parked at the last stop with nowhere left to go, and
    // the phase alone cannot: both read AtStop. Zero here plus AtStop IS the stalled condition.
    int PendingStopCount,
    DateTimeOffset LastModified);

public readonly record struct TripsPageVm(IReadOnlyCollection<TripVm> Items, int TotalCount);

/// <summary>
/// Everything the trip detail screen needs in one round trip: the trip, its ordered stops with
/// their deliveries, the active assignment, the route plan (including the toll breakdown), POD
/// summaries and the current share state.
/// </summary>
public readonly record struct TripDetailVm(
    TripVm Trip,
    IReadOnlyCollection<TripStopVm> Stops,
    TripAssignmentVm? Assignment,
    RoutePlanVm? RoutePlan,
    IReadOnlyCollection<ProofOfDeliveryVm> ProofsOfDelivery,
    IReadOnlyCollection<TripShareVm> Shares);

public readonly record struct TripStopVm(
    Guid TripStopId,
    Guid AccountId,
    Guid TripId,
    int Sequence,
    string Name,
    string? Address,
    // The coarse locality, distinct from the full street Address. Exposed so the stop edit dialog
    // can round-trip it — without this an edit saved the locality back empty, and the public
    // snapshot (which may expose City but never Address) silently lost it.
    string? City,
    double Latitude,
    double Longitude,
    Guid? GeofenceId,
    int ArrivalRadiusMeters,
    string Activity,
    DateTimeOffset? PlannedArrivalFrom,
    DateTimeOffset? PlannedArrivalTo,
    string Status,
    DateTimeOffset? ActualArrivalAt,
    DateTimeOffset? ActualDepartureAt,
    DateTimeOffset? EtaAt,
    string EtaSource,
    DateTimeOffset? DelayAlertedAt,
    bool RequiresPod,
    short Priority,
    string? Observations,
    IReadOnlyCollection<DeliveryVm> Deliveries);

public readonly record struct DeliveryVm(
    Guid DeliveryId,
    Guid AccountId,
    Guid TripStopId,
    string? Reference,
    string ClientName,
    string? BranchName,
    string? ProductsSummary,
    string Status,
    string? Observations,
    int SequenceIndex);

public readonly record struct TripAssignmentVm(
    Guid TripAssignmentId,
    Guid AccountId,
    Guid TripId,
    Guid DriverId,
    Guid TransporterId,
    string Status,
    DateTimeOffset AssignedAt,
    DateTimeOffset? AcknowledgedAt,
    DateTimeOffset? EndedAt);

public readonly record struct RoutePlanVm(
    Guid RoutePlanId,
    Guid AccountId,
    Guid TripId,
    string Provider,
    GeometryLineVm? Geometry,
    GeometryLineVm? Corridor,
    int CorridorMeters,
    double PlannedDistanceMeters,
    int PlannedDurationSeconds,
    string? WaypointsJson,
    string? LegsJson,
    DateTimeOffset ComputedAt,
    string Status,
    string? ErrorCode,
    string? ErrorMessage,
    string? TollVehicleClass,
    decimal? EstimatedTollAmount,
    string? TollCurrency,
    string TollStatus,
    IReadOnlyCollection<TollStationMatchVm> TollStations);

public readonly record struct TripEventVm(
    Guid TripEventId,
    Guid AccountId,
    Guid TripId,
    Guid? TripStopId,
    string EventType,
    DateTimeOffset OccurredAt,
    string Source,
    string? PayloadJson);

public readonly record struct TripTimelinePageVm(IReadOnlyCollection<TripEventVm> Items, int TotalCount);

public readonly record struct ProofOfDeliveryVm(
    Guid ProofOfDeliveryId,
    Guid AccountId,
    Guid TripStopId,
    Guid? DeliveryId,
    string ReceiverName,
    string? ReceiverDocument,
    DateTimeOffset CapturedAt,
    double? Latitude,
    double? Longitude,
    string? Notes,
    IReadOnlyCollection<TripDocumentVm> Documents);

public readonly record struct TripDocumentVm(
    Guid TripDocumentId,
    Guid AccountId,
    Guid TripId,
    Guid? TripStopId,
    Guid? ProofOfDeliveryId,
    Guid DocumentId,
    string Kind);

/// <summary>
/// A share's configuration and lifecycle. <c>Token</c> is populated exactly once, by the create
/// path — it is never re-readable afterwards (spec 11 acceptance 23).
/// </summary>
public readonly record struct TripShareVm(
    Guid TripShareId,
    Guid AccountId,
    Guid TripId,
    Guid PublicLinkGrantId,
    bool IncludeDriverName,
    bool IncludeVehicle,
    bool IncludeLivePosition,
    bool IncludeStopDetail,
    bool IncludePodSummary,
    // Mirrors the input flag so the existing-links list can show whether a link discloses the
    // planned route. A disclosure flag the operator cannot read back is a disclosure they cannot audit.
    bool IncludeRoute,
    string CreatedByPrincipalId,
    DateTimeOffset ExpiresAt,
    DateTimeOffset? RevokedAt,
    string? Token);

/// <summary>Result of a position-feed batch, returned to Router for logging only.</summary>
public readonly record struct TripProcessingResultVm(
    int ProcessedCount,
    int StopsArrived,
    int StopsDeparted,
    int DeviationsRaised);

public readonly record struct UserVm(Guid UserId, Guid AccountId, string Username);

/// <summary>An id and the name a human would type for it — what bulk planning resolves against.</summary>
public readonly record struct NamedEntityVm(Guid Id, string Name);

/// <summary>
/// Outcome of a bulk trip upload. Rows that failed are reported individually and the rest still
/// landed, so the operator fixes those lines and re-imports rather than guessing (spec 11a §9.1).
/// </summary>
public readonly record struct TripCsvImportResultVm(
    int RowsRead,
    int TripsCreated,
    IReadOnlyCollection<TripCsvImportErrorVm> Errors);

public readonly record struct TripCsvImportErrorVm(int RowNumber, string ErrorCode, string Message);
