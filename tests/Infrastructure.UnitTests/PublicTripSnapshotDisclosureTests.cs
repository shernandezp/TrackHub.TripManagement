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

using Microsoft.EntityFrameworkCore;
using NetTopologySuite.Geometries;
using System.Text.Json;
using TrackHub.TripManagement.Domain.Constants;
using TrackHub.TripManagement.Domain.Models;
using TrackHub.TripManagement.Infrastructure.TripDB;
using TrackHub.TripManagement.Infrastructure.TripDB.Entities;
using TrackHub.TripManagement.Infrastructure.TripDB.Readers;

namespace Infrastructure.UnitTests;

/// <summary>
/// Tests over the anonymous public-tracking disclosure boundary (spec 11 §7.8, acceptance 23).
/// <para>
/// This suite exists because there were previously ZERO tests here and four separate leaks survived
/// into shipped code: every other stop's exact street address projected into the "city" slot, the
/// consignee's name projected with no flag gating it at all, the full planned route attached
/// unconditionally, and — in the other direction — two flags (<c>IncludeVehicle</c>,
/// <c>IncludeDriverName</c>) that were inert. A field-by-field assertion of the all-off and all-on
/// snapshots, plus a sweep asserting the forbidden data cannot appear under ANY flag combination,
/// is what makes those failures loud instead of silent.
/// </para>
/// </summary>
[TestFixture]
public sealed class PublicTripSnapshotDisclosureTests
{
    private static readonly Guid AccountId = Guid.Parse("aaaaaaaa-0000-0000-0000-000000000001");
    private static readonly Guid OtherAccountId = Guid.Parse("aaaaaaaa-0000-0000-0000-000000000002");
    private static readonly Guid TripId = Guid.Parse("bbbbbbbb-0000-0000-0000-000000000001");
    private static readonly Guid StopId = Guid.Parse("cccccccc-0000-0000-0000-000000000001");
    private static readonly Guid TransporterId = Guid.Parse("dddddddd-0000-0000-0000-000000000001");
    private static readonly Guid DriverId = Guid.Parse("eeeeeeee-0000-0000-0000-000000000001");
    private static readonly Guid GrantId = Guid.Parse("ffffffff-0000-0000-0000-000000000001");

    // Every one of these is data §7.8 forbids in a public snapshot. Each appears in the seeded rows
    // so a regression that starts projecting it is caught by the sweep below rather than by a
    // customer noticing their competitor's drop list.
    private static readonly string[] ForbiddenValues =
    [
        "Cra 7 #71-52 Oficina 401",   // TripStop.Address - the exact delivery address
        "ACME Distribucion S.A.S.",   // Trip.CustomerName - one link holder's identity, to another
        "Handle with care - dispute", // Trip.Notes - internal notes
        "Ramirez",                    // the driver's family name (given name only is allowed)
        "Gomez",                      // ditto
        "3105557788",                 // driver contact data
        "Warehouse loading bay code", // TripStop.Observations
        "VII",                        // toll vehicle class
        "187500.00",                  // estimated toll amount
        "COP",                        // toll currency
        "Ricardo Perez",              // POD receiver name
        "CC 1020304050",              // POD receiver document
    ];

    private static ApplicationDbContext NewContext()
        => new(new DbContextOptionsBuilder<ApplicationDbContext>()
            .UseInMemoryDatabase($"trip-disclosure-{Guid.NewGuid()}")
            .UseQueryTrackingBehavior(QueryTrackingBehavior.NoTracking)
            .ConfigureWarnings(w => w.Ignore(Microsoft.EntityFrameworkCore.Diagnostics.InMemoryEventId.TransactionIgnoredWarning))
            .Options);

    private static async Task<ApplicationDbContext> SeedAsync(
        TripShare share,
        string tripStatus = TripStatuses.InProgress)
    {
        var context = NewContext();

        context.Trips.Add(new Trip
        {
            TripId = TripId,
            AccountId = AccountId,
            Code = "TRIP-0001",
            Status = tripStatus,
            TransporterId = TransporterId,
            DriverId = DriverId,
            CustomerName = "ACME Distribucion S.A.S.",
            Notes = "Handle with care - dispute",
            TollVehicleClass = "VII",
            OriginName = "Depot",
            OriginPoint = NewPoint(4.65, -74.05),
            PlannedStartAt = new DateTimeOffset(2026, 7, 21, 12, 0, 0, TimeSpan.Zero),
            PlannedEndAt = new DateTimeOffset(2026, 7, 21, 20, 0, 0, TimeSpan.Zero),
            ActualStartAt = new DateTimeOffset(2026, 7, 21, 12, 5, 0, TimeSpan.Zero),
            LastPoint = NewPoint(4.71, -74.07),
            LastPositionAt = new DateTimeOffset(2026, 7, 21, 15, 0, 0, TimeSpan.Zero),
        });

        context.TripStops.Add(new TripStop
        {
            TripStopId = StopId,
            AccountId = AccountId,
            TripId = TripId,
            Sequence = 1,
            Name = "Bodega Norte",
            Address = "Cra 7 #71-52 Oficina 401",
            City = "Bogota",
            Observations = "Warehouse loading bay code",
            Point = NewPoint(4.7, -74.0),
            Status = TripStopStatuses.Arrived,
            EtaSource = EtaSources.Ors,
            EtaAt = new DateTimeOffset(2026, 7, 21, 16, 0, 0, TimeSpan.Zero),
            ActualArrivalAt = new DateTimeOffset(2026, 7, 21, 15, 45, 0, TimeSpan.Zero),
            PlannedArrivalFrom = new DateTimeOffset(2026, 7, 21, 15, 0, 0, TimeSpan.Zero),
            PlannedArrivalTo = new DateTimeOffset(2026, 7, 21, 17, 0, 0, TimeSpan.Zero),
        });

        context.ProofsOfDelivery.Add(new ProofOfDelivery
        {
            AccountId = AccountId,
            TripStopId = StopId,
            ReceiverName = "Ricardo Perez",
            ReceiverDocument = "CC 1020304050",
            CapturedAt = new DateTimeOffset(2026, 7, 21, 15, 50, 0, TimeSpan.Zero),
            ClientEventId = Guid.NewGuid(),
        });

        context.RoutePlans.Add(new RoutePlan
        {
            AccountId = AccountId,
            TripId = TripId,
            Status = RoutePlanStatuses.Ready,
            ComputedAt = new DateTimeOffset(2026, 7, 21, 11, 0, 0, TimeSpan.Zero),
            Geom = new LineString([new Coordinate(-74.05, 4.65), new Coordinate(-74.0, 4.7)]) { SRID = 4326 },
            TollVehicleClass = "VII",
            EstimatedTollAmount = 187500.00m,
            TollCurrency = "COP",
            TollStatus = TollStatuses.Computed,
        });

        context.Transporters.Add(new Transporter
        {
            TransporterId = TransporterId,
            AccountId = AccountId,
            TransporterTypeId = 3,
            Name = "Tractomula 12 - WGY482",
        });

        // Full display name including family names and, in the real table, contact columns that are
        // deliberately NOT mapped into this DbContext at all.
        context.Drivers.Add(new Driver
        {
            DriverId = DriverId,
            AccountId = AccountId,
            Name = "Carlos Ramirez Gomez",
        });

        context.TripShares.Add(share);

        await context.SaveChangesAsync(CancellationToken.None);
        return context;
    }

    private static Point NewPoint(double latitude, double longitude)
        => new(longitude, latitude) { SRID = 4326 };

    private static TripShare Share(
        bool driverName = false,
        bool vehicle = false,
        bool livePosition = false,
        bool stopDetail = false,
        bool podSummary = false,
        bool route = false)
        => new()
        {
            AccountId = AccountId,
            TripId = TripId,
            PublicLinkGrantId = GrantId,
            IncludeDriverName = driverName,
            IncludeVehicle = vehicle,
            IncludeLivePosition = livePosition,
            IncludeStopDetail = stopDetail,
            IncludePodSummary = podSummary,
            IncludeRoute = route,
            CreatedByPrincipalId = "dispatcher",
            ExpiresAt = new DateTimeOffset(2026, 8, 21, 0, 0, 0, TimeSpan.Zero),
        };

    /// <summary>
    /// The floor: with every flag off, a link holder learns the trip exists, its code, its status
    /// and its schedule — and nothing else. Every gated field must be absent, not merely empty.
    /// </summary>
    [Test]
    public async Task AllFlagsOff_ExposesOnlyTheUngatedFields()
    {
        using var context = await SeedAsync(Share());

        var snapshot = await new TripShareReader(context)
            .GetPublicSnapshotAsync(GrantId, AccountId, CancellationToken.None);

        Assert.That(snapshot, Is.Not.Null);
        var trip = snapshot!.Value;

        Assert.Multiple(() =>
        {
            // Ungated by §7.8: code, status, planned/actual start and end.
            Assert.That(trip.Code, Is.EqualTo("TRIP-0001"));
            Assert.That(trip.Status, Is.EqualTo(TripStatuses.InProgress));
            Assert.That(trip.PlannedStartAt, Is.EqualTo(new DateTimeOffset(2026, 7, 21, 12, 0, 0, TimeSpan.Zero)));
            Assert.That(trip.ActualStartAt, Is.Not.Null);

            // Gated - every one of these must be absent.
            Assert.That(trip.Stops, Is.Empty, "IncludeStopDetail is off");
            Assert.That(trip.VehicleLabel, Is.Null, "IncludeVehicle is off");
            Assert.That(trip.DriverGivenName, Is.Null, "IncludeDriverName is off");
            Assert.That(trip.LastLatitude, Is.Null, "IncludeLivePosition is off");
            Assert.That(trip.LastLongitude, Is.Null, "IncludeLivePosition is off");
            Assert.That(trip.LastPositionAt, Is.Null, "IncludeLivePosition is off");
            Assert.That(trip.PlannedRoute, Is.Null, "IncludeRoute is off - a share with every box unticked must not hand out the planned route");
        });
    }

    /// <summary>
    /// The disclosure boundary is pinned STRUCTURALLY, not field by field.
    /// <para>
    /// Spec 11 §7.8 is an exhaustive allow-list, so the risk is a field being ADDED — and no
    /// value-based assertion can catch a member that did not exist when it was written.
    /// <c>CustomerName</c> was exactly that: it sat on this type projected to a hardcoded null, so
    /// every runtime assertion passed while the slot stayed one edit away from disclosing which
    /// customer a multi-drop trip belongs to, to every holder of every link on it.
    /// </para>
    /// </summary>
    [Test]
    public void PublicTripVm_ExposesExactlyTheFieldsSpecSevenPointEightAllows()
    {
        string[] allowed =
        [
            nameof(PublicTripVm.TripId), nameof(PublicTripVm.Code), nameof(PublicTripVm.Status),
            nameof(PublicTripVm.PlannedStartAt), nameof(PublicTripVm.PlannedEndAt),
            nameof(PublicTripVm.ActualStartAt), nameof(PublicTripVm.ActualEndAt),
            nameof(PublicTripVm.Stops), nameof(PublicTripVm.VehicleLabel),
            nameof(PublicTripVm.DriverGivenName), nameof(PublicTripVm.LastLatitude),
            nameof(PublicTripVm.LastLongitude), nameof(PublicTripVm.LastPositionAt),
            nameof(PublicTripVm.PlannedRoute),
        ];

        string[] stopAllowed =
        [
            nameof(PublicTripStopVm.Sequence), nameof(PublicTripStopVm.Name), nameof(PublicTripStopVm.City),
            nameof(PublicTripStopVm.Status), nameof(PublicTripStopVm.PlannedArrivalFrom),
            nameof(PublicTripStopVm.PlannedArrivalTo), nameof(PublicTripStopVm.ActualArrivalAt),
            nameof(PublicTripStopVm.EtaAt), nameof(PublicTripStopVm.HasProofOfDelivery),
        ];

        Assert.Multiple(() =>
        {
            Assert.That(
                typeof(PublicTripVm).GetProperties().Select(p => p.Name).Where(n => n != "EqualityContract"),
                Is.EquivalentTo(allowed),
                "A field was added to or removed from the public snapshot. §7.8 is an exhaustive "
                + "allow-list: extend it there first, and gate anything new behind a TripShare flag.");

            Assert.That(
                typeof(PublicTripStopVm).GetProperties().Select(p => p.Name).Where(n => n != "EqualityContract"),
                Is.EquivalentTo(stopAllowed),
                "A field was added to the public STOP snapshot. Address must never appear here — "
                + "City is the only locality §7.8 permits.");
        });
    }

    /// <summary>
    /// The ceiling: with every flag on, each gated field appears — and appears in its DISCLOSURE
    /// form, not its internal form. City, not street address. Given name, not full name.
    /// </summary>
    [Test]
    public async Task AllFlagsOn_ExposesEachGatedFieldInItsDisclosureForm()
    {
        using var context = await SeedAsync(Share(true, true, true, true, true, true));

        var snapshot = await new TripShareReader(context)
            .GetPublicSnapshotAsync(GrantId, AccountId, CancellationToken.None);

        Assert.That(snapshot, Is.Not.Null);
        var trip = snapshot!.Value;

        Assert.Multiple(() =>
        {
            Assert.That(trip.VehicleLabel, Is.EqualTo("Tractomula 12 - WGY482"));

            // Given name ONLY: the first whitespace-delimited token. "Carlos", never
            // "Carlos Ramirez Gomez" - a first-and-last-name identification of a specific employee
            // is one public-records search away from their address.
            Assert.That(trip.DriverGivenName, Is.EqualTo("Carlos"));

            Assert.That(trip.LastLatitude, Is.EqualTo(4.71));
            Assert.That(trip.LastLongitude, Is.EqualTo(-74.07));
            Assert.That(trip.LastPositionAt, Is.Not.Null);
            Assert.That(trip.PlannedRoute, Is.Not.Null);
            Assert.That(trip.PlannedRoute!.Value.Coordinates, Has.Count.EqualTo(2));

            Assert.That(trip.Stops, Has.Count.EqualTo(1));
            var stop = trip.Stops.First();
            Assert.That(stop.Sequence, Is.EqualTo(1));
            Assert.That(stop.Name, Is.EqualTo("Bodega Norte"));

            // The coarse locality, NOT TripStop.Address. Conflating the two is the leak this
            // assertion exists to prevent.
            Assert.That(stop.City, Is.EqualTo("Bogota"));
            Assert.That(stop.Status, Is.EqualTo(TripStopStatuses.Arrived));
            Assert.That(stop.EtaAt, Is.Not.Null);
            Assert.That(stop.ActualArrivalAt, Is.Not.Null);

            // Existence boolean only - POD content and documents are never public.
            Assert.That(stop.HasProofOfDelivery, Is.True);
        });
    }

    /// <summary>
    /// <c>IncludeLivePosition</c> is necessary but not sufficient: a completed trip's last known
    /// position is where the vehicle finished, which is a customer's premises (acceptance 23).
    /// </summary>
    [Test]
    public async Task LivePosition_IsSuppressedWhenTheTripIsNotInProgress()
    {
        using var context = await SeedAsync(
            Share(livePosition: true, stopDetail: true), TripStatuses.Completed);

        var snapshot = await new TripShareReader(context)
            .GetPublicSnapshotAsync(GrantId, AccountId, CancellationToken.None);

        Assert.That(snapshot, Is.Not.Null);
        Assert.Multiple(() =>
        {
            Assert.That(snapshot!.Value.LastLatitude, Is.Null);
            Assert.That(snapshot!.Value.LastLongitude, Is.Null);
            Assert.That(snapshot!.Value.LastPositionAt, Is.Null);
        });
    }

    /// <summary>
    /// The sweep: across ALL 64 flag combinations, no forbidden value may appear anywhere in the
    /// serialized snapshot. Serializing rather than checking known properties is deliberate — a
    /// field added to <c>PublicTripVm</c> later is covered by this test the day it is added.
    /// </summary>
    [Test]
    public async Task NoFlagCombination_EverDisclosesTollCostNotesOrContactData()
    {
        for (var mask = 0; mask < 64; mask++)
        {
            var share = Share(
                driverName: (mask & 1) != 0,
                vehicle: (mask & 2) != 0,
                livePosition: (mask & 4) != 0,
                stopDetail: (mask & 8) != 0,
                podSummary: (mask & 16) != 0,
                route: (mask & 32) != 0);

            using var context = await SeedAsync(share);

            var snapshot = await new TripShareReader(context)
                .GetPublicSnapshotAsync(GrantId, AccountId, CancellationToken.None);

            Assert.That(snapshot, Is.Not.Null);
            var payload = JsonSerializer.Serialize(snapshot!.Value);

            foreach (var forbidden in ForbiddenValues)
            {
                Assert.That(
                    payload,
                    Does.Not.Contain(forbidden),
                    $"flag mask {mask} disclosed '{forbidden}' in the public snapshot");
            }
        }
    }

    /// <summary>
    /// The account is not decoration on the lookup: a grant id resolved against the wrong account
    /// returns nothing rather than another tenant's trip (acceptance 1).
    /// </summary>
    [Test]
    public async Task Snapshot_IsNullForAnotherAccountsGrantId()
    {
        using var context = await SeedAsync(Share(true, true, true, true, true, true));

        var snapshot = await new TripShareReader(context)
            .GetPublicSnapshotAsync(GrantId, OtherAccountId, CancellationToken.None);

        Assert.That(snapshot, Is.Null);
    }

    /// <summary>
    /// A revoked share stops projecting at the store, independently of Manager's grant state — the
    /// two checks are belt and braces because either alone leaving a link live is a disclosure
    /// incident (acceptance 24).
    /// </summary>
    [Test]
    public async Task Snapshot_IsNullOnceTheShareIsRevoked()
    {
        var share = Share(true, true, true, true, true, true);
        share.RevokedAt = new DateTimeOffset(2026, 7, 21, 13, 0, 0, TimeSpan.Zero);

        using var context = await SeedAsync(share);

        var snapshot = await new TripShareReader(context)
            .GetPublicSnapshotAsync(GrantId, AccountId, CancellationToken.None);

        Assert.That(snapshot, Is.Null);
    }
}
