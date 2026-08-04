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
/// Trip-level export row. Flat by design: a report row is READ, never joined. Display names and the
/// route-plan roll-up are resolved server-side so Reporting drains one paged feed instead of
/// fanning out per row to Manager (spec 11 §13).
/// <para>
/// The measured durations answer the question the module exists to answer — how long did loading,
/// transit and the whole trip actually take (spec 11a §13). They are computed here rather than in
/// Reporting so every consumer gets the same arithmetic, and each is null when the measurement it
/// depends on was never taken: a trip whose start was declared has no loading time, and saying so
/// beats reporting zero.
/// </para>
/// </summary>
public readonly record struct TripReportRowVm(
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
    DateTimeOffset PlannedStartAt,
    DateTimeOffset? PlannedEndAt,
    DateTimeOffset? ActualStartAt,
    DateTimeOffset? ActualEndAt,
    DateTimeOffset? OriginArrivedAt,
    DateTimeOffset? OriginDepartedAt,
    int? LoadingMinutes,
    int? TransitMinutes,
    int? TotalMinutes,
    string? Notes,
    DateTimeOffset? LastPositionAt,
    double? LastLatitude,
    double? LastLongitude,
    double ActualDistanceMeters,
    string? TollVehicleClass,
    DateTimeOffset? DeviationOpenedAt,
    string? CancellationReason,
    int StopCount,
    DateTimeOffset LastModified,
    string? TransporterName,
    string? DriverName,
    double? PlannedDistanceMeters,
    decimal? EstimatedTollAmount,
    string? TollCurrency,
    string? TollStatus);

/// <summary>
/// A page of report rows.
/// <para>
/// <b><paramref name="NextSkip"/> is the producer's own cursor, not <c>Items.Count</c>.</b> The toll
/// feed pages ROUTE PLANS and then expands each into one row per matched station, so a page of 500
/// plans can return 1500 rows. A consumer that advanced by rows skipped 1500 plans and compared a
/// row count against a plan <paramref name="TotalCount"/>, terminating early and silently dropping
/// trips from a financial report. Consumers must follow this cursor and stop on
/// <paramref name="HasMore"/>; only the producer knows what unit it pages in.
/// </para>
/// </summary>
public readonly record struct TripReportPageVm(IReadOnlyCollection<TripReportRowVm> Items, int TotalCount, int NextSkip, bool HasMore);

/// <summary>
/// Stop-level export row. <paramref name="CustomerName"/> is the stop's delivery client name,
/// falling back to the trip's customer when the stop has no delivery. The four counts bucket the
/// stop's deliveries by status: <c>Delivered</c>, <c>Rejected</c> and <c>PartiallyDelivered</c>.
/// </summary>
/// <param name="Activity">
/// What the vehicle did here. It is what turns the dwell between
/// <paramref name="ActualArrivalAt"/> and <paramref name="ActualDepartureAt"/> from an anonymous
/// number of minutes into loading or unloading time (spec 11a §13).
/// </param>
public readonly record struct TripStopReportRowVm(
    Guid TripStopId,
    Guid TripId,
    string TripCode,
    string? TransporterName,
    string? DriverName,
    string? CustomerName,
    int Sequence,
    string Name,
    string Activity,
    string Status,
    DateTimeOffset? PlannedArrivalFrom,
    DateTimeOffset? PlannedArrivalTo,
    DateTimeOffset? ActualArrivalAt,
    DateTimeOffset? ActualDepartureAt,
    int DeliveryCount,
    int DeliveredCount,
    int FailedDeliveryCount,
    int PartialDeliveryCount);

/// <summary>
/// A page of report rows.
/// <para>
/// <b><paramref name="NextSkip"/> is the producer's own cursor, not <c>Items.Count</c>.</b> The toll
/// feed pages ROUTE PLANS and then expands each into one row per matched station, so a page of 500
/// plans can return 1500 rows. A consumer that advanced by rows skipped 1500 plans and compared a
/// row count against a plan <paramref name="TotalCount"/>, terminating early and silently dropping
/// trips from a financial report. Consumers must follow this cursor and stop on
/// <paramref name="HasMore"/>; only the producer knows what unit it pages in.
/// </para>
/// </summary>
public readonly record struct TripStopReportPageVm(IReadOnlyCollection<TripStopReportRowVm> Items, int TotalCount, int NextSkip, bool HasMore);

/// <summary>
/// One route-plan/station match.
/// <para>
/// <b><paramref name="Amount"/> stays null and <paramref name="HasTariff"/> false when no tariff
/// covers the class on the plan date.</b> That pair is what renders the <c>PartialNoTariff</c>
/// column in <c>trip-toll-cost</c>. Emitting <c>0</c> instead would make a catalog gap invisible and
/// net it silently into the total — the exact failure mode spec 11 §18.9 exists to prevent.
/// </para>
/// </summary>
public readonly record struct TripTollReportRowVm(
    Guid TripId,
    string TripCode,
    Guid RoutePlanId,
    DateTimeOffset PlannedStartAt,
    string? TollVehicleClass,
    Guid TollStationId,
    string StationName,
    string? StationCode,
    string? RoadName,
    string? Direction,
    decimal? Amount,
    string? Currency,
    bool HasTariff);

/// <summary>
/// A page of report rows.
/// <para>
/// <b><paramref name="NextSkip"/> is the producer's own cursor, not <c>Items.Count</c>.</b> The toll
/// feed pages ROUTE PLANS and then expands each into one row per matched station, so a page of 500
/// plans can return 1500 rows. A consumer that advanced by rows skipped 1500 plans and compared a
/// row count against a plan <paramref name="TotalCount"/>, terminating early and silently dropping
/// trips from a financial report. Consumers must follow this cursor and stop on
/// <paramref name="HasMore"/>; only the producer knows what unit it pages in.
/// </para>
/// </summary>
public readonly record struct TripTollReportPageVm(IReadOnlyCollection<TripTollReportRowVm> Items, int TotalCount, int NextSkip, bool HasMore);

/// <summary>
/// POD register row. <paramref name="DocumentCount"/> is a count, never the document list: this
/// feed is a register, and the bytes stay behind Manager's access policy (spec 11 §13, SC-06).
/// </summary>
public readonly record struct TripPodReportRowVm(
    Guid ProofOfDeliveryId,
    Guid TripId,
    string TripCode,
    Guid TripStopId,
    int StopSequence,
    string StopName,
    string ReceiverName,
    string? ReceiverDocument,
    DateTimeOffset CapturedAt,
    double? Latitude,
    double? Longitude,
    int DocumentCount);

/// <summary>
/// A page of report rows.
/// <para>
/// <b><paramref name="NextSkip"/> is the producer's own cursor, not <c>Items.Count</c>.</b> The toll
/// feed pages ROUTE PLANS and then expands each into one row per matched station, so a page of 500
/// plans can return 1500 rows. A consumer that advanced by rows skipped 1500 plans and compared a
/// row count against a plan <paramref name="TotalCount"/>, terminating early and silently dropping
/// trips from a financial report. Consumers must follow this cursor and stop on
/// <paramref name="HasMore"/>; only the producer knows what unit it pages in.
/// </para>
/// </summary>
public readonly record struct TripPodReportPageVm(IReadOnlyCollection<TripPodReportRowVm> Items, int TotalCount, int NextSkip, bool HasMore);
