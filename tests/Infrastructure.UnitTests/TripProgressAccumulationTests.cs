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

using Common.Application.Interfaces;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.Logging.Abstractions;
using Moq;
using TrackHub.TripManagement.Infrastructure.TripDB.Writers;

namespace Infrastructure.UnitTests;

/// <summary>
/// <c>Trip.TryAdvanceProgress</c>, buffered by the detection unit of work — the odometer, the last-seen point, and the
/// out-of-order guard.
/// <para>
/// This is the only writer detection calls for EVERY position of EVERY open trip, and it is the
/// source of <c>ActualDistanceMeters</c> — the number every distance report, every plan-vs-actual
/// comparison and every fuel reconciliation downstream is built on. It had no test at all.
/// </para>
/// <para>
/// The out-of-order guard is the part that matters most, and it is why the method returns
/// <see cref="bool"/> rather than <c>Task</c>. A redelivered fix (a client retry, or a WithRetry
/// policy firing after a timeout on a call that had already committed) must be REJECTED: accepting
/// it adds a phantom leg to the odometer, rewinds <c>LastPositionAt</c>, and — because the caller
/// keys off this return value — lets one genuinely out-of-corridor fix count three times toward the
/// three-fix deviation threshold and open a false episode with a real alert.
/// </para>
/// </summary>
[TestFixture]
public class TripProgressAccumulationTests
{
    private static readonly Guid TripId = Guid.Parse("88888888-8888-8888-8888-888888888888");
    private static readonly DateTimeOffset T0 = new(2026, 7, 21, 12, 0, 0, TimeSpan.Zero);

    private static async Task<WriterTestContext> SeededAsync()
    {
        var context = WriterTestContext.Create();
        context.Trips.Add(WriterTestData.Trip(TripId, "TRIP-PROGRESS"));
        await context.SaveChangesAsync(CancellationToken.None);
        return context;
    }

    private static TripWriter Writer(WriterTestContext context)
    {
        var user = new Mock<IUser>();
        user.SetupGet(u => u.Id).Returns(Guid.NewGuid().ToString());
        user.SetupGet(u => u.PrincipalType).Returns(PrincipalType.ServiceClient);
        return new TripWriter(context, user.Object);
    }

    [Test]
    public async Task FirstFix_RecordsTheLastPointAndTimestampAndAddsNoDistance()
    {
        // Detection passes 0 for the first fix because there is no previous point to measure from.
        // A trip must not inherit a distance from its very first sighting.
        using var context = await SeededAsync();

        var unit = await WriterTestData.LoadedUnitAsync(context);

        var accepted = unit.TryAdvanceProgress(
            TripId, 4.65, -74.05, T0, 0d);

        await unit.FlushAsync(CancellationToken.None);

        var trip = await context.Trips.AsNoTracking().FirstAsync(t => t.TripId == TripId, CancellationToken.None);
        Assert.Multiple(() =>
        {
            Assert.That(accepted, Is.True);
            Assert.That(trip.ActualDistanceMeters, Is.EqualTo(0d));
            Assert.That(trip.LastPositionAt, Is.EqualTo(T0));
            Assert.That(trip.LastPoint, Is.Not.Null);
            Assert.That(trip.LastPoint!.Y, Is.EqualTo(4.65).Within(1e-9), "Y is latitude — PostGIS stores lon,lat");
            Assert.That(trip.LastPoint!.X, Is.EqualTo(-74.05).Within(1e-9));
        });
    }

    [Test]
    public async Task SuccessiveFixes_AccumulateTheOdometerRatherThanReplacingIt()
    {
        // ActualDistanceMeters is a running total, not the last leg. Assigning instead of adding
        // leaves every completed trip reporting only the distance of its final hop.
        using var context = await SeededAsync();
        var unit = await WriterTestData.LoadedUnitAsync(context);

        unit.TryAdvanceProgress(TripId, 4.65, -74.05, T0, 0d);
        unit.TryAdvanceProgress(TripId, 4.66, -74.06, T0.AddMinutes(1), 1500d);
        unit.TryAdvanceProgress(TripId, 4.67, -74.07, T0.AddMinutes(2), 2500d);

        await unit.FlushAsync(CancellationToken.None);

        var trip = await context.Trips.AsNoTracking().FirstAsync(t => t.TripId == TripId, CancellationToken.None);
        Assert.Multiple(() =>
        {
            Assert.That(trip.ActualDistanceMeters, Is.EqualTo(4000d).Within(1e-6));
            Assert.That(trip.LastPositionAt, Is.EqualTo(T0.AddMinutes(2)), "the last-seen stamp advances with the newest accepted fix");
            Assert.That(trip.LastPoint!.Y, Is.EqualTo(4.67).Within(1e-9));
        });
    }

    // ----- The out-of-order guard --------------------------------------------------------------

    [Test]
    public async Task AnOlderFix_IsRejectedAndMovesNothing()
    {
        // Position feeds reorder. A late-arriving older fix must not rewind the trip's idea of
        // where the vehicle is, and must not add its leg to the odometer.
        using var context = await SeededAsync();
        var unit = await WriterTestData.LoadedUnitAsync(context);

        unit.TryAdvanceProgress(TripId, 4.65, -74.05, T0.AddMinutes(5), 0d);
        var accepted = unit.TryAdvanceProgress(TripId, 9.99, -70.0, T0, 999_999d);

        await unit.FlushAsync(CancellationToken.None);

        var trip = await context.Trips.AsNoTracking().FirstAsync(t => t.TripId == TripId, CancellationToken.None);
        Assert.Multiple(() =>
        {
            Assert.That(accepted, Is.False, "the rejection must be REPORTED — detection keys its whole skip decision off this");
            Assert.That(trip.ActualDistanceMeters, Is.EqualTo(0d), "a stale fix must never move the odometer");
            Assert.That(trip.LastPositionAt, Is.EqualTo(T0.AddMinutes(5)));
            Assert.That(trip.LastPoint!.Y, Is.EqualTo(4.65).Within(1e-9), "the last-seen point was rewound to a stale location");
        });
    }

    [Test]
    public async Task AReplayedFixWithTheSameTimestamp_IsRejected()
    {
        // "Not newer" is the rule, not "older". An exact replay — the common shape of a retried
        // batch — carries the same DeviceDateTime and would otherwise be counted twice.
        using var context = await SeededAsync();
        var unit = await WriterTestData.LoadedUnitAsync(context);

        unit.TryAdvanceProgress(TripId, 4.65, -74.05, T0, 0d);
        var replay = unit.TryAdvanceProgress(TripId, 4.65, -74.05, T0, 1500d);

        await unit.FlushAsync(CancellationToken.None);

        var trip = await context.Trips.AsNoTracking().FirstAsync(t => t.TripId == TripId, CancellationToken.None);
        Assert.Multiple(() =>
        {
            Assert.That(replay, Is.False);
            Assert.That(trip.ActualDistanceMeters, Is.EqualTo(0d), "a replay double-counted its leg into the odometer");
        });
    }

    [Test]
    public async Task ANegativeAddedDistance_NeverRewindsTheOdometer()
    {
        // Defensive, and cheap: the odometer is monotonic by definition, and a negative leg from a
        // future caller bug would silently understate a completed trip.
        using var context = await SeededAsync();
        var unit = await WriterTestData.LoadedUnitAsync(context);

        unit.TryAdvanceProgress(TripId, 4.65, -74.05, T0, 5000d);
        unit.TryAdvanceProgress(TripId, 4.66, -74.06, T0.AddMinutes(1), -3000d);

        await unit.FlushAsync(CancellationToken.None);

        var trip = await context.Trips.AsNoTracking().FirstAsync(t => t.TripId == TripId, CancellationToken.None);
        Assert.That(trip.ActualDistanceMeters, Is.EqualTo(5000d).Within(1e-6));
    }

    /// <summary>
    /// Tenant scope on the write path, not only on the read path: the position feed is keyed by
    /// transporter, and a mis-scoped batch must not write one account's movement onto another's trip.
    /// <para>
    /// The guarantee moved with the code and got stronger. It used to be a per-call account filter
    /// on the update; now the working set itself is loaded per account, so a trip belonging to
    /// somebody else is not merely un-writable — it is not in the unit at all, and every mutator is
    /// a no-op against an id it never loaded.
    /// </para>
    /// </summary>
    [Test]
    public async Task AFixForAnotherAccountsTrip_IsNotEvenInTheWorkingSet()
    {
        using var context = await SeededAsync();

        var foreignUnit = new TripDetectionUnitOfWork(context, NullLogger<TripDetectionUnitOfWork>.Instance);
        var loaded = await foreignUnit.LoadAsync(
            Guid.NewGuid(), [WriterTestData.TransporterId], null, CancellationToken.None);

        var accepted = foreignUnit.TryAdvanceProgress(TripId, 4.65, -74.05, T0, 1000d);
        await foreignUnit.FlushAsync(CancellationToken.None);

        var trip = await context.Trips.AsNoTracking().FirstAsync(t => t.TripId == TripId, CancellationToken.None);
        Assert.Multiple(() =>
        {
            Assert.That(loaded, Is.Empty, "another account's trip was loaded into the working set");
            Assert.That(accepted, Is.False);
            Assert.That(trip.ActualDistanceMeters, Is.EqualTo(0d));
        });
    }
}
