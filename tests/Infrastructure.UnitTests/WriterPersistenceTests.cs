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
using Common.Domain.Enums;
using Microsoft.EntityFrameworkCore;
using Moq;
using TrackHub.TripManagement.Domain.Constants;
using TrackHub.TripManagement.Domain.Interfaces;
using TrackHub.TripManagement.Domain.Records;
using TrackHub.TripManagement.Infrastructure.TripDB.Entities;
using TrackHub.TripManagement.Infrastructure.TripDB.Writers;

namespace Infrastructure.UnitTests;

/// <summary>
/// Every writer mutation actually reaches the database.
/// <para>
/// This is the counterpart to <see cref="MultiWriteRequestTests"/>, and it guards the OPPOSITE
/// failure. Under a <c>NoTracking</c> context a mutation is only saved if the row was fetched
/// <c>AsTracking()</c>; forget that and <c>SaveChangesAsync</c> writes NOTHING, returns success, and
/// the caller — and its audit trail — reports a change that never happened. Unlike the attach
/// conflict it replaced, that failure is completely silent, so it cannot be left to review.
/// </para>
/// <para>
/// Every method here is one whose tracking was changed when the <c>Attach</c> calls came out. Each
/// test writes, then re-reads with <c>AsNoTracking</c> — reading through the tracked instance would
/// pass on an unsaved in-memory mutation and prove nothing.
/// </para>
/// </summary>
[TestFixture]
public class WriterPersistenceTests
{
    private static readonly Guid TripId = Guid.Parse("aaaaaaaa-0000-0000-0000-000000000001");
    private static readonly Guid StopId = Guid.Parse("aaaaaaaa-0000-0000-0000-000000000002");
    private static readonly Guid SecondStopId = Guid.Parse("aaaaaaaa-0000-0000-0000-000000000003");
    private static readonly Guid DeliveryId = Guid.Parse("aaaaaaaa-0000-0000-0000-000000000004");
    private static readonly Guid ShareId = Guid.Parse("aaaaaaaa-0000-0000-0000-000000000005");
    private static readonly Guid DriverId = Guid.Parse("aaaaaaaa-0000-0000-0000-000000000006");
    private static readonly Guid TransporterId = Guid.Parse("55555555-5555-5555-5555-555555555555");

    private static IUser User()
    {
        var user = new Mock<IUser>();
        user.SetupGet(u => u.PrincipalType).Returns(PrincipalType.User);
        user.SetupGet(u => u.UserId).Returns(Guid.Parse("99999999-9999-9999-9999-999999999999"));
        return user.Object;
    }

    /// <summary>A context holding a trip with two stops and one delivery, tracker cleared.</summary>
    private static async Task<WriterTestContext> SeededAsync()
    {
        var context = WriterTestContext.Create();
        context.Trips.Add(WriterTestData.Trip(TripId, "TRIP-P"));
        context.TripStops.Add(WriterTestData.Stop(StopId, TripId, TripStopStatuses.Pending, 1));
        context.TripStops.Add(WriterTestData.Stop(SecondStopId, TripId, TripStopStatuses.Pending, 2));
        context.Deliveries.Add(new Delivery
        {
            DeliveryId = DeliveryId,
            AccountId = WriterTestData.AccountId,
            TripStopId = StopId,
            ClientName = "Acme",
            Status = DeliveryStatuses.Pending,
        });

        await context.SaveChangesAsync(CancellationToken.None);
        context.ChangeTracker.Clear();
        return context;
    }

    [Test]
    public async Task AssignTrip_PersistsTheDriverAndEndsThePriorAssignment()
    {
        using var context = await SeededAsync();
        context.TripAssignments.Add(new TripAssignment
        {
            AccountId = WriterTestData.AccountId,
            TripId = TripId,
            DriverId = Guid.NewGuid(),
            TransporterId = TransporterId,
            Status = TripAssignmentStatuses.Active,
            AssignedAt = DateTimeOffset.UtcNow.AddDays(-1),
        });
        await context.SaveChangesAsync(CancellationToken.None);
        context.ChangeTracker.Clear();

        await new TripWriter(context, User())
            .AssignTripAsync(TripId, WriterTestData.AccountId, DriverId, null, CancellationToken.None);

        var assignments = await context.TripAssignments.AsNoTracking().ToListAsync(CancellationToken.None);
        var trip = await context.Trips.AsNoTracking().FirstAsync(t => t.TripId == TripId, CancellationToken.None);
        Assert.Multiple(() =>
        {
            Assert.That(trip.DriverId, Is.EqualTo(DriverId));
            Assert.That(assignments.Count(a => a.Status == TripAssignmentStatuses.Active), Is.EqualTo(1));
            Assert.That(assignments.Count(a => a.Status == TripAssignmentStatuses.Ended), Is.EqualTo(1), "the prior assignment was not ended in the database");
        });
    }

    [Test]
    public async Task ReorderStops_PersistsTheNewSequence()
    {
        using var context = await SeededAsync();

        await new TripStopWriter(context)
            .ReorderStopsAsync(TripId, WriterTestData.AccountId, [SecondStopId, StopId], CancellationToken.None);

        var stops = await context.TripStops.AsNoTracking()
            .Where(s => s.TripId == TripId).OrderBy(s => s.Sequence).ToListAsync(CancellationToken.None);
        Assert.That(stops.Select(s => s.TripStopId), Is.EqualTo(new[] { SecondStopId, StopId }));
    }

    [Test]
    public async Task RemoveStop_DeletesItAndRenumbersWhatIsLeft()
    {
        using var context = await SeededAsync();

        await new TripStopWriter(context).RemoveStopAsync(StopId, WriterTestData.AccountId, CancellationToken.None);

        var stops = await context.TripStops.AsNoTracking()
            .Where(s => s.TripId == TripId).ToListAsync(CancellationToken.None);
        Assert.Multiple(() =>
        {
            Assert.That(stops, Has.Count.EqualTo(1));
            Assert.That(stops[0].TripStopId, Is.EqualTo(SecondStopId));
            Assert.That(stops[0].Sequence, Is.EqualTo(1), "the surviving stop was not renumbered in the database");
        });
    }

    [Test]
    public async Task UpdateDelivery_PersistsTheEditedFields()
    {
        using var context = await SeededAsync();

        await new DeliveryWriter(context).UpdateDeliveryAsync(
            DeliveryId, WriterTestData.AccountId,
            new DeliveryDto("REF-9", "Globex", null, null, null, 4), CancellationToken.None);

        var delivery = await context.Deliveries.AsNoTracking().FirstAsync(d => d.DeliveryId == DeliveryId, CancellationToken.None);
        Assert.Multiple(() =>
        {
            Assert.That(delivery.ClientName, Is.EqualTo("Globex"));
            Assert.That(delivery.Reference, Is.EqualTo("REF-9"));
            Assert.That(delivery.SequenceIndex, Is.EqualTo(4));
        });
    }

    [Test]
    public async Task UpdateDeliveryOutcome_PersistsTheOutcome()
    {
        using var context = await SeededAsync();

        var recorded = await new DeliveryWriter(context).UpdateDeliveryOutcomeAsync(
            DeliveryId, WriterTestData.AccountId, DeliveryStatuses.Delivered, "Left at gate",
            $"trip-outcome:{DeliveryId:N}", CancellationToken.None);

        var delivery = await context.Deliveries.AsNoTracking().FirstAsync(d => d.DeliveryId == DeliveryId, CancellationToken.None);
        Assert.Multiple(() =>
        {
            Assert.That(recorded, Is.True);
            Assert.That(delivery.Status, Is.EqualTo(DeliveryStatuses.Delivered));
            Assert.That(delivery.Observations, Is.EqualTo("Left at gate"));
        });
    }

    [Test]
    public async Task MarkStopDeliveries_PersistsTheStatusAcrossThePendingRows()
    {
        using var context = await SeededAsync();

        await new DeliveryWriter(context).MarkStopDeliveriesAsync(
            StopId, WriterTestData.AccountId, DeliveryStatuses.Delivered, CancellationToken.None);

        var delivery = await context.Deliveries.AsNoTracking().FirstAsync(d => d.DeliveryId == DeliveryId, CancellationToken.None);
        Assert.That(delivery.Status, Is.EqualTo(DeliveryStatuses.Delivered));
    }

    [Test]
    public async Task RevokeShare_PersistsTheRevocationInstant()
    {
        using var context = await SeededAsync();
        context.TripShares.Add(new TripShare
        {
            TripShareId = ShareId,
            AccountId = WriterTestData.AccountId,
            TripId = TripId,
            PublicLinkGrantId = Guid.NewGuid(),
            CreatedByPrincipalId = "dispatcher",
            ExpiresAt = DateTimeOffset.UtcNow.AddDays(7),
        });
        await context.SaveChangesAsync(CancellationToken.None);
        context.ChangeTracker.Clear();

        await new TripShareWriter(context).RevokeShareAsync(ShareId, WriterTestData.AccountId, CancellationToken.None);

        var share = await context.TripShares.AsNoTracking().FirstAsync(s => s.TripShareId == ShareId, CancellationToken.None);
        Assert.That(share.RevokedAt, Is.Not.Null, "a revoked link that is not revoked in the database still resolves");
    }

    [Test]
    public async Task SetTransporterTollClass_PersistsAChangeToAnExistingMapping()
    {
        using var context = await SeededAsync();
        context.TransporterTollClasses.Add(new TransporterTollClass
        {
            AccountId = WriterTestData.AccountId,
            TransporterId = TransporterId,
            TollVehicleClassCode = "II",
        });
        await context.SaveChangesAsync(CancellationToken.None);
        context.ChangeTracker.Clear();

        await new TransporterTollClassStore(context).SetMappingAsync(
            WriterTestData.AccountId, null, TransporterId, "V", CancellationToken.None);

        var mappings = await context.TransporterTollClasses.AsNoTracking().ToListAsync(CancellationToken.None);
        Assert.Multiple(() =>
        {
            Assert.That(mappings, Has.Count.EqualTo(1), "the mapping was inserted again instead of updated");
            Assert.That(mappings[0].TollVehicleClassCode, Is.EqualTo("V"));
        });
    }

    [Test]
    public async Task UpdateTollStation_PersistsTheEdit()
    {
        using var context = await SeededAsync();
        var stationId = Guid.NewGuid();
        context.TollStations.Add(new TollStation
        {
            TollStationId = stationId,
            Name = "Peaje Norte",
            Code = "PN",
            Point = WriterTestData.Point(4.8, -74.1),
        });
        await context.SaveChangesAsync(CancellationToken.None);
        context.ChangeTracker.Clear();

        await new TollCatalogWriter(context, new Mock<ITollCatalogReader>().Object, User()).UpdateStationAsync(
            stationId,
            new TollStationDto("Peaje Norte II", "PN2", 4.9, -74.2, "CO", null, null, null, null, null),
            CancellationToken.None);

        var station = await context.TollStations.AsNoTracking().FirstAsync(s => s.TollStationId == stationId, CancellationToken.None);
        Assert.Multiple(() =>
        {
            Assert.That(station.Name, Is.EqualTo("Peaje Norte II"));
            Assert.That(station.Code, Is.EqualTo("PN2"));
        });
    }

    [Test]
    public async Task DeactivateTollVehicleClass_PersistsTheFlag()
    {
        using var context = await SeededAsync();
        var classId = Guid.NewGuid();
        context.TollVehicleClasses.Add(new TollVehicleClass { TollVehicleClassId = classId, Code = "IV", Name = "Four axles" });
        await context.SaveChangesAsync(CancellationToken.None);
        context.ChangeTracker.Clear();

        await new TollCatalogWriter(context, new Mock<ITollCatalogReader>().Object, User())
            .DeactivateVehicleClassAsync(classId, CancellationToken.None);

        var vehicleClass = await context.TollVehicleClasses.AsNoTracking().FirstAsync(c => c.TollVehicleClassId == classId, CancellationToken.None);
        Assert.That(vehicleClass.Active, Is.False, "a class reported deactivated is still priceable");
    }
}
