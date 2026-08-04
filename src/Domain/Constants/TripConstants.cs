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

namespace TrackHub.TripManagement.Domain.Constants;

/// <summary>
/// Trip lifecycle statuses and the transition matrix that governs them (spec 11 §17.11).
/// Stored as strings; the matrix is the single source of truth for every lifecycle command.
/// </summary>
public static class TripStatuses
{
    public const string Created = nameof(Created);
    public const string InProgress = nameof(InProgress);
    public const string Paused = nameof(Paused);
    public const string Completed = nameof(Completed);
    public const string Cancelled = nameof(Cancelled);
    public const string Aborted = nameof(Aborted);

    public static readonly IReadOnlyCollection<string> All =
        [Created, InProgress, Paused, Completed, Cancelled, Aborted];

    /// <summary>Terminal statuses: no transition leaves them and no mutation is accepted.</summary>
    public static readonly IReadOnlySet<string> Terminal =
        new HashSet<string>(StringComparer.Ordinal) { Completed, Cancelled, Aborted };

    private static readonly Dictionary<string, IReadOnlySet<string>> Transitions = new(StringComparer.Ordinal)
    {
        [Created] = new HashSet<string>(StringComparer.Ordinal) { InProgress, Cancelled },
        [InProgress] = new HashSet<string>(StringComparer.Ordinal) { Paused, Completed, Aborted, Cancelled },

        // Paused → Completed is a manual-only edge (spec 11a §5.1). Without it a dispatcher who
        // took control of a finished trip had to Resume it — briefly handing it back to automation —
        // purely so they could close it.
        [Paused] = new HashSet<string>(StringComparer.Ordinal) { InProgress, Completed, Cancelled, Aborted },
        [Completed] = new HashSet<string>(StringComparer.Ordinal),
        [Cancelled] = new HashSet<string>(StringComparer.Ordinal),
        [Aborted] = new HashSet<string>(StringComparer.Ordinal),
    };

    public static bool IsValid(string? value) => value != null && All.Contains(value);

    public static bool IsTerminal(string status) => Terminal.Contains(status);

    /// <summary>
    /// The trip has passed its start, so the per-stop arrival geometry has already been snapshotted
    /// and any stop added or edited now must be snapshotted individually.
    /// <para>
    /// <c>Paused</c> counts. Only the <c>Created → InProgress</c> transition takes the bulk
    /// snapshot, and Resume does not repeat it, so a stop added while paused was left with a null
    /// <c>ArrivalGeom</c> — detection filters those out, so it could never auto-arrive and the trip
    /// became completable only by manual override or <c>force</c>.
    /// </para>
    /// </summary>
    public static bool HasStarted(string status)
        => string.Equals(status, InProgress, StringComparison.Ordinal)
            || string.Equals(status, Paused, StringComparison.Ordinal);

    public static bool CanTransition(string from, string to)
        => Transitions.TryGetValue(from, out var allowed) && allowed.Contains(to);
}

/// <summary>Stop progression: <c>Pending → Arrived → Departed</c>, or <c>Pending → Skipped</c>.</summary>
public static class TripStopStatuses
{
    public const string Pending = nameof(Pending);
    public const string Arrived = nameof(Arrived);
    public const string Departed = nameof(Departed);
    public const string Skipped = nameof(Skipped);

    public static readonly IReadOnlyCollection<string> All = [Pending, Arrived, Departed, Skipped];

    public static bool IsValid(string? value) => value != null && All.Contains(value);

    /// <summary>A stop that has reached <c>Departed</c> or <c>Skipped</c> is closed to further progress.</summary>
    public static bool IsClosed(string status)
        => string.Equals(status, Departed, StringComparison.Ordinal)
        || string.Equals(status, Skipped, StringComparison.Ordinal);
}

/// <summary>
/// The zone radii this module accepts. Shared by the origin and by every stop on purpose — a trip's
/// ends are measured exactly the way its middle is (spec 11a §4.1).
/// </summary>
public static class TripGeometry
{
    public const int DefaultRadiusMeters = 150;
    public const int MinRadiusMeters = 50;
    public const int MaxRadiusMeters = 5000;

    /// <summary>Clamps operator- or partner-supplied radii into the accepted band.</summary>
    public static int NormalizeRadius(int radiusMeters)
        => radiusMeters < MinRadiusMeters || radiusMeters > MaxRadiusMeters
            ? DefaultRadiusMeters
            : radiusMeters;
}

/// <summary>
/// What a stop is FOR. Dwell without this is an anonymous number of minutes; with it the same
/// measurement reads as loading time or unloading time in the reports (spec 11a §4.2).
/// </summary>
public static class TripStopActivities
{
    public const string Load = nameof(Load);
    public const string Unload = nameof(Unload);
    public const string Other = nameof(Other);

    public static readonly IReadOnlyCollection<string> All = [Load, Unload, Other];

    public static bool IsValid(string? value) => value != null && All.Contains(value);

    /// <summary>
    /// Normalizes free-form input (CSV column, partner payload) to a catalog value, defaulting to
    /// <see cref="Unload"/> — the overwhelmingly common case for a delivery stop.
    /// </summary>
    public static string Normalize(string? value)
        => All.FirstOrDefault(a => string.Equals(a, value?.Trim(), StringComparison.OrdinalIgnoreCase)) ?? Unload;
}

/// <summary>
/// The live phase of a trip, DERIVED server-side from status plus the measured origin and stop
/// timestamps (spec 11a §4.3). Never stored: it is a reading of the recorded facts, and a stored
/// copy would be one more thing that can disagree with them.
/// </summary>
public static class TripPhases
{
    public const string Scheduled = nameof(Scheduled);
    public const string Armed = nameof(Armed);
    public const string AtOrigin = nameof(AtOrigin);
    public const string InTransit = nameof(InTransit);
    public const string AtStop = nameof(AtStop);
    public const string Overdue = nameof(Overdue);
    public const string Paused = nameof(Paused);
    public const string Completed = nameof(Completed);
    public const string Cancelled = nameof(Cancelled);
    public const string Aborted = nameof(Aborted);

    public static readonly IReadOnlyCollection<string> All =
        [Scheduled, Armed, AtOrigin, InTransit, AtStop, Overdue, Paused, Completed, Cancelled, Aborted];

    public static bool IsValid(string? value) => value != null && All.Contains(value);
}

public static class DeliveryStatuses
{
    public const string Pending = nameof(Pending);
    public const string Delivered = nameof(Delivered);
    public const string PartiallyDelivered = nameof(PartiallyDelivered);
    public const string Rejected = nameof(Rejected);

    public static readonly IReadOnlyCollection<string> All = [Pending, Delivered, PartiallyDelivered, Rejected];

    public static bool IsValid(string? value) => value != null && All.Contains(value);
}

public static class TripAssignmentStatuses
{
    public const string Active = nameof(Active);
    public const string Ended = nameof(Ended);
    public const string Cancelled = nameof(Cancelled);

    public static readonly IReadOnlyCollection<string> All = [Active, Ended, Cancelled];

    public static bool IsValid(string? value) => value != null && All.Contains(value);
}

public static class RoutePlanStatuses
{
    public const string Ready = nameof(Ready);
    public const string Failed = nameof(Failed);

    public static readonly IReadOnlyCollection<string> All = [Ready, Failed];

    public static bool IsValid(string? value) => value != null && All.Contains(value);
}

public static class RoutePlanProviders
{
    public const string OpenRouteService = nameof(OpenRouteService);
    public const string Manual = nameof(Manual);

    public static readonly IReadOnlyCollection<string> All = [OpenRouteService, Manual];

    public static bool IsValid(string? value) => value != null && All.Contains(value);
}

/// <summary>
/// Where a stop's ETA came from. Recorded per stop so the UI can be honest about confidence
/// instead of presenting a planned-schedule fallback as a live estimate (spec 11 §18.11).
/// </summary>
public static class EtaSources
{
    public const string Ors = nameof(Ors);
    public const string Planned = nameof(Planned);
    public const string Unavailable = nameof(Unavailable);

    public static readonly IReadOnlyCollection<string> All = [Ors, Planned, Unavailable];

    public static bool IsValid(string? value) => value != null && All.Contains(value);
}

/// <summary>
/// Outcome of toll matching over a route plan. <see cref="PartialNoTariff"/> exists so an
/// estimate can never silently understate cost: a matched station with no tariff for the trip's
/// class contributes 0 AND says so (spec 11 §6.2, §18.9).
/// </summary>
public static class TollStatuses
{
    public const string Computed = nameof(Computed);
    public const string PartialNoTariff = nameof(PartialNoTariff);
    public const string NoStations = nameof(NoStations);
    public const string NotComputed = nameof(NotComputed);

    /// <summary>
    /// The matched stations price in more than one currency, so no single total is meaningful.
    /// <para>
    /// The amount is null and the per-station breakdown carries each station's own amount and
    /// currency. Previously the service summed across currencies and labelled the result with
    /// whichever it matched FIRST — 12 500 COP + 3.50 USD reported as "12503.50 COP", and reversing
    /// the match order reported the same route in USD. Reachable on ordinary Colombian border
    /// corridors (Cúcuta–San Antonio, Ipiales–Tulcán). It is the same class of lie
    /// <see cref="PartialNoTariff"/> exists to prevent, and worse for being silent.
    /// </para>
    /// </summary>
    public const string MixedCurrency = nameof(MixedCurrency);

    public static readonly IReadOnlyCollection<string> All =
        [Computed, PartialNoTariff, NoStations, NotComputed, MixedCurrency];

    public static bool IsValid(string? value) => value != null && All.Contains(value);
}

/// <summary>Who produced a <c>TripEvent</c>. Manual overrides and detections share one log.</summary>
public static class TripEventSources
{
    public const string Portal = nameof(Portal);
    public const string Driver = nameof(Driver);
    public const string Detection = nameof(Detection);
    public const string Job = nameof(Job);
    public const string ServiceClient = nameof(ServiceClient);

    public static readonly IReadOnlyCollection<string> All = [Portal, Driver, Detection, Job, ServiceClient];

    public static bool IsValid(string? value) => value != null && All.Contains(value);
}

/// <summary>
/// Trip domain event types. These literals travel to Manager's <c>recordAlertEvent</c> and are
/// mirrored by <c>AlertEventTypes</c> there — keep both catalogs aligned.
/// </summary>
public static class TripEventTypes
{
    public const string TripCreated = nameof(TripCreated);
    public const string TripUpdated = nameof(TripUpdated);
    public const string TripAssigned = nameof(TripAssigned);
    public const string TripAssignmentAcknowledged = nameof(TripAssignmentAcknowledged);
    public const string TripStarted = nameof(TripStarted);
    public const string TripPaused = nameof(TripPaused);
    public const string TripResumed = nameof(TripResumed);
    /// <summary>
    /// The vehicle left the origin zone: loading ended, transit began.
    /// <para>
    /// TIMELINE-ONLY. Deliberately NOT mirrored in Manager's <c>AlertEventTypes</c> — it is a
    /// measurement, not something anyone needs to be woken up for, and adding it would put a new
    /// literal on the wire for no subscriber (spec 11a §11).
    /// </para>
    /// </summary>
    public const string TripOriginDeparted = nameof(TripOriginDeparted);
    public const string TripStopArrived = nameof(TripStopArrived);
    public const string TripStopDeparted = nameof(TripStopDeparted);
    public const string TripStopSkipped = nameof(TripStopSkipped);
    public const string TripRouteDeviation = nameof(TripRouteDeviation);
    public const string TripDelayed = nameof(TripDelayed);
    public const string TripPodSubmitted = nameof(TripPodSubmitted);
    public const string TripDeliveryOutcomeRecorded = nameof(TripDeliveryOutcomeRecorded);
    public const string TripCompleted = nameof(TripCompleted);
    public const string TripCancelled = nameof(TripCancelled);
    public const string TripAborted = nameof(TripAborted);
    public const string TripShared = nameof(TripShared);
    public const string TripShareRevoked = nameof(TripShareRevoked);
    public const string TripRoutePlanned = nameof(TripRoutePlanned);
    public const string TripRoutePlanFailed = nameof(TripRoutePlanFailed);
    public const string TripStartDue = nameof(TripStartDue);
    public const string TollStationChanged = nameof(TollStationChanged);
    public const string TollTariffChanged = nameof(TollTariffChanged);

    /// <summary>
    /// The timeline event a status transition produces. <c>Created → InProgress</c> is a start and
    /// <c>Paused → InProgress</c> is a resume — the same destination status, two different things
    /// that happened, which is why the ORIGIN matters here.
    /// </summary>
    public static string ForTransition(string fromStatus, string toStatus) => toStatus switch
    {
        TripStatuses.InProgress => string.Equals(fromStatus, TripStatuses.Created, StringComparison.Ordinal)
            ? TripStarted
            : TripResumed,
        TripStatuses.Paused => TripPaused,
        TripStatuses.Completed => TripCompleted,
        TripStatuses.Cancelled => TripCancelled,
        TripStatuses.Aborted => TripAborted,
        _ => TripUpdated,
    };
}

public static class TripDocumentKinds
{
    public const string Signature = nameof(Signature);
    public const string Photo = nameof(Photo);
    public const string Manifest = nameof(Manifest);
    public const string BillOfLading = nameof(BillOfLading);
    public const string Receipt = nameof(Receipt);
    public const string Other = nameof(Other);

    public static readonly IReadOnlyCollection<string> All = [Signature, Photo, Manifest, BillOfLading, Receipt, Other];

    public static bool IsValid(string? value) => value != null && All.Contains(value);
}

/// <summary>
/// Specific, localizable rejection codes. Spec 11 §9 requires these rather than generic failures
/// so spec 10's offline error centre can explain a rejection to a driver without guessing.
/// </summary>
public static class TripErrorCodes
{
    public const string TripNotActive = "TRIP_NOT_ACTIVE";
    public const string TripAlreadyTerminal = "TRIP_ALREADY_TERMINAL";
    public const string InvalidTransition = "TRIP_INVALID_TRANSITION";
    public const string StopAlreadyDeparted = "STOP_ALREADY_DEPARTED";
    public const string StopNotArrived = "STOP_NOT_ARRIVED";
    public const string StopAlreadySkipped = "STOP_ALREADY_SKIPPED";
    public const string StopsNotComplete = "TRIP_STOPS_NOT_COMPLETE";
    public const string PodDocumentNotClean = "POD_DOCUMENT_NOT_CLEAN";
    public const string DuplicateTripCode = "TRIP_DUPLICATE_CODE";
    public const string DuplicateExternalReference = "TRIP_DUPLICATE_EXTERNAL_REFERENCE";
    public const string TripHasHistory = "TRIP_HAS_HISTORY";

    /// <summary>
    /// The transporter is already running another trip. One physical unit runs one trip at a time,
    /// enforced by <c>ux_trips_transporterid_inprogress</c> (spec 11a §4.1).
    /// </summary>
    public const string TransporterBusy = "TRIP_TRANSPORTER_BUSY";

    /// <summary>
    /// The trip is armed — detection is watching it — so its transporter and origin are frozen.
    /// Re-pointing a watched trip mid-decision changes what the running measurement means (§12.4).
    /// </summary>
    public const string TripArmed = "TRIP_ARMED";

    /// <summary>
    /// An "already in transit" trip was declared without a start time and no recorded visit could be
    /// found to back-fill from, so there is nothing honest to stamp <c>ActualStartAt</c> with (§5.4).
    /// </summary>
    public const string StartEvidenceRequired = "TRIP_START_EVIDENCE_REQUIRED";
    public const string DriverNotAssignable = "TRIP_DRIVER_NOT_ASSIGNABLE";
    public const string RoutingNotConfigured = "ROUTING_NOT_CONFIGURED";
    public const string RoutingUnavailable = "ROUTING_UNAVAILABLE";

    /// <summary>
    /// The provider answered, but the geometry it returned cannot produce a usable route line or
    /// corridor (fewer than two coordinates, or a buffer that degenerates to a non-polygon). Stored
    /// as a <c>Failed</c> plan rather than a <c>Ready</c> one with null geometry: a Ready plan with
    /// no corridor makes every later position read as out-of-corridor (spec 11 §6.1, §7.4).
    /// </summary>
    public const string RoutingInvalidGeometry = "ROUTING_INVALID_GEOMETRY";
    public const string OverlappingTariff = "TOLL_OVERLAPPING_TARIFF";
    public const string DuplicateTollStation = "TOLL_DUPLICATE_STATION";
    public const string DuplicateTollVehicleClass = "TOLL_DUPLICATE_VEHICLE_CLASS";
    /// <summary>Kept unprefixed: it is already on the wire in the CSV row-error report.</summary>
    public const string UnknownVehicleClass = "UNKNOWN_VEHICLE_CLASS";
    public const string ShareRevoked = "TRIP_SHARE_REVOKED";
}

/// <summary>
/// Cross-service literals this module sends to Manager. Kept together so a rename is one edit.
/// </summary>
public static class TripSharing
{
    /// <summary><c>PublicLinkGrant.ResourceType</c> for a shared trip.</summary>
    public const string ResourceType = "Trip";

    /// <summary>The only scope a trip public link ever carries.</summary>
    public const string TrackScope = "trip.track";

    /// <summary><c>AlertEvent.SourceModule</c> for everything this service emits.</summary>
    public const string SourceModule = "TrackHub.TripManagement";
}
