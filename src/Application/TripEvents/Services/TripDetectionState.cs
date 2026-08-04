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

namespace TrackHub.TripManagement.Application.TripEvents.Services;

/// <summary>
/// Per-trip mutable state for one detection pass.
/// <para>
/// This is a WITHIN-BATCH optimisation only. The source of truth is the database: every field that
/// spans fixes is seeded from the persisted trip/stop row and written back through the writers.
/// Router pushes exactly ONE position per transporter per call, so a batch-local departure clock or
/// deviation run-length is discarded before it can ever mature — which is precisely why departure
/// and deviation detection never fired in production (spec 11 §7.4).
/// </para>
/// </summary>
public sealed class TripDetectionState(OpenTripVm trip)
{
    private readonly List<OpenTripStopVm> stops = [.. trip.Stops.OrderBy(s => s.Sequence)];

    public Guid TripId { get; } = trip.TripId;

    public string Code { get; } = trip.Code;

    /// <summary>
    /// Mutable within the pass: a <c>Created</c> trip that auto-starts becomes <c>InProgress</c>
    /// for every step that follows, in this fix and in the rest of the batch.
    /// </summary>
    public string Status { get; set; } = trip.Status;

    public bool IsCreated => string.Equals(Status, TripStatuses.Created, StringComparison.Ordinal);

    public bool IsInProgress => string.Equals(Status, TripStatuses.InProgress, StringComparison.Ordinal);

    public DateTimeOffset PlannedStartAt { get; } = trip.PlannedStartAt;

    public DateTimeOffset? ArmedAt { get; set; } = trip.ArmedAt;

    /// <summary>
    /// Whether the origin snapshot exists. Set by a successful arming, and false for every trip that
    /// predates zero-touch — origin measurement simply never fires for those (§14).
    /// </summary>
    public bool HasOriginGeom { get; set; } = trip.HasOriginGeom;

    public DateTimeOffset? OriginArrivedAt { get; set; } = trip.OriginArrivedAt;

    public DateTimeOffset? OriginDepartedAt { get; set; } = trip.OriginDepartedAt;

    /// <summary>Persisted origin exit-debounce clock, seeded from the column so the 30 s window spans calls.</summary>
    public DateTimeOffset? OriginOutsideSince { get; set; } = trip.OriginOutsideSinceAt;

    /// <summary>Whether the fix currently being processed falls inside the origin zone.</summary>
    public bool InsideOrigin { get; set; }

    public Guid TransporterId { get; } = trip.TransporterId;

    public Guid? DriverId { get; } = trip.DriverId;

    public Guid? RoutePlanId { get; } = trip.RoutePlanId;

    public bool HasReadyRoutePlan { get; } = trip.HasReadyRoutePlan;

    public IReadOnlyList<OpenTripStopVm> Stops => stops;

    public double? LastLatitude { get; set; } = trip.LastLatitude;

    public double? LastLongitude { get; set; } = trip.LastLongitude;

    /// <summary>
    /// Set while a deviation episode is open; cleared by re-entry into the corridor. Seeded from
    /// the persisted column, and — because it is persisted — it is also the episode's identity: the
    /// <c>TripEvent</c> idempotency key derives from it, so one episode mints exactly one key even
    /// across batches and process restarts.
    /// </summary>
    public DateTimeOffset? DeviationOpenedAt { get; set; } = trip.DeviationOpenedAt;

    /// <summary>Persisted run length of consecutive out-of-corridor fixes.</summary>
    public int ConsecutiveOutside { get; set; } = trip.ConsecutiveOutsideFixes;

    /// <summary>Stops whose arrival geometry contains the fix currently being processed.</summary>
    public HashSet<Guid> ContainingStops { get; set; } = [];

    /// <summary>
    /// When each arrived stop was first seen outside its geometry (departure debounce), seeded from
    /// the persisted <c>TripStop.OutsideSinceAt</c> so the 30 s window spans calls.
    /// </summary>
    public Dictionary<Guid, DateTimeOffset> OutsideSince { get; } = trip.Stops
        .Where(s => s.OutsideSinceAt.HasValue)
        .ToDictionary(s => s.TripStopId, s => s.OutsideSinceAt!.Value);

    public void MarkStopStatus(Guid tripStopId, string status)
    {
        for (var i = 0; i < stops.Count; i++)
        {
            if (stops[i].TripStopId != tripStopId)
                continue;

            stops[i] = stops[i] with { Status = status };
            return;
        }
    }
}
