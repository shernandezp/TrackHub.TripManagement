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

namespace TrackHub.TripManagement.Domain.Records;

/// <summary>
/// Trip write contract. <c>AccountId</c> is deliberately absent — it is resolved from the caller's
/// identity, never accepted from the wire (spec 11 acceptance 1).
/// <para>
/// The "already in transit" declaration (spec 11a §5.4) deliberately does NOT live here. Backfill
/// has to read the trip's ROUTE to replay stop visits, and the route does not exist yet at create
/// time — it is a separate finalizing step (<c>DeclareTripInTransitCommand</c>) that every planning
/// input calls once its stops have landed.
/// </para>
/// </summary>
/// <param name="OriginGeofenceId">
/// The account geofence the origin was picked from, when it was — its real shape then becomes the
/// origin zone instead of a radius buffer.
/// </param>
/// <param name="OriginRadiusMeters">Buffer radius for a POI/point origin (50–5000 m).</param>
public readonly record struct TripDto(
    string Code,
    Guid TransporterId,
    Guid? DriverId,
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
    string? Notes,
    string? TollVehicleClass);

/// <summary>
/// Stop write contract.
/// <para>
/// <paramref name="Address"/> and <paramref name="City"/> are deliberately two fields with two
/// disclosure levels. <c>Address</c> is the full reverse-geocoded street label ("Cra 7 #71-52,
/// Bogota") and is internal/dispatcher-only. <c>City</c> is the coarse locality and is the ONLY one
/// §7.8 allows into a public snapshot — conflating them is what leaked every other stop's exact
/// delivery address to any link holder. The portal fills both from the same Router reverse-geocode
/// response (<c>AddressVm</c> already carries a distinct city alongside the full address).
/// </para>
/// </summary>
/// <param name="Activity">
/// <c>Load</c>, <c>Unload</c> or <c>Other</c>. Anything unrecognised (including empty) normalizes to
/// <c>Unload</c>, so an older client that does not send the field keeps working.
/// </param>
public readonly record struct TripStopDto(
    string Name,
    string? Address,
    string? City,
    double Latitude,
    double Longitude,
    Guid? GeofenceId,
    int ArrivalRadiusMeters,
    string? Activity,
    DateTimeOffset? PlannedArrivalFrom,
    DateTimeOffset? PlannedArrivalTo,
    bool RequiresPod,
    short Priority,
    string? Observations);

public readonly record struct DeliveryDto(
    string? Reference,
    string ClientName,
    string? BranchName,
    string? ProductsSummary,
    string? Observations,
    int SequenceIndex);

/// <summary>
/// POD capture. <paramref name="ClientEventId"/> makes the write idempotent by design rather than
/// by caller discipline — this is what lets spec 10 layer an offline outbox on top without
/// reopening the handler (spec 11 §7.3, §9).
/// </summary>
public readonly record struct ProofOfDeliveryDto(
    Guid TripStopId,
    Guid? DeliveryId,
    string ReceiverName,
    string? ReceiverDocument,
    string? Notes,
    DateTimeOffset CapturedAt,
    double? Latitude,
    double? Longitude,
    IReadOnlyCollection<Guid> DocumentIds,
    Guid ClientEventId);

/// <summary>
/// Field-level disclosure configuration for a public share.
/// <para>
/// Every member is a DISCLOSURE flag and therefore fails closed: an omitted or default-constructed
/// value must expose less, never more. <paramref name="IncludeRoute"/> exists because §7.8 lists
/// the planned route geometry as flag-gated ("per field flags") while §6.1's field list forgot it —
/// §7.8 is the disclosure contract, so a share created with every flag off must not hand out the
/// full planned route.
/// </para>
/// </summary>
public readonly record struct TripShareFieldFlagsDto(
    bool IncludeDriverName,
    bool IncludeVehicle,
    bool IncludeLivePosition,
    bool IncludeStopDetail,
    bool IncludePodSummary,
    bool IncludeRoute);

/// <summary>
/// Position pushed by Router/SyncWorker. Mirrors the Geofencing <c>TransporterPositionDto</c>
/// shape so the two feeds stay symmetric.
/// </summary>
public readonly record struct TransporterPositionDto(
    Guid TransporterId,
    double Latitude,
    double Longitude,
    DateTimeOffset DeviceDateTime);

/// <summary>
/// Partner/TMS import row. Idempotent on <paramref name="ExternalReference"/>, which is unique
/// per account — a re-sent batch updates rather than duplicating (spec 11 §7.9).
/// <para>
/// Re-sending a still-<c>Created</c> trip REPLACES its stops (spec 11a §9.2). The stops payload used
/// to be silently dropped on the update path, so a partner's weekly re-upload could revise a trip's
/// header and never its route — the one thing a re-plan usually changes.
/// </para>
/// </summary>
public readonly record struct TripImportDto(
    string ExternalReference,
    string Code,
    Guid TransporterId,
    Guid? DriverId,
    string? CustomerName,
    string OriginName,
    double OriginLatitude,
    double OriginLongitude,
    Guid? OriginGeofenceId,
    DateTimeOffset PlannedStartAt,
    DateTimeOffset? PlannedEndAt,
    DateTimeOffset? StartedAt,
    string? Notes,
    IReadOnlyCollection<TripStopDto> Stops);

/// <summary>Per-item import outcome. The batch never fails as a unit.</summary>
public readonly record struct TripImportResultVm(
    string ExternalReference,
    bool Succeeded,
    Guid? TripId,
    string? ErrorCode,
    string? ErrorMessage);

/// <summary>Payload carried to Manager's <c>recordAlertEvent</c> for every trip alert.</summary>
public readonly record struct TripAlertDto(
    Guid AccountId,
    Guid TripId,
    Guid? TripStopId,
    string TripCode,
    Guid TransporterId,
    Guid? DriverId,
    string? StopName,
    DateTimeOffset OccurredAt,
    DateTimeOffset? EtaAt,
    DateTimeOffset? PlannedArrivalTo,
    int? DelayMinutes,
    double? Latitude,
    double? Longitude);
