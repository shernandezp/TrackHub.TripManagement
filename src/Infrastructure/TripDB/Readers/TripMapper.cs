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

using System.Text.Json;
using NetTopologySuite.Geometries;

namespace TrackHub.TripManagement.Infrastructure.TripDB.Readers;

/// <summary>
/// Entity to view-model mapping, applied AFTER materialization on purpose.
/// <para>
/// Every reader in this project materializes entity rows and maps here rather than projecting
/// straight into a record struct inside the query. That is deliberate: Npgsql cannot translate an
/// <c>OrderBy</c> whose key is a member of a constructor-built record struct, and EF InMemory
/// evaluates such an ordering client-side so unit tests pass while production throws
/// "could not be translated" (rules.md, Forbidden patterns - it shipped in spec 09). Ordering
/// therefore always happens on entity columns before this mapper is reached.
/// </para>
/// </summary>
internal static class TripMapper
{
    internal static readonly JsonSerializerOptions Json = new(JsonSerializerDefaults.Web);

    /// <summary>
    /// <paramref name="phaseStops"/> is what the phase is read from. It is optional so the paths
    /// that genuinely have no stop context (a freshly created trip returned by its own writer) still
    /// map — an empty list yields a phase derived from status and the origin timestamps alone, which
    /// is exactly right for a trip that has no route yet.
    /// </summary>
    internal static TripVm ToVm(
        Trip trip,
        int stopCount,
        IReadOnlyCollection<PhaseStopVm>? phaseStops = null,
        int overdueGraceMinutes = TripAccountConfigVm.DefaultOverdueGraceMinutes)
    {
        var phase = TripPhaseResolver.Resolve(
            trip.Status,
            trip.PlannedStartAt,
            trip.ArmedAt,
            trip.OriginArrivedAt,
            trip.OriginDepartedAt,
            trip.OriginName,
            phaseStops ?? [],
            DateTimeOffset.UtcNow,
            overdueGraceMinutes);

        return new TripVm(
            trip.TripId,
            trip.AccountId,
            trip.Code,
            trip.Status,
            trip.TransporterId,
            trip.DriverId,
            trip.RoutePlanId,
            trip.ServiceOrderId,
            trip.ExternalReference,
            trip.CustomerName,
            trip.OriginName,
            trip.OriginPoint.Y,
            trip.OriginPoint.X,
            trip.OriginGeofenceId,
            trip.OriginRadiusMeters,
            trip.PlannedStartAt,
            trip.PlannedEndAt,
            trip.ActualStartAt,
            trip.ActualEndAt,
            trip.ArmedAt,
            trip.OriginArrivedAt,
            trip.OriginDepartedAt,
            phase.Phase,
            phase.PhaseStopName,
            phase.PhaseStopActivity,
            phase.PhaseEtaAt,
            phase.PhaseDelayed,
            trip.Notes,
            trip.LastPositionAt,
            trip.LastPoint?.Y,
            trip.LastPoint?.X,
            trip.ActualDistanceMeters,
            trip.TollVehicleClass,
            trip.DeviationOpenedAt,
            trip.CancellationReason,
            stopCount,
            phaseStops?.Count(s => string.Equals(s.Status, TripStopStatuses.Pending, StringComparison.Ordinal)) ?? 0,
            trip.LastModified);
    }

    internal static TripStopVm ToVm(TripStop stop, IReadOnlyCollection<DeliveryVm> deliveries)
        => new(
            stop.TripStopId,
            stop.AccountId,
            stop.TripId,
            stop.Sequence,
            stop.Name,
            stop.Address,
            stop.City,
            stop.Point.Y,
            stop.Point.X,
            stop.GeofenceId,
            stop.ArrivalRadiusMeters,
            stop.Activity,
            stop.PlannedArrivalFrom,
            stop.PlannedArrivalTo,
            stop.Status,
            stop.ActualArrivalAt,
            stop.ActualDepartureAt,
            stop.EtaAt,
            stop.EtaSource,
            stop.DelayAlertedAt,
            stop.RequiresPod,
            stop.Priority,
            stop.Observations,
            deliveries);

    internal static DeliveryVm ToVm(Delivery delivery)
        => new(
            delivery.DeliveryId,
            delivery.AccountId,
            delivery.TripStopId,
            delivery.Reference,
            delivery.ClientName,
            delivery.BranchName,
            delivery.ProductsSummary,
            delivery.Status,
            delivery.Observations,
            delivery.SequenceIndex);

    internal static TripAssignmentVm ToVm(TripAssignment assignment)
        => new(
            assignment.TripAssignmentId,
            assignment.AccountId,
            assignment.TripId,
            assignment.DriverId,
            assignment.TransporterId,
            assignment.Status,
            assignment.AssignedAt,
            assignment.AcknowledgedAt,
            assignment.EndedAt);

    internal static RoutePlanVm ToVm(RoutePlan plan)
        => new(
            plan.RoutePlanId,
            plan.AccountId,
            plan.TripId,
            plan.Provider,
            ToLine(plan.Geom),
            ToLine(plan.CorridorGeom),
            plan.CorridorMeters,
            plan.PlannedDistanceMeters,
            plan.PlannedDurationSeconds,
            plan.WaypointsJson,
            plan.LegsJson,
            plan.ComputedAt,
            plan.Status,
            plan.ErrorCode,
            plan.ErrorMessage,
            plan.TollVehicleClass,
            plan.EstimatedTollAmount,
            plan.TollCurrency,
            plan.TollStatus,
            DeserializeStations(plan.TollStationsJson));

    internal static TripEventVm ToVm(TripEvent tripEvent)
        => new(
            tripEvent.TripEventId,
            tripEvent.AccountId,
            tripEvent.TripId,
            tripEvent.TripStopId,
            tripEvent.EventType,
            tripEvent.OccurredAt,
            tripEvent.Source,
            tripEvent.PayloadJson);

    internal static ProofOfDeliveryVm ToVm(ProofOfDelivery pod, IReadOnlyCollection<TripDocumentVm> documents)
        => new(
            pod.ProofOfDeliveryId,
            pod.AccountId,
            pod.TripStopId,
            pod.DeliveryId,
            pod.ReceiverName,
            pod.ReceiverDocument,
            pod.CapturedAt,
            pod.Latitude,
            pod.Longitude,
            pod.Notes,
            documents);

    internal static TripDocumentVm ToVm(TripDocument document)
        => new(
            document.TripDocumentId,
            document.AccountId,
            document.TripId,
            document.TripStopId,
            document.ProofOfDeliveryId,
            document.DocumentId,
            document.Kind);

    /// <summary>
    /// Share projection. <c>Token</c> is always null here: the plaintext token is returned exactly
    /// once by the create path and is never re-readable (acceptance 23).
    /// </summary>
    internal static TripShareVm ToVm(TripShare share, string? token = null)
        => new(
            share.TripShareId,
            share.AccountId,
            share.TripId,
            share.PublicLinkGrantId,
            share.IncludeDriverName,
            share.IncludeVehicle,
            share.IncludeLivePosition,
            share.IncludeStopDetail,
            share.IncludePodSummary,
            share.IncludeRoute,
            share.CreatedByPrincipalId,
            share.ExpiresAt,
            share.RevokedAt,
            token);

    internal static TollStationVm ToVm(TollStation station)
        => new(
            station.TollStationId,
            station.Name,
            station.Code,
            station.Point.Y,
            station.Point.X,
            station.Country,
            station.Region,
            station.RoadName,
            station.Direction,
            station.Operator,
            station.Notes,
            station.Active);

    internal static TollVehicleClassVm ToVm(TollVehicleClass vehicleClass)
        => new(
            vehicleClass.TollVehicleClassId,
            vehicleClass.Code,
            vehicleClass.Name,
            vehicleClass.Description,
            vehicleClass.SortOrder,
            vehicleClass.Active);

    internal static TollTariffVm ToVm(TollTariff tariff)
        => new(
            tariff.TollTariffId,
            tariff.TollStationId,
            tariff.TollVehicleClassCode,
            tariff.Amount,
            tariff.Currency,
            tariff.EffectiveFrom,
            tariff.EffectiveTo);

    internal static TransporterTollClassVm ToVm(TransporterTollClass mapping)
        => new(
            mapping.TransporterTollClassId,
            mapping.AccountId,
            mapping.TransporterTypeId,
            mapping.TransporterId,
            mapping.TollVehicleClassCode);

    internal static GeometryLineVm? ToLine(Geometry? geometry)
        => geometry is null
            ? null
            : new GeometryLineVm([.. geometry.Coordinates.Select(c => new CoordinateVm(c.Y, c.X))]);

    internal static IReadOnlyCollection<TollStationMatchVm> DeserializeStations(string? json)
    {
        if (string.IsNullOrWhiteSpace(json))
        {
            return [];
        }

        try
        {
            return JsonSerializer.Deserialize<List<TollStationMatchVm>>(json, Json) ?? [];
        }
        catch (JsonException)
        {
            // A stored breakdown that can no longer be parsed must not take the trip detail
            // screen down: the amount and status columns still explain the estimate.
            return [];
        }
    }
}
