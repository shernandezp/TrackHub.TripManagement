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

using System.Reflection;
using Common.Application;
using TrackHub.TripManagement.Application.Common;
using TrackHub.TripManagement.Application.PublicTrips.Services;
using TrackHub.TripManagement.Application.PublicTrips.Services.Interfaces;
using TrackHub.TripManagement.Application.Tolls.Services;
using TrackHub.TripManagement.Application.Tolls.Services.Interfaces;
using TrackHub.TripManagement.Application.TripEvents.Services;
using TrackHub.TripManagement.Application.TripEvents.Services.Interfaces;
using TrackHub.TripManagement.Application.Trips.Services;
using TrackHub.TripManagement.Application.Trips.Services.Interfaces;

namespace Microsoft.Extensions.DependencyInjection;

public static class DependencyInjection
{
    public static IServiceCollection AddApplicationServices(this IServiceCollection services)
    {
        var assembly = Assembly.GetExecutingAssembly();
        services.AddApplicationServices(assembly);

        // CachingBehavior resolves IDistributedCache for EVERY request type, whether or not the
        // request is cached. Omitting this does not disable caching — it breaks the whole pipeline
        // at the first request with a DI resolution failure (rules.md).
        services.AddDistributedMemoryCache();

        services.AddScoped<ITripDetectionService, TripDetectionService>();
        services.AddScoped<ITollEstimationService, TollEstimationService>();
        services.AddScoped<ITripEtaService, TripEtaService>();

        // Shared by the detection pipeline and the trip-eta-refresh sweep: one implementation of
        // "is this trip finished?", so the two callers can never disagree (spec 11a §5.2).
        services.AddScoped<ITripAutoCompletionService, TripAutoCompletionService>();
        services.AddScoped<ITripStartBackfillService, TripStartBackfillService>();

        // Resolved directly by the anonymous public-tracking endpoint, which bypasses the mediator.
        services.AddScoped<IPublicTripResolver, PublicTripResolver>();

        // Spec 12 owns service orders; until it ships there is nothing to ask, so the default
        // implementation accepts any reference. The PORT is what matters: CreateTrip/UpdateTrip
        // already call it on every write, so spec 12 enforces the reference by registering its own
        // implementation in the Infrastructure layer (which runs after this and therefore wins) and
        // reopens no handler. See IServiceOrderValidator for the full rationale.
        services.AddScoped<IServiceOrderValidator, PermissiveServiceOrderValidator>();

        return services;
    }
}
