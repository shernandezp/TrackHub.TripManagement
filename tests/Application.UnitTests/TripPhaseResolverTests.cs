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

namespace TrackHub.TripManagement.Application.UnitTests;

/// <summary>
/// Spec 11a §4.3/§10: the phase is what the dispatch board leads with, and it is DERIVED from
/// recorded facts on every read. A board full of rows reading <c>InProgress</c> is exactly why a
/// dispatcher used to have to open each one.
/// </summary>
[TestFixture]
public class TripPhaseResolverTests
{
    private static readonly DateTimeOffset Now = new(2026, 8, 3, 12, 0, 0, TimeSpan.Zero);

    [Test]
    public void ACreatedTripInsideItsWindow_ReadsScheduledUntilItIsArmed()
    {
        var phase = TripPhaseResolver.Resolve(
            TripStatuses.Created, Now.AddHours(1), null, null, null, "Plant 3", [], Now, 120);

        Assert.That(phase.Phase, Is.EqualTo(TripPhases.Scheduled));
    }

    [Test]
    public void AnArmedTrip_ReadsArmedAndNamesWhereItIsWaiting()
    {
        var phase = TripPhaseResolver.Resolve(
            TripStatuses.Created, Now.AddMinutes(30), Now.AddMinutes(-10), null, null, "Plant 3", [], Now, 120);

        Assert.Multiple(() =>
        {
            Assert.That(phase.Phase, Is.EqualTo(TripPhases.Armed));
            Assert.That(phase.PhaseStopName, Is.EqualTo("Plant 3"));
        });
    }

    /// <summary>
    /// Overdue is a READING, never a status change: the trip stays Created and stays queued, and the
    /// decision to cancel or re-plan belongs to a dispatcher. Silently skipping it would run
    /// Thursday's trip before anyone noticed Monday's never left (§7).
    /// </summary>
    [Test]
    public void ATripPastItsGrace_ReadsOverdueWithoutChangingStatus()
    {
        var phase = TripPhaseResolver.Resolve(
            TripStatuses.Created, Now.AddHours(-3), Now.AddHours(-4), null, null, "Plant 3", [], Now, 120);

        Assert.That(phase.Phase, Is.EqualTo(TripPhases.Overdue));
    }

    [Test]
    public void ARunningTripStillAtItsOrigin_ReadsLoading()
    {
        var phase = TripPhaseResolver.Resolve(
            TripStatuses.InProgress, Now.AddHours(-1), Now.AddHours(-2), Now.AddHours(-2), null, "Plant 3",
            [new PhaseStopVm(1, "Client X", TripStopActivities.Unload, TripStopStatuses.Pending, null, null)], Now, 120);

        Assert.Multiple(() =>
        {
            Assert.That(phase.Phase, Is.EqualTo(TripPhases.AtOrigin));
            Assert.That(phase.PhaseStopName, Is.EqualTo("Plant 3"));

            // Origin dwell IS loading time by definition (§4.2) — stated, not looked up.
            Assert.That(phase.PhaseStopActivity, Is.EqualTo(TripStopActivities.Load));
        });
    }

    /// <summary>
    /// A running trip whose origin was never MEASURED must not be narrated as loading. Three
    /// populations land here and none of them is exotic: an account with <c>autoLifecycle</c> off
    /// never arms, a trip that predates the zero-touch migration has no <c>OriginGeom</c> (§14), and
    /// a manual override starts a trip detection never saw. Reading "no departure recorded" as
    /// "still at the origin" pinned all three on "Loading at Plant 3" for their whole run.
    /// </summary>
    [Test]
    public void ARunningTripWhoseOriginWasNeverMeasured_ReadsFromItsStopsInsteadOfClaimingItIsLoading()
    {
        var eta = Now.AddMinutes(35);
        var phase = TripPhaseResolver.Resolve(
            TripStatuses.InProgress, Now.AddHours(-1), null, null, null, "Plant 3",
            [new PhaseStopVm(1, "Client X", TripStopActivities.Unload, TripStopStatuses.Pending, eta, null)], Now, 120);

        Assert.Multiple(() =>
        {
            Assert.That(phase.Phase, Is.EqualTo(TripPhases.InTransit));
            Assert.That(phase.PhaseStopName, Is.EqualTo("Client X"));
        });
    }

    [Test]
    public void OnceItLeavesTheOrigin_ItReadsInTransitTowardsTheNextStopWithThatStopsEta()
    {
        var eta = Now.AddMinutes(35);
        var phase = TripPhaseResolver.Resolve(
            TripStatuses.InProgress, Now.AddHours(-2), Now.AddHours(-3), Now.AddHours(-2), Now.AddHours(-1), "Plant 3",
            [new PhaseStopVm(1, "Client X", TripStopActivities.Unload, TripStopStatuses.Pending, eta, null)], Now, 120);

        Assert.Multiple(() =>
        {
            Assert.That(phase.Phase, Is.EqualTo(TripPhases.InTransit));
            Assert.That(phase.PhaseStopName, Is.EqualTo("Client X"));
            Assert.That(phase.PhaseEtaAt, Is.EqualTo(eta));
        });
    }

    [Test]
    public void AnArrivedStop_ReadsAtStopAndCarriesThatStopsActivity()
    {
        var phase = TripPhaseResolver.Resolve(
            TripStatuses.InProgress, Now.AddHours(-2), Now.AddHours(-3), Now.AddHours(-2), Now.AddHours(-1), "Plant 3",
            [
                new PhaseStopVm(1, "Client X", TripStopActivities.Unload, TripStopStatuses.Arrived, null, null),
                new PhaseStopVm(2, "Client Y", TripStopActivities.Unload, TripStopStatuses.Pending, null, null),
            ],
            Now, 120);

        Assert.Multiple(() =>
        {
            Assert.That(phase.Phase, Is.EqualTo(TripPhases.AtStop));
            Assert.That(phase.PhaseStopName, Is.EqualTo("Client X"));
            Assert.That(phase.PhaseStopActivity, Is.EqualTo(TripStopActivities.Unload));
        });
    }

    [Test]
    public void APausedTrip_ReadsPausedRatherThanWhereverItHappensToBe()
    {
        // Pause is the dispatcher's "I am taking control" switch, and automation is suspended. The
        // board must say so instead of continuing to narrate a measurement nobody is taking.
        var phase = TripPhaseResolver.Resolve(
            TripStatuses.Paused, Now.AddHours(-2), Now.AddHours(-3), Now.AddHours(-2), Now.AddHours(-1), "Plant 3",
            [new PhaseStopVm(1, "Client X", TripStopActivities.Unload, TripStopStatuses.Arrived, null, null)], Now, 120);

        Assert.That(phase.Phase, Is.EqualTo(TripPhases.Paused));
    }

    [TestCase(TripStatuses.Completed, TripPhases.Completed)]
    [TestCase(TripStatuses.Cancelled, TripPhases.Cancelled)]
    [TestCase(TripStatuses.Aborted, TripPhases.Aborted)]
    public void TerminalStatuses_ReadAsThemselves(string status, string expected)
    {
        var phase = TripPhaseResolver.Resolve(
            status, Now.AddHours(-2), Now.AddHours(-3), Now.AddHours(-2), Now.AddHours(-1), "Plant 3", [], Now, 120);

        Assert.That(phase.Phase, Is.EqualTo(expected));
    }
}
