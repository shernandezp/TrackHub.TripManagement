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
/// What a trip is doing right now, in the dispatcher's terms rather than the database's: "loading at
/// Plant 3", "in transit to Client X", "overdue to start". Status alone cannot say any of that — a
/// board full of rows reading <c>InProgress</c> is why the dispatcher used to have to open each one.
/// </summary>
public readonly record struct TripPhaseVm(
    string Phase,
    string? PhaseStopName,
    string? PhaseStopActivity,
    DateTimeOffset? PhaseEtaAt,

    // Whether the stop this phase is about has already raised TripDelayed. It travels with the
    // phase so the board's "Delayed" exception means exactly what the alert means — the portal used
    // to re-derive it as "next-stop ETA later than the trip's planned END", a second, looser
    // definition of the same word sitting next to the first on the same screen.
    bool PhaseDelayed);

/// <summary>The minimum a stop contributes to the phase reading.</summary>
public readonly record struct PhaseStopVm(
    int Sequence,
    string Name,
    string Activity,
    string Status,
    DateTimeOffset? EtaAt,
    DateTimeOffset? DelayAlertedAt);

/// <summary>
/// The single derivation of <see cref="TripPhaseVm"/> (spec 11a §4.3), shared by every reader.
/// <para>
/// Deliberately a pure function over recorded facts and never a stored column: a persisted phase is
/// one more thing that can disagree with the timestamps it was derived from, and it would need
/// rewriting on every fix.
/// </para>
/// </summary>
public static class TripPhaseResolver
{
    public static TripPhaseVm Resolve(
        string status,
        DateTimeOffset plannedStartAt,
        DateTimeOffset? armedAt,
        DateTimeOffset? originArrivedAt,
        DateTimeOffset? originDepartedAt,
        string originName,
        IReadOnlyCollection<PhaseStopVm> stops,
        DateTimeOffset now,
        int overdueGraceMinutes)
    {
        switch (status)
        {
            case TripStatuses.Completed:
                return new TripPhaseVm(TripPhases.Completed, null, null, null, false);
            case TripStatuses.Cancelled:
                return new TripPhaseVm(TripPhases.Cancelled, null, null, null, false);
            case TripStatuses.Aborted:
                return new TripPhaseVm(TripPhases.Aborted, null, null, null, false);
            case TripStatuses.Paused:
                return new TripPhaseVm(TripPhases.Paused, null, null, null, false);
        }

        if (string.Equals(status, TripStatuses.Created, StringComparison.Ordinal))
        {
            // Overdue is a READING, not a status change: the trip stays Created and stays queued.
            // Skipping it silently would run Thursday's trip before anyone noticed Monday's never
            // left, and that call belongs to a dispatcher (spec 11a §7).
            if (now > plannedStartAt.AddMinutes(overdueGraceMinutes))
            {
                return new TripPhaseVm(TripPhases.Overdue, null, null, null, false);
            }

            // No PhaseEtaAt before the trip is running. It means "when we expect to reach
            // PhaseStopName", and the only honest answer for a trip that has not left is the
            // planned start — which the board already shows in its own column. Putting it here
            // rendered "Waiting at Plant 3 (ETA 07:00)", an estimate of nothing.
            return armedAt.HasValue
                ? new TripPhaseVm(TripPhases.Armed, originName, TripStopActivities.Load, null, false)
                : new TripPhaseVm(TripPhases.Scheduled, null, null, null, false);
        }

        // Running, and MEASURED into its origin zone without having measurably left it: that is
        // loading, because origin dwell IS loading time by definition (§4.2) — which is why the
        // activity is stated rather than looked up.
        //
        // Both halves matter. Keying on "no departure recorded" alone read AtOrigin for every trip
        // whose origin is not measured at all, and those are not edge cases: an account running with
        // `autoLifecycle` off never arms, a trip that predates the zero-touch migration has no
        // OriginGeom (§14), and a manual override starts a trip that detection never saw. All three
        // sat on "Loading at <origin>" for their entire run while the truck was hundreds of km away,
        // which is worse than no phase at all. With no origin measurement the stops are the only
        // honest source, so fall through to them.
        if (originArrivedAt is not null && originDepartedAt is null)
        {
            return new TripPhaseVm(TripPhases.AtOrigin, originName, TripStopActivities.Load, null, false);
        }

        var atStop = stops
            .Where(s => string.Equals(s.Status, TripStopStatuses.Arrived, StringComparison.Ordinal))
            .OrderBy(s => s.Sequence)
            .Select(s => (PhaseStopVm?)s)
            .FirstOrDefault();

        if (atStop is { } arrived)
        {
            return new TripPhaseVm(TripPhases.AtStop, arrived.Name, arrived.Activity, null, arrived.DelayAlertedAt is not null);
        }

        var next = stops
            .Where(s => string.Equals(s.Status, TripStopStatuses.Pending, StringComparison.Ordinal))
            .OrderBy(s => s.Sequence)
            .Select(s => (PhaseStopVm?)s)
            .FirstOrDefault();

        return next is { } pending
            ? new TripPhaseVm(TripPhases.InTransit, pending.Name, pending.Activity, pending.EtaAt, pending.DelayAlertedAt is not null)

            // Running with nothing left open: the route is done and auto-completion has not landed
            // yet (a stop was skipped manually, or the sweep is a cycle away). "In transit" with no
            // destination is the honest reading — it is on the road and no longer expected anywhere.
            : new TripPhaseVm(TripPhases.InTransit, null, null, null, false);
    }
}
