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
using TrackHub.TripManagement.Application.Common;
using TrackHub.TripManagement.Application.Deliveries.Commands.Delete;
using TrackHub.TripManagement.Application.Trips.Commands.Create;
using TrackHub.TripManagement.Application.Trips.Commands.Delete;
using TrackHub.TripManagement.Application.TripStops.Commands.Add;
using TrackHub.TripManagement.Application.TripStops.Commands.Remove;

namespace TrackHub.TripManagement.Application.UnitTests;

/// <summary>
/// Acceptance 1-4: the group-visibility predicate that guards the list, detail and report paths
/// must guard the WRITE paths and route replay identically, and every cross-account parent
/// reference must be rejected at write time.
/// <para>
/// Each test below is a negative: the attempt a dispatcher (or a partner integration) would
/// actually make with a borrowed id. They exist because all of these previously SUCCEEDED — the
/// single-trip lookup applied only the account predicate, the stop- and delivery-addressed commands
/// looked nothing up at all, and the transporter check returned early for anyone who sees the whole
/// account.
/// </para>
/// </summary>
[TestFixture]
public class TripVisibilityEnforcementTests
{
    private static readonly Guid ForeignTransporterId = Guid.Parse("77777777-7777-7777-7777-777777777777");
    private static readonly Guid ForeignGeofenceId = Guid.Parse("88888888-8888-8888-8888-888888888888");
    private static readonly Guid DeliveryId = Guid.Parse("99999999-9999-9999-9999-999999999999");

    // -----------------------------------------------------------------------------------------
    // The [AllowCrossAccount] compensation on the four report feeds. The marker switches the
    // pipeline guard off for EVERY caller, so this handler-side binding is the only thing keeping
    // a user from exporting another tenant's trips — pin it so it cannot rot.
    // -----------------------------------------------------------------------------------------

    /// <summary>A user asking for a FOREIGN account's report feed must be refused.</summary>
    [Test]
    public void ReportScope_UserRequestingForeignAccount_IsForbidden()
    {
        var foreignAccountId = Guid.NewGuid();

        Assert.ThrowsAsync<ForbiddenAccessException>(() => TripVisibility.ResolveReportScopeAsync(
            TestFactory.User(Roles.Manager).Object, TestFactory.UserReader().Object, foreignAccountId, CancellationToken.None));
    }

    /// <summary>A user asking for their OWN account keeps working (group scope applies).</summary>
    [Test]
    public async Task ReportScope_UserRequestingOwnAccount_Passes()
    {
        var scope = await TripVisibility.ResolveReportScopeAsync(
            TestFactory.User(Roles.User).Object, TestFactory.UserReader().Object, TestFactory.AccountId, CancellationToken.None);

        Assert.That(scope, Is.EqualTo(TestFactory.UserId), "a dispatcher stays group-scoped on the report path");
    }

    /// <summary>The service identity (non-Guid subject) sees the whole requested account.</summary>
    [Test]
    public async Task ReportScope_ServiceIdentity_IsAccountWide()
    {
        var service = new Mock<IUser>();
        service.SetupGet(u => u.Id).Returns("reporting_client");
        service.SetupGet(u => u.PrincipalType).Returns(PrincipalType.ServiceClient);

        var scope = await TripVisibility.ResolveReportScopeAsync(
            service.Object, TestFactory.UserReader().Object, Guid.NewGuid(), CancellationToken.None);

        Assert.That(scope, Is.Null);
    }

    // -----------------------------------------------------------------------------------------
    // Defect 2 - the single-trip lookup used by every write path and by route replay.
    // -----------------------------------------------------------------------------------------

    /// <summary>
    /// A dispatcher scoped to their groups must have their user id pushed into the reader, so the
    /// reader can apply the same <c>vw_visible_transporter</c> predicate the list path applies.
    /// Passing null here is exactly the bug: it means "sees the whole account".
    /// </summary>
    [Test]
    public async Task GroupScopedDispatcher_PassesTheirUserIdAsTheReaderScope()
    {
        var reader = new Mock<ITripReader>();
        reader.Setup(r => r.GetTripAsync(TestFactory.TripId, TestFactory.AccountId, TestFactory.UserId, It.IsAny<CancellationToken>()))
            .ReturnsAsync(TestFactory.Trip(TripStatuses.Created));

        var handler = new DeleteTripCommandHandler(
            new Mock<ITripWriter>().Object,
            reader.Object,
            EventWriter(hasEvents: false).Object,
            TestFactory.UserReader().Object,
            TestFactory.User(Roles.User).Object);

        await handler.Handle(new DeleteTripCommand(TestFactory.TripId), CancellationToken.None);

        reader.Verify(
            r => r.GetTripAsync(TestFactory.TripId, TestFactory.AccountId, TestFactory.UserId, It.IsAny<CancellationToken>()),
            Times.Once);
        reader.Verify(
            r => r.GetTripAsync(It.IsAny<Guid>(), It.IsAny<Guid>(), null, It.IsAny<CancellationToken>()),
            Times.Never);
    }

    /// <summary>An Administrator is account-wide and must NOT be narrowed to their own groups.</summary>
    [Test]
    public async Task Administrator_PassesNullScope_AndStillSeesTheWholeAccount()
    {
        var reader = new Mock<ITripReader>();
        reader.Setup(r => r.GetTripAsync(TestFactory.TripId, TestFactory.AccountId, null, It.IsAny<CancellationToken>()))
            .ReturnsAsync(TestFactory.Trip(TripStatuses.Created));

        var handler = new DeleteTripCommandHandler(
            new Mock<ITripWriter>().Object,
            reader.Object,
            EventWriter(hasEvents: false).Object,
            TestFactory.UserReader().Object,
            TestFactory.User(Roles.Administrator).Object);

        await handler.Handle(new DeleteTripCommand(TestFactory.TripId), CancellationToken.None);

        reader.Verify(
            r => r.GetTripAsync(TestFactory.TripId, TestFactory.AccountId, null, It.IsAny<CancellationToken>()),
            Times.Once);
    }

    /// <summary>
    /// The reader answering "not visible" must surface as 404, never 403: a dispatcher must not be
    /// able to probe which trip ids exist in sibling groups (spec 11 §7.10, non-disclosure).
    /// </summary>
    [Test]
    public void ATripOutsideTheCallersGroups_IsNotFound_NotForbidden()
    {
        var reader = new Mock<ITripReader>();
        reader.Setup(r => r.GetTripAsync(It.IsAny<Guid>(), It.IsAny<Guid>(), It.IsAny<Guid?>(), It.IsAny<CancellationToken>()))
            .ThrowsAsync(new Ardalis.GuardClauses.NotFoundException($"{TestFactory.TripId}", "Trip"));

        var writer = new Mock<ITripWriter>();
        var handler = new DeleteTripCommandHandler(
            writer.Object,
            reader.Object,
            EventWriter(hasEvents: false).Object,
            TestFactory.UserReader().Object,
            TestFactory.User(Roles.User).Object);

        Assert.ThrowsAsync<Ardalis.GuardClauses.NotFoundException>(async () =>
            await handler.Handle(new DeleteTripCommand(TestFactory.TripId), CancellationToken.None));

        writer.Verify(w => w.DeleteTripAsync(It.IsAny<Guid>(), It.IsAny<Guid>(), It.IsAny<CancellationToken>()), Times.Never);
    }

    // -----------------------------------------------------------------------------------------
    // Defect 2 - the stop- and delivery-addressed commands, which carried no trip id at all.
    // -----------------------------------------------------------------------------------------

    [Test]
    public void RemovingAStopOfAnInvisibleTrip_IsNotFound_AndWritesNothing()
    {
        var reader = new Mock<ITripReader>();
        reader.Setup(r => r.FindVisibleTripIdByStopAsync(
                TestFactory.StopId, TestFactory.AccountId, TestFactory.UserId, It.IsAny<CancellationToken>()))
            .ReturnsAsync((Guid?)null);

        var writer = new Mock<ITripStopWriter>();
        var handler = new RemoveTripStopCommandHandler(
            writer.Object, reader.Object, TestFactory.UserReader().Object, TestFactory.User(Roles.User).Object);

        Assert.ThrowsAsync<Ardalis.GuardClauses.NotFoundException>(async () =>
            await handler.Handle(new RemoveTripStopCommand(TestFactory.StopId), CancellationToken.None));

        writer.Verify(w => w.RemoveStopAsync(It.IsAny<Guid>(), It.IsAny<Guid>(), It.IsAny<CancellationToken>()), Times.Never);
    }

    [Test]
    public void DeletingADeliveryOfAnInvisibleTrip_IsNotFound_AndWritesNothing()
    {
        var reader = new Mock<ITripReader>();
        reader.Setup(r => r.FindVisibleTripIdByDeliveryAsync(
                DeliveryId, TestFactory.AccountId, TestFactory.UserId, It.IsAny<CancellationToken>()))
            .ReturnsAsync((Guid?)null);

        var writer = new Mock<IDeliveryWriter>();
        var handler = new DeleteDeliveryCommandHandler(
            writer.Object, reader.Object, TestFactory.UserReader().Object, TestFactory.User(Roles.User).Object);

        Assert.ThrowsAsync<Ardalis.GuardClauses.NotFoundException>(async () =>
            await handler.Handle(new DeleteDeliveryCommand(DeliveryId), CancellationToken.None));

        writer.Verify(w => w.DeleteDeliveryAsync(It.IsAny<Guid>(), It.IsAny<Guid>(), It.IsAny<CancellationToken>()), Times.Never);
    }

    // -----------------------------------------------------------------------------------------
    // Defect 5 - the transporter account check that whole-account principals used to skip.
    // -----------------------------------------------------------------------------------------

    /// <summary>
    /// The leak this closes is concrete: the report readers resolve TransporterName/DriverName by
    /// unscoped id, so a trip pointing at another account's transporter printed that account's
    /// transporter and driver names into this account's report output.
    /// </summary>
    [Test]
    public void AnAdministrator_CannotPointATripAtAnotherAccountsTransporter()
    {
        var reader = new Mock<ITripReader>();
        reader.Setup(r => r.TransporterExistsInAccountAsync(
                ForeignTransporterId, TestFactory.AccountId, It.IsAny<CancellationToken>()))
            .ReturnsAsync(false);

        var user = TestFactory.User(Roles.Administrator).Object;

        Assert.ThrowsAsync<Ardalis.GuardClauses.NotFoundException>(async () =>
            await TripVisibility.EnsureTransporterVisibleAsync(
                reader.Object, user, TestFactory.AccountId, TestFactory.UserId, ForeignTransporterId, CancellationToken.None));

        // The group predicate is never even consulted - the account boundary fails first.
        reader.Verify(
            r => r.IsTransporterVisibleAsync(It.IsAny<Guid>(), It.IsAny<Guid>(), It.IsAny<Guid>(), It.IsAny<CancellationToken>()),
            Times.Never);
    }

    /// <summary>An in-account transporter outside the caller's groups is still 403, not 404.</summary>
    [Test]
    public void AnInAccountTransporterOutsideTheCallersGroups_IsForbidden()
    {
        var reader = new Mock<ITripReader>();
        reader.Setup(r => r.TransporterExistsInAccountAsync(
                TestFactory.TransporterId, TestFactory.AccountId, It.IsAny<CancellationToken>()))
            .ReturnsAsync(true);
        reader.Setup(r => r.IsTransporterVisibleAsync(
                TestFactory.AccountId, TestFactory.UserId, TestFactory.TransporterId, It.IsAny<CancellationToken>()))
            .ReturnsAsync(false);

        Assert.ThrowsAsync<ForbiddenAccessException>(async () =>
            await TripVisibility.EnsureTransporterVisibleAsync(
                reader.Object, TestFactory.User(Roles.User).Object, TestFactory.AccountId,
                TestFactory.UserId, TestFactory.TransporterId, CancellationToken.None));
    }

    [Test]
    public async Task AnInAccountTransporterInsideTheCallersGroups_IsAccepted()
    {
        var reader = new Mock<ITripReader>();
        reader.Setup(r => r.TransporterExistsInAccountAsync(
                TestFactory.TransporterId, TestFactory.AccountId, It.IsAny<CancellationToken>()))
            .ReturnsAsync(true);
        reader.Setup(r => r.IsTransporterVisibleAsync(
                TestFactory.AccountId, TestFactory.UserId, TestFactory.TransporterId, It.IsAny<CancellationToken>()))
            .ReturnsAsync(true);

        await TripVisibility.EnsureTransporterVisibleAsync(
            reader.Object, TestFactory.User(Roles.User).Object, TestFactory.AccountId,
            TestFactory.UserId, TestFactory.TransporterId, CancellationToken.None);

        Assert.Pass();
    }

    // -----------------------------------------------------------------------------------------
    // Defect 4 - the stop geofence, which failed SILENTLY rather than loudly.
    // -----------------------------------------------------------------------------------------

    /// <summary>
    /// Before this check, a cross-account or simply wrong <c>GeofenceId</c> was accepted, and the
    /// arrival snapshot quietly substituted a radius buffer — the dispatcher believed detection ran
    /// against their polygon while it actually ran against a 150 m circle. A detection failure
    /// presented as a success is worse than a rejection.
    /// </summary>
    [Test]
    public void AddingAStopWithACrossAccountGeofence_IsNotFound_AndWritesNothing()
    {
        var reader = new Mock<ITripReader>();
        reader.Setup(r => r.GetTripAsync(TestFactory.TripId, TestFactory.AccountId, It.IsAny<Guid?>(), It.IsAny<CancellationToken>()))
            .ReturnsAsync(TestFactory.Trip(TripStatuses.Created));
        reader.Setup(r => r.GeofenceExistsInAccountAsync(
                ForeignGeofenceId, TestFactory.AccountId, It.IsAny<CancellationToken>()))
            .ReturnsAsync(false);

        var writer = new Mock<ITripStopWriter>();
        var handler = new AddTripStopCommandHandler(
            writer.Object, reader.Object, TestFactory.UserReader().Object, TestFactory.User().Object);

        Assert.ThrowsAsync<Ardalis.GuardClauses.NotFoundException>(async () =>
            await handler.Handle(new AddTripStopCommand(TestFactory.TripId, Stop(ForeignGeofenceId)), CancellationToken.None));

        writer.Verify(
            w => w.AddStopAsync(It.IsAny<Guid>(), It.IsAny<Guid>(), It.IsAny<TripStopDto>(), It.IsAny<CancellationToken>()),
            Times.Never);
    }

    /// <summary>A stop with no linked geofence is the common case and must not pay for the check.</summary>
    [Test]
    public async Task AddingAStopWithNoGeofence_SkipsTheLookupEntirely()
    {
        var reader = new Mock<ITripReader>();
        reader.Setup(r => r.GetTripAsync(TestFactory.TripId, TestFactory.AccountId, It.IsAny<Guid?>(), It.IsAny<CancellationToken>()))
            .ReturnsAsync(TestFactory.Trip(TripStatuses.Created));

        var writer = new Mock<ITripStopWriter>();
        writer.Setup(w => w.AddStopAsync(It.IsAny<Guid>(), It.IsAny<Guid>(), It.IsAny<TripStopDto>(), It.IsAny<CancellationToken>()))
            .ReturnsAsync(TestFactory.Stop());

        var handler = new AddTripStopCommandHandler(
            writer.Object, reader.Object, TestFactory.UserReader().Object, TestFactory.User().Object);

        await handler.Handle(new AddTripStopCommand(TestFactory.TripId, Stop(geofenceId: null)), CancellationToken.None);

        reader.Verify(
            r => r.GeofenceExistsInAccountAsync(It.IsAny<Guid>(), It.IsAny<Guid>(), It.IsAny<CancellationToken>()),
            Times.Never);
    }

    // -----------------------------------------------------------------------------------------
    // Defect 3 - ServiceOrderId, validated through the spec-12 port.
    // -----------------------------------------------------------------------------------------

    /// <summary>
    /// The port is consulted on every create. Its default implementation is permissive because
    /// spec 12 owns service orders, but the CALL SITE is what this asserts: a rejecting
    /// implementation must actually stop the write, so spec 12 enforces the reference by
    /// registration alone.
    /// </summary>
    [Test]
    public void ARejectedServiceOrderReference_StopsTheCreate()
    {
        var serviceOrderId = Guid.Parse("aaaaaaaa-aaaa-aaaa-aaaa-aaaaaaaaaaaa");

        var reader = new Mock<ITripReader>();
        reader.Setup(r => r.TransporterExistsInAccountAsync(
                TestFactory.TransporterId, TestFactory.AccountId, It.IsAny<CancellationToken>()))
            .ReturnsAsync(true);

        var serviceOrders = new Mock<IServiceOrderValidator>();
        serviceOrders.Setup(v => v.ExistsInAccountAsync(serviceOrderId, TestFactory.AccountId, It.IsAny<CancellationToken>()))
            .ReturnsAsync(false);

        var writer = new Mock<ITripWriter>();
        var handler = new CreateTripCommandHandler(
            writer.Object,
            reader.Object,
            TestFactory.UserReader().Object,
            TestFactory.User().Object,
            new Mock<IManagerValidationClient>().Object,
            serviceOrders.Object,
            new Mock<ITransporterTollClassStore>().Object);

        Assert.ThrowsAsync<Ardalis.GuardClauses.NotFoundException>(async () =>
            await handler.Handle(new CreateTripCommand(Trip(serviceOrderId)), CancellationToken.None));

        writer.Verify(
            w => w.CreateTripAsync(It.IsAny<TripDto>(), It.IsAny<Guid>(), It.IsAny<CancellationToken>()),
            Times.Never);
    }

    /// <summary>A null reference means "no service order" and must never reach the port.</summary>
    [Test]
    public async Task ANullServiceOrderReference_IsNotValidated()
    {
        var serviceOrders = new Mock<IServiceOrderValidator>();

        await TripVisibility.EnsureServiceOrderInAccountAsync(
            serviceOrders.Object, null, TestFactory.AccountId, CancellationToken.None);

        serviceOrders.Verify(
            v => v.ExistsInAccountAsync(It.IsAny<Guid>(), It.IsAny<Guid>(), It.IsAny<CancellationToken>()),
            Times.Never);
    }

    /// <summary>The shipped default accepts anything — deliberately, and only because the call sites are correct.</summary>
    [Test]
    public async Task ThePermissiveDefault_AcceptsAnyReference()
    {
        var accepted = await new PermissiveServiceOrderValidator()
            .ExistsInAccountAsync(Guid.NewGuid(), TestFactory.AccountId, CancellationToken.None);

        Assert.That(accepted, Is.True);
    }

    private static Mock<ITripEventWriter> EventWriter(bool hasEvents)
    {
        var writer = new Mock<ITripEventWriter>();
        writer.Setup(w => w.HasEventsAsync(It.IsAny<Guid>(), It.IsAny<Guid>(), It.IsAny<CancellationToken>()))
            .ReturnsAsync(hasEvents);
        return writer;
    }

    private static TripStopDto Stop(Guid? geofenceId)
        => new("Customer site", null, null, 4.7, -74.0, geofenceId, 150, TripStopActivities.Unload, null, null, false, 0, null);

    private static TripDto Trip(Guid? serviceOrderId)
        => new("TRIP-001", TestFactory.TransporterId, null, serviceOrderId, null, "ACME", "Depot", 4.65, -74.05, null, TripGeometry.DefaultRadiusMeters,
            DateTimeOffset.UtcNow, null, null, null);
}
