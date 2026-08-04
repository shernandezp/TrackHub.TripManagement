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

using Microsoft.Extensions.Logging.Abstractions;
using TrackHub.TripManagement.Domain.Constants;
using TrackHub.TripManagement.Infrastructure.TripDB.Writers;

namespace Infrastructure.UnitTests;

/// <summary>
/// The per-vehicle queue (spec 11a §7): which <c>Created</c> trip, if any, joins the detection
/// working set. Strictly one per vehicle, strictly in planned order, and only inside the activation
/// window.
/// <para>
/// This is the behaviour; <c>TripQueryTranslationTests.LoadWorkingSet_*</c> is the guard that the same
/// query survives Npgsql. Both are needed — the queue rules are what stop Thursday's trip running
/// before Monday's, and an untranslatable query would stop every trip running at all.
/// </para>
/// </summary>
[TestFixture]
public class ArmableTripSelectionTests
{
    private static readonly Guid TruckA = Guid.Parse("55555555-5555-5555-5555-555555555555");
    private static readonly Guid TruckB = Guid.Parse("66666666-6666-6666-6666-666666666666");
    private static readonly DateTimeOffset Now = new(2026, 8, 3, 12, 0, 0, TimeSpan.Zero);

    /// <summary>`now + activationLeadMinutes`, the form the reader takes the window in.</summary>
    private static readonly DateTimeOffset OneHourOut = Now.AddHours(1);

    private static async Task<WriterTestContext> SeededAsync(params (Guid Transporter, string Code, string Status, DateTimeOffset PlannedStart)[] trips)
    {
        var context = WriterTestContext.Create();
        foreach (var (transporter, code, status, plannedStart) in trips)
        {
            var trip = WriterTestData.Trip(Guid.NewGuid(), code);
            trip.TransporterId = transporter;
            trip.Status = status;
            trip.PlannedStartAt = plannedStart;
            context.Trips.Add(trip);
        }

        await context.SaveChangesAsync(CancellationToken.None);
        return context;
    }

    private static async Task<List<string>> WatchedCodesAsync(WriterTestContext context, DateTimeOffset? armableUntil)
    {
        var unit = new TripDetectionUnitOfWork(context, NullLogger<TripDetectionUnitOfWork>.Instance);
        var trips = await unit.LoadAsync(
            WriterTestData.AccountId, [TruckA, TruckB], armableUntil, CancellationToken.None);

        return [.. trips.Select(t => t.Code).Order()];
    }

    [Test]
    public async Task OnlyTheEarliestPlannedCreatedTripPerVehicleArms()
    {
        // A week planned for one truck. Exactly one row joins the working set — the set grows by one
        // per vehicle, never by the backlog (§6.1).
        using var context = await SeededAsync(
            (TruckA, "MON", TripStatuses.Created, Now.AddMinutes(30)),
            (TruckA, "TUE", TripStatuses.Created, Now.AddMinutes(40)),
            (TruckA, "WED", TripStatuses.Created, Now.AddMinutes(50)));

        Assert.That(await WatchedCodesAsync(context, OneHourOut), Is.EqualTo(new[] { "MON" }));
    }

    [Test]
    public async Task ATripOutsideItsActivationWindowDoesNotArm()
    {
        using var context = await SeededAsync((TruckA, "LATER", TripStatuses.Created, Now.AddHours(6)));

        Assert.That(await WatchedCodesAsync(context, OneHourOut), Is.Empty);
    }

    [Test]
    public async Task AVehicleAlreadyRunningATripArmsNothingElse()
    {
        // One physical unit runs one trip at a time, so the queue behind it stays untouched until
        // the current trip closes.
        using var context = await SeededAsync(
            (TruckA, "RUNNING", TripStatuses.InProgress, Now.AddHours(-2)),
            (TruckA, "NEXT", TripStatuses.Created, Now.AddMinutes(30)));

        Assert.That(await WatchedCodesAsync(context, OneHourOut), Is.EqualTo(new[] { "RUNNING" }));
    }

    /// <summary>
    /// A PAUSED trip still holds its vehicle. Pause is the dispatcher taking control of a trip that
    /// is still under way (§5.2) — the load is on the truck and the truck is out there.
    /// <para>
    /// Keying "busy" on <c>InProgress</c> alone made pausing look like releasing: trip N+1 armed and
    /// auto-started on a unit already committed, so one truck ran two trips at once, both measuring
    /// the same positions. The unique index carries the same filter for the same reason.
    /// </para>
    /// <para>
    /// The paused trip is not WATCHED either — automation stays off it — so the working set here is
    /// empty rather than containing it.
    /// </para>
    /// </summary>
    [Test]
    public async Task APausedTripStillHoldsItsVehicleAndBlocksTheQueue()
    {
        using var context = await SeededAsync(
            (TruckA, "PAUSED", TripStatuses.Paused, Now.AddHours(-2)),
            (TruckA, "NEXT", TripStatuses.Created, Now.AddMinutes(30)));

        Assert.That(await WatchedCodesAsync(context, OneHourOut), Is.Empty);
    }

    /// <summary>
    /// The queue-blocking rule stated as behaviour: an overdue trip nobody cancelled keeps its
    /// place. Running Thursday's trip before anyone noticed Monday's never left is a dispatcher's
    /// decision, not the system's (§7).
    /// </summary>
    [Test]
    public async Task AnOverdueTripStillBlocksTheOnesBehindIt()
    {
        using var context = await SeededAsync(
            (TruckA, "OVERDUE", TripStatuses.Created, Now.AddHours(-8)),
            (TruckA, "NEXT", TripStatuses.Created, Now.AddMinutes(30)));

        Assert.That(await WatchedCodesAsync(context, OneHourOut), Is.EqualTo(new[] { "OVERDUE" }));
    }

    [Test]
    public async Task EachVehicleArmsItsOwnNextTripIndependently()
    {
        using var context = await SeededAsync(
            (TruckA, "A1", TripStatuses.Created, Now.AddMinutes(10)),
            (TruckA, "A2", TripStatuses.Created, Now.AddMinutes(20)),
            (TruckB, "B1", TripStatuses.Created, Now.AddMinutes(15)));

        Assert.That(await WatchedCodesAsync(context, OneHourOut), Is.EqualTo(new[] { "A1", "B1" }));
    }

    /// <summary>
    /// The account kill switch reaching the reader: with no window, the working set is exactly what
    /// it was before zero-touch — <c>InProgress</c> only (§8).
    /// </summary>
    [Test]
    public async Task WithNoArmingWindowOnlyRunningTripsAreWatched()
    {
        using var context = await SeededAsync(
            (TruckA, "RUNNING", TripStatuses.InProgress, Now.AddHours(-2)),
            (TruckB, "QUEUED", TripStatuses.Created, Now.AddMinutes(30)));

        Assert.That(await WatchedCodesAsync(context, null), Is.EqualTo(new[] { "RUNNING" }));
    }

    [Test]
    public async Task ATerminalTripIsNeverWatched()
    {
        using var context = await SeededAsync(
            (TruckA, "DONE", TripStatuses.Completed, Now.AddHours(-4)),
            (TruckB, "PAUSED", TripStatuses.Paused, Now.AddHours(-1)));

        // Paused is deliberately absent too: pause is the dispatcher's "I am taking control"
        // switch, and it removes the trip from detection entirely (§5.2).
        Assert.That(await WatchedCodesAsync(context, OneHourOut), Is.Empty);
    }
}
