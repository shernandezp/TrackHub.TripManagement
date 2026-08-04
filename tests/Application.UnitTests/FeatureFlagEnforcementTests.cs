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
using Common.Application.Services;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.Caching.Memory;
using Microsoft.Extensions.Configuration;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.DependencyInjection.Extensions;
using TrackHub.TripManagement.Infrastructure.TripDB;
using TrackHub.TripManagement.Infrastructure.TripDB.Entities;
using TrackHub.TripManagement.Infrastructure.TripDB.Services;

namespace TrackHub.TripManagement.Application.UnitTests;

/// <summary>
/// Spec 11 §17.10, the acceptance criterion that asks for this file by name: "TripManagement's own
/// <c>IFeatureFlagService</c> is registered <b>and verified — a test asserts a disabled account is
/// actually rejected</b>, guarding against Common's fail-open default."
/// <para>
/// The hazard is quiet. Common registers <see cref="AlwaysEnabledFeatureFlagService"/> through
/// <c>TryAddScoped</c>, so a service that loses its own registration does not fail, throw, or log
/// anything — every <c>[RequireFeature(FeatureKeys.TripManagement)]</c> in the module simply starts
/// passing, and every trip surface becomes reachable by an account that never bought the feature.
/// Nothing else in the test suite would notice, which is exactly why this test exists.
/// </para>
/// </summary>
[TestFixture]
public class FeatureFlagEnforcementTests
{
    private static readonly Guid AccountId = TestFactory.AccountId;

    /// <summary>
    /// The registration guard. Asserts the OVERRIDE wins, in both composition orders — Common's
    /// <c>TryAddScoped</c> is a no-op when it runs second, and is out-ranked by the later plain
    /// <c>AddScoped</c> when it runs first. If this module's registration is ever dropped or
    /// downgraded to <c>TryAddScoped</c>, this fails.
    /// </summary>
    [TestCase(true, TestName = "CommonsFailOpenDefaultRegisteredFirst_IsOverridden")]
    [TestCase(false, TestName = "CommonsFailOpenDefaultRegisteredLast_DoesNotTakeOver")]
    public void TripManagementsFeatureFlagService_IsTheOneResolved(bool commonFirst)
    {
        var services = new ServiceCollection();

        if (commonFirst)
        {
            services.TryAddScoped<IFeatureFlagService, AlwaysEnabledFeatureFlagService>();
        }

        services.AddApplicationDbContext(Configuration());

        if (!commonFirst)
        {
            services.TryAddScoped<IFeatureFlagService, AlwaysEnabledFeatureFlagService>();
        }

        // Last registration wins at resolution time, so that is what must be asserted - not merely
        // that a descriptor for this module's type exists somewhere in the collection.
        var effective = services.Last(d => d.ServiceType == typeof(IFeatureFlagService));

        Assert.That(effective.ImplementationType, Is.EqualTo(typeof(FeatureFlagService)));
        Assert.That(effective.ImplementationType, Is.Not.EqualTo(typeof(AlwaysEnabledFeatureFlagService)));
    }

    /// <summary>
    /// The behavioural half: an account with no <c>trip-management</c> row is rejected. A missing
    /// row means DISABLED, never "unknown, so allow".
    /// </summary>
    [Test]
    public async Task AnAccountWithNoFeatureRow_IsRejected()
    {
        await using var context = InMemoryContext();
        var service = new FeatureFlagService(context, new MemoryCache(new MemoryCacheOptions()));

        var enabled = await service.IsEnabledAsync(AccountId, FeatureKeys.TripManagement, CancellationToken.None);

        Assert.That(enabled, Is.False);
    }

    /// <summary>An explicitly disabled row is rejected too — presence of a row is not permission.</summary>
    [Test]
    public async Task AnAccountWithTheFeatureTurnedOff_IsRejected()
    {
        await using var context = InMemoryContext();
        context.AccountFeatures.Add(Feature(enabled: false));
        await context.SaveChangesAsync(CancellationToken.None);

        var service = new FeatureFlagService(context, new MemoryCache(new MemoryCacheOptions()));

        var enabled = await service.IsEnabledAsync(AccountId, FeatureKeys.TripManagement, CancellationToken.None);

        Assert.That(enabled, Is.False);
    }

    /// <summary>An expired effectivity window is rejected — a lapsed subscription is not a live one.</summary>
    [Test]
    public async Task AnAccountWhoseFeatureWindowHasClosed_IsRejected()
    {
        await using var context = InMemoryContext();
        context.AccountFeatures.Add(Feature(
            enabled: true,
            effectiveTo: DateTimeOffset.UtcNow.AddDays(-1)));
        await context.SaveChangesAsync(CancellationToken.None);

        var service = new FeatureFlagService(context, new MemoryCache(new MemoryCacheOptions()));

        var enabled = await service.IsEnabledAsync(AccountId, FeatureKeys.TripManagement, CancellationToken.None);

        Assert.That(enabled, Is.False);
    }

    /// <summary>The positive control: an enabled account is allowed, so the rejections above mean something.</summary>
    [Test]
    public async Task AnAccountWithTheFeatureEnabled_IsAllowed()
    {
        await using var context = InMemoryContext();
        context.AccountFeatures.Add(Feature(enabled: true));
        await context.SaveChangesAsync(CancellationToken.None);

        var service = new FeatureFlagService(context, new MemoryCache(new MemoryCacheOptions()));

        var enabled = await service.IsEnabledAsync(AccountId, FeatureKeys.TripManagement, CancellationToken.None);

        Assert.That(enabled, Is.True);
    }

    /// <summary>
    /// The contrast that makes the whole fixture worth having: on the exact input the tests above
    /// reject, Common's default says yes. That is the failure mode being guarded against.
    /// </summary>
    [Test]
    public async Task CommonsDefault_WouldHaveAllowedTheDisabledAccount()
    {
        var enabled = await new AlwaysEnabledFeatureFlagService()
            .IsEnabledAsync(AccountId, FeatureKeys.TripManagement, CancellationToken.None);

        Assert.That(enabled, Is.True, "Common's default is fail-open by design; this module must override it.");
    }

    private static AccountFeature Feature(bool enabled, DateTimeOffset? effectiveTo = null)
        => new()
        {
            AccountFeatureId = Guid.NewGuid(),
            AccountId = AccountId,
            FeatureKey = FeatureKeys.TripManagement,
            Enabled = enabled,
            EffectiveFrom = DateTimeOffset.UtcNow.AddDays(-30),
            EffectiveTo = effectiveTo,
        };

    private static ApplicationDbContext InMemoryContext()
        => new(new DbContextOptionsBuilder<ApplicationDbContext>()
            .UseInMemoryDatabase($"trip-feature-flags-{Guid.NewGuid()}")
            .UseQueryTrackingBehavior(QueryTrackingBehavior.NoTracking)
            .Options);

    private static IConfiguration Configuration()
        => new ConfigurationBuilder()
            .AddInMemoryCollection(new Dictionary<string, string?>
            {
                // Never opened: AddApplicationDbContext only needs a non-null string to configure
                // Npgsql, and this fixture asserts registration, not connectivity.
                ["ConnectionStrings:DefaultConnection"] = "Host=localhost;Database=trackhub;Username=none;Password=none",
            })
            .Build();
}
