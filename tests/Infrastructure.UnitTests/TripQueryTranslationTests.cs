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
using System.Net.Sockets;
using Microsoft.EntityFrameworkCore;
using Npgsql;
using TrackHub.TripManagement.Domain.Models;
using TrackHub.TripManagement.Infrastructure.TripDB;
using TrackHub.TripManagement.Infrastructure.TripDB.Readers;
using TrackHub.TripManagement.Infrastructure.TripDB.Writers;

namespace Infrastructure.UnitTests;

/// <summary>
/// Guards against LINQ the real PostgreSQL provider cannot translate (spec 11 §16).
/// <para>
/// The Application unit tests run on EF InMemory, which evaluates ANY expression client-side and
/// therefore can never fail on an untranslatable query. That blind spot shipped a real bug in spec
/// 09: the workforce readers ordered the PROJECTED record struct
/// (<c>Project(query).OrderBy(x =&gt; x.StartsAt)</c>), which Npgsql rejects with "could not be
/// translated" — green unit tests, "Unexpected Execution Error" against a real deployment. This
/// module's readers are paged, projected and spatial, so they are exposed to exactly that trap.
/// </para>
/// <para>
/// These tests use the REAL Npgsql provider pointed at an unreachable host. Translation happens
/// BEFORE any connection is attempted, so an untranslatable query fails with
/// <see cref="InvalidOperationException"/> ("could not be translated") while a correct one gets as
/// far as a connection error. Asserting on WHICH failure occurs verifies translation with no
/// database required.
/// </para>
/// </summary>
[TestFixture]
public class TripQueryTranslationTests
{
    // Port 1 is never listening; the short timeouts keep the connection failure fast.
    private const string UnreachableConnection =
        "Host=127.0.0.1;Port=1;Database=none;Username=none;Password=none;Timeout=1;Command Timeout=1";

    private static readonly Guid AccountId = Guid.NewGuid();
    private static readonly Guid UserId = Guid.NewGuid();
    private static readonly Guid TripId = Guid.NewGuid();

    private static ApplicationDbContext NewNpgsqlContext()
        => new(new DbContextOptionsBuilder<ApplicationDbContext>()
            .UseNpgsql(UnreachableConnection, o => o.UseNetTopologySuite())
            .UseQueryTrackingBehavior(QueryTrackingBehavior.NoTracking)
            .Options);

    private static async Task AssertTranslatesAsync(Func<Task> query)
    {
        try
        {
            await query();
        }
        catch (InvalidOperationException ex) when (
            ex.Message.Contains("could not be translated", StringComparison.OrdinalIgnoreCase))
        {
            Assert.Fail($"The query cannot be translated to SQL and would fail at runtime:{Environment.NewLine}{ex.Message}");
            return;
        }
        catch (Exception ex) when (IsConnectionFailure(ex))
        {
            // The ONLY outcome that proves translation succeeded: EF built the SQL, then the
            // provider tried to open a socket to a host that is not listening.
            return;
        }
        catch (Exception ex)
        {
            // A guard clause, an ArgumentException or an EF wording change used to land in a bare
            // `catch (Exception) { }` and score a free pass, so the test asserted nothing at all.
            Assert.Fail(
                $"The query failed before it reached the database, so nothing was translated: " +
                $"{ex.GetType().FullName}: {ex.Message}");
            return;
        }

        // Returning normally means the reader never opened a connection — an early return or a null
        // guard short-circuited it, and no SQL was ever generated.
        Assert.Fail("The query completed without attempting a connection, so no SQL was translated.");
    }

    /// <summary>
    /// A failure to reach 127.0.0.1:1. Npgsql wraps the socket error, and the shape of the wrapping
    /// differs by platform and version, so the whole inner chain is inspected.
    /// </summary>
    private static bool IsConnectionFailure(Exception exception)
    {
        for (var current = exception; current is not null; current = current.InnerException)
        {
            if (current is NpgsqlException or SocketException or TimeoutException)
                return true;
        }

        return false;
    }

    // The group-scoped overload (userId non-null) joins vw_visible_transporter, which is the path
    // most likely to break translation; the account-wide overload (userId null) takes a different
    // branch, so both are covered.
    [Test]
    public async Task GetTripsPage_GroupScoped_TranslatesToSql()
    {
        using var context = NewNpgsqlContext();
        var reader = new TripReader(context, new AccountFeatureReader(context));

        await AssertTranslatesAsync(() => reader.GetTripsPageAsync(
            AccountId, UserId, ["InProgress"], DateTimeOffset.UtcNow.AddDays(-7), DateTimeOffset.UtcNow,
            null, null, null, "search", 0, 50, CancellationToken.None));
    }

    [Test]
    public async Task GetTripsPage_AccountWide_TranslatesToSql()
    {
        using var context = NewNpgsqlContext();
        var reader = new TripReader(context, new AccountFeatureReader(context));

        await AssertTranslatesAsync(() => reader.GetTripsPageAsync(
            AccountId, null, null, DateTimeOffset.UtcNow.AddDays(-7), DateTimeOffset.UtcNow,
            null, null, null, null, 0, 50, CancellationToken.None));
    }

    [Test]
    public async Task GetTripDetail_TranslatesToSql()
    {
        using var context = NewNpgsqlContext();
        var reader = new TripReader(context, new AccountFeatureReader(context));

        await AssertTranslatesAsync(() => reader.GetTripDetailAsync(
            TripId, AccountId, UserId, CancellationToken.None));
    }

    [Test]
    public async Task GetActiveTrips_TranslatesToSql()
    {
        using var context = NewNpgsqlContext();
        var reader = new TripReader(context, new AccountFeatureReader(context));

        await AssertTranslatesAsync(() => reader.GetActiveTripsAsync(
            AccountId, UserId, CancellationToken.None));
    }

    [Test]
    public async Task GetTimeline_TranslatesToSql()
    {
        using var context = NewNpgsqlContext();
        var reader = new TripReader(context, new AccountFeatureReader(context));

        await AssertTranslatesAsync(() => reader.GetTimelineAsync(
            TripId, AccountId, UserId, 0, 50, CancellationToken.None));
    }

    // The four Reporting export feeds (spec 11 §13). These are drained at 500/page by another
    // service, so an untranslatable query here breaks report execution, not a screen.
    [Test]
    public async Task GetTripReportRows_TranslatesToSql()
    {
        using var context = NewNpgsqlContext();
        var reader = new TripReader(context, new AccountFeatureReader(context));

        await AssertTranslatesAsync(() => reader.GetTripReportRowsAsync(
            AccountId, UserId, DateTimeOffset.UtcNow.AddDays(-30), DateTimeOffset.UtcNow,
            null, null, 0, 500, CancellationToken.None));
    }

    [Test]
    public async Task GetTripStopReportRows_TranslatesToSql()
    {
        using var context = NewNpgsqlContext();
        var reader = new TripReader(context, new AccountFeatureReader(context));

        await AssertTranslatesAsync(() => reader.GetTripStopReportRowsAsync(
            AccountId, UserId, DateTimeOffset.UtcNow.AddDays(-30), DateTimeOffset.UtcNow,
            null, null, 0, 500, CancellationToken.None));
    }

    [Test]
    public async Task GetTripTollReportRows_TranslatesToSql()
    {
        using var context = NewNpgsqlContext();
        var reader = new TripReader(context, new AccountFeatureReader(context));

        await AssertTranslatesAsync(() => reader.GetTripTollReportRowsAsync(
            AccountId, UserId, DateTimeOffset.UtcNow.AddDays(-30), DateTimeOffset.UtcNow,
            null, null, 0, 500, CancellationToken.None));
    }

    [Test]
    public async Task GetTripPodReportRows_TranslatesToSql()
    {
        using var context = NewNpgsqlContext();
        var reader = new TripReader(context, new AccountFeatureReader(context));

        await AssertTranslatesAsync(() => reader.GetTripPodReportRowsAsync(
            AccountId, UserId, DateTimeOffset.UtcNow.AddDays(-30), DateTimeOffset.UtcNow,
            null, null, 0, 500, CancellationToken.None));
    }

    [Test]
    public async Task GetTollStationsPage_TranslatesToSql()
    {
        using var context = NewNpgsqlContext();
        var reader = new TollCatalogReader(context);

        await AssertTranslatesAsync(() => reader.GetStationsPageAsync(
            "search", "CO", true, 0, 500, CancellationToken.None));
    }

    [Test]
    public async Task GetTollStationDetail_TranslatesToSql()
    {
        using var context = NewNpgsqlContext();
        var reader = new TollCatalogReader(context);

        await AssertTranslatesAsync(() => reader.GetStationDetailAsync(
            Guid.NewGuid(), CancellationToken.None));
    }

    // The spatial path: ST_DWithin against the route line. Spatial predicates are the most
    // translation-fragile queries in the module.
    [Test]
    public async Task MatchStations_TranslatesToSql()
    {
        using var context = NewNpgsqlContext();
        var reader = new TollCatalogReader(context);

        IReadOnlyCollection<CoordinateVm> route =
        [
            new CoordinateVm(4.60971, -74.08175),
            new CoordinateVm(4.70000, -74.10000),
        ];

        await AssertTranslatesAsync(() => reader.MatchStationsAsync(
            route, 100d, "II", DateOnly.FromDateTime(DateTimeOffset.UtcNow.UtcDateTime),
            CancellationToken.None));
    }

    [Test]
    public async Task HasOverlappingTariff_TranslatesToSql()
    {
        using var context = NewNpgsqlContext();
        var reader = new TollCatalogReader(context);

        await AssertTranslatesAsync(() => reader.HasOverlappingTariffAsync(
            Guid.NewGuid(), "II", DateOnly.FromDateTime(DateTimeOffset.UtcNow.UtcDateTime),
            null, null, CancellationToken.None));
    }

    // Detection runs on every position batch from Router; an untranslatable query here would
    // silently break arrival/departure detection for every account.
    [Test]
    public async Task LoadWorkingSet_TranslatesToSql()
    {
        using var context = NewNpgsqlContext();
        var unit = new TripDetectionUnitOfWork(context, NullLogger<TripDetectionUnitOfWork>.Instance);

        await AssertTranslatesAsync(() => unit.LoadAsync(
            AccountId, [Guid.NewGuid()], DateTimeOffset.UtcNow.AddHours(1), CancellationToken.None));
    }

    /// <summary>
    /// The armable-trip half of the working set: the NOT EXISTS that expresses the per-vehicle queue
    /// (spec 11a §7). It is a DIFFERENT query shape from the InProgress half, and passing null for
    /// the window skips it entirely — so it needs its own guard with the window open.
    /// </summary>
    [Test]
    public async Task LoadWorkingSet_WithNoArmingWindow_TranslatesToSql()
    {
        using var context = NewNpgsqlContext();
        var unit = new TripDetectionUnitOfWork(context, NullLogger<TripDetectionUnitOfWork>.Instance);

        await AssertTranslatesAsync(() => unit.LoadAsync(
            AccountId, [Guid.NewGuid()], null, CancellationToken.None));
    }

    [Test]
    public async Task GetCompletionCandidates_TranslatesToSql()
    {
        using var context = NewNpgsqlContext();
        var reader = new TripDetectionReader(context);

        await AssertTranslatesAsync(() => reader.GetCompletionCandidatesAsync(AccountId, CancellationToken.None));
    }

    [Test]
    public async Task GetCompletionState_TranslatesToSql()
    {
        using var context = NewNpgsqlContext();
        var reader = new TripDetectionReader(context);

        await AssertTranslatesAsync(() => reader.GetCompletionStateAsync(AccountId, TripId, CancellationToken.None));
    }

    // Geofencing's visit history, read across the shared database (SVD-05) to rebuild a late-created
    // trip from measurements (spec 11a §5.4).
    [Test]
    public async Task GetGeofenceVisits_TranslatesToSql()
    {
        using var context = NewNpgsqlContext();
        var reader = new GeofenceVisitReader(context);

        await AssertTranslatesAsync(() => reader.GetVisitsAsync(
            AccountId, Guid.NewGuid(), [Guid.NewGuid()], DateTimeOffset.UtcNow.AddHours(-24), CancellationToken.None));
    }

    // The account's named places, resolved by a bulk upload (spec 11a §9.1). It reads a
    // Manager-owned table and a Geofencing-owned one in the same pass.
    [Test]
    public async Task GetPlaces_TranslatesToSql()
    {
        using var context = NewNpgsqlContext();
        var reader = new PlaceReader(context);

        await AssertTranslatesAsync(() => reader.GetPlacesAsync(AccountId, CancellationToken.None));
    }

    // Bulk planning resolves the transporter column by NAME, through the same group-visibility
    // EXISTS the trip queries use.
    [Test]
    public async Task GetTransporterNames_GroupScoped_TranslatesToSql()
    {
        using var context = NewNpgsqlContext();
        var reader = new TripReader(context, new AccountFeatureReader(context));

        await AssertTranslatesAsync(() => reader.GetTransporterNamesAsync(AccountId, UserId, CancellationToken.None));
    }

    [Test]
    public async Task GetDriverNames_TranslatesToSql()
    {
        using var context = NewNpgsqlContext();
        var reader = new TripReader(context, new AccountFeatureReader(context));

        await AssertTranslatesAsync(() => reader.GetDriverNamesAsync(AccountId, CancellationToken.None));
    }

    [Test]
    public async Task IsInsideCorridor_TranslatesToSql()
    {
        using var context = NewNpgsqlContext();
        var reader = new TripDetectionReader(context);

        await AssertTranslatesAsync(() => reader.IsInsideCorridorAsync(
            AccountId, Guid.NewGuid(), 4.60971, -74.08175, CancellationToken.None));
    }

    [Test]
    public async Task GetEtaCandidates_TranslatesToSql()
    {
        using var context = NewNpgsqlContext();
        var reader = new TripDetectionReader(context);

        await AssertTranslatesAsync(() => reader.GetEtaCandidatesAsync(
            AccountId, DateTimeOffset.UtcNow.AddMinutes(-15), CancellationToken.None));
    }

    // The same query now also returns trips whose position is stale or absent (the freshness filter
    // moved out of the WHERE clause and into a projected flag, so the fallback path is reachable).
    // A cutoff in the future makes every candidate stale, exercising that shape.
    [Test]
    public async Task GetEtaCandidates_WithEveryPositionStale_TranslatesToSql()
    {
        using var context = NewNpgsqlContext();
        var reader = new TripDetectionReader(context);

        await AssertTranslatesAsync(() => reader.GetEtaCandidatesAsync(
            AccountId, DateTimeOffset.UtcNow.AddYears(1), CancellationToken.None));
    }

    [Test]
    public async Task GetTripsDueToStart_TranslatesToSql()
    {
        using var context = NewNpgsqlContext();
        var reader = new TripDetectionReader(context);

        await AssertTranslatesAsync(() => reader.GetTripsDueToStartAsync(
            AccountId, DateTimeOffset.UtcNow.AddMinutes(-30), DateTimeOffset.UtcNow.AddMinutes(60), CancellationToken.None));
    }

    [Test]
    public async Task GetPublicSnapshot_TranslatesToSql()
    {
        using var context = NewNpgsqlContext();
        var reader = new TripShareReader(context);

        await AssertTranslatesAsync(() => reader.GetPublicSnapshotAsync(
            Guid.NewGuid(), AccountId, CancellationToken.None));
    }
}
