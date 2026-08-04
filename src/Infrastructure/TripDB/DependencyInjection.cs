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
using Microsoft.EntityFrameworkCore.Diagnostics;
using Microsoft.Extensions.Configuration;
using TrackHub.TripManagement.Domain.Interfaces;
using TrackHub.TripManagement.Infrastructure.TripDB;
using TrackHub.TripManagement.Infrastructure.TripDB.Interfaces;
using TrackHub.TripManagement.Infrastructure.TripDB.Readers;
using TrackHub.TripManagement.Infrastructure.TripDB.Services;
using TrackHub.TripManagement.Infrastructure.TripDB.Writers;
using Common.Application.Extensions;

namespace Microsoft.Extensions.DependencyInjection;

public static class DependencyInjection
{
    public static IServiceCollection AddApplicationDbContext(this IServiceCollection services, IConfiguration configuration)
    {
        var connectionString = configuration.GetConnectionString("DefaultConnection");

        Ardalis.GuardClauses.Guard.Against.Null(connectionString, message: "Connection string 'DefaultConnection' not found.");

        services.AddDbContext<ApplicationDbContext>((sp, options) =>
        {
            options.AddInterceptors(sp.GetServices<ISaveChangesInterceptor>());
            options.UseNpgsql(connectionString, o =>
            {
                o.UseQuerySplittingBehavior(QuerySplittingBehavior.SplitQuery);
                o.UseNetTopologySuite();
            });
            options.UseQueryTrackingBehavior(QueryTrackingBehavior.NoTracking);
        });

        services.AddTrackHubHeaderPropagation();

        services.AddScoped<IApplicationDbContext>(provider => provider.GetRequiredService<ApplicationDbContext>());

        // Trip aggregate.
        services.AddScoped<ITripReader, TripReader>();
        services.AddScoped<ITripWriter, TripWriter>();
        services.AddScoped<ITripStopWriter, TripStopWriter>();
        services.AddScoped<ITripEventWriter, TripEventWriter>();
        services.AddScoped<IDeliveryWriter, DeliveryWriter>();
        services.AddScoped<IProofOfDeliveryWriter, ProofOfDeliveryWriter>();
        services.AddScoped<ITripDetectionReader, TripDetectionReader>();

        // The detection working set as a unit of work: loaded once per batch, committed once per fix.
        services.AddScoped<ITripDetectionUnitOfWork, TripDetectionUnitOfWork>();

        // Geofencing's visit history, read-only (SVD-05): the evidence a late-created trip is
        // rebuilt from (spec 11a §5.4).
        services.AddScoped<IGeofenceVisitReader, GeofenceVisitReader>();

        // The account's named places (geofences, then POIs), for bulk planning by name (§9.1).
        services.AddScoped<IPlaceReader, PlaceReader>();

        // Route planning.
        services.AddScoped<IRoutePlanReader, RoutePlanReader>();
        services.AddScoped<IRoutePlanWriter, RoutePlanWriter>();

        // Public sharing.
        services.AddScoped<ITripShareReader, TripShareReader>();
        services.AddScoped<ITripShareWriter, TripShareWriter>();

        // Toll catalog (platform reference data) and its account-scoped mapping.
        services.AddScoped<ITollCatalogReader, TollCatalogReader>();
        services.AddScoped<ITollCatalogWriter, TollCatalogWriter>();
        services.AddScoped<ITransporterTollClassStore, TransporterTollClassStore>();

        // Local identity/feature reads.
        services.AddScoped<IUserReader, UserReader>();
        services.AddScoped<IAccountFeatureReader, AccountFeatureReader>();

        services.AddMemoryCache();

        // Cross-service account-status enforcement.
        services.AddScoped<Common.Application.Interfaces.IAccountOperationalStatusReader, AccountOperationalStatusReader>();
        services.AddScoped<Common.Application.Interfaces.IAccountOperationalStatusService, Common.Application.Services.CachedAccountOperationalStatusService>();

        // CRITICAL (spec 11 section 15, acceptance 10): Common registers a FAIL-OPEN
        // AlwaysEnabledFeatureFlagService with TryAddScoped. This is a plain AddScoped so the
        // DB-backed implementation is the one resolved - without it every [RequireFeature] in this
        // module silently passes for accounts that do not have trip-management.
        services.AddScoped<Common.Application.Interfaces.IFeatureFlagService, FeatureFlagService>();

        // Module discovery seam: registers any IServiceModule implementations shipped in
        // this assembly (none in this repository).
        services.AddDiscoveredModules(typeof(ApplicationDbContext).Assembly, configuration);

        return services;
    }
}
