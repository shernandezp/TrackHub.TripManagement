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

using System.Text.Json;
using TrackHub.TripManagement.Infrastructure.TripDB.Events;
using TrackHub.TripManagement.Infrastructure.TripDB.Readers;

namespace TrackHub.TripManagement.Infrastructure.TripDB.Writers;

/// <summary>
/// Route-plan persistence. A provider failure is stored as a <c>Failed</c> plan with an error
/// code - never surfaced as an exception to the trip command's caller, and never a reason the trip
/// cannot proceed (spec 11 section 7.3, acceptance 18).
/// </summary>
public sealed class RoutePlanWriter(IApplicationDbContext context) : IRoutePlanWriter
{
    public async Task<RoutePlanVm> SaveReadyPlanAsync(
        Guid tripId,
        Guid accountId,
        string provider,
        IReadOnlyCollection<CoordinateVm> geometry,
        int corridorMeters,
        double plannedDistanceMeters,
        int plannedDurationSeconds,
        string? waypointsJson,
        string? legsJson,
        TollEstimateVm tollEstimate,
        CancellationToken cancellationToken)
    {
        var trip = await FindTripAsync(tripId, accountId, cancellationToken);
        var line = TripGeometryFactory.Line(geometry);
        var corridor = TripGeometryFactory.Corridor(line, corridorMeters);

        // A Ready plan MUST carry both geometries (spec 11 §6.1: null only on a Failed plan). Both
        // factories return null for a degenerate provider response — a single-vertex line, or a
        // buffer that does not come back a Polygon — and storing that as Ready is far worse than
        // failing: HasReadyRoutePlan flips on, every position then tests against a null corridor,
        // and IsInsideCorridorAsync cannot distinguish "no corridor" from "off route". The vehicle
        // drives the route perfectly and gets a TripRouteDeviation on the third fix, with no
        // re-entry able to clear it. Degrade to Failed so the trip stays usable and the reason is
        // visible (acceptance 18).
        if (line is null || corridor is null)
        {
            return await SaveFailedPlanAsync(
                tripId, accountId, provider, corridorMeters,
                TripErrorCodes.RoutingInvalidGeometry,
                $"The routing provider returned {geometry.Count} coordinate(s), which cannot form a route line and corridor.",
                cancellationToken);
        }

        var plan = new RoutePlan
        {
            AccountId = accountId,
            TripId = tripId,
            Provider = provider,
            Geom = line,
            CorridorGeom = corridor,
            CorridorMeters = corridorMeters,
            PlannedDistanceMeters = plannedDistanceMeters,
            PlannedDurationSeconds = plannedDurationSeconds,
            WaypointsJson = waypointsJson,
            LegsJson = legsJson,
            ComputedAt = DateTimeOffset.UtcNow,
            Status = RoutePlanStatuses.Ready,
            TollVehicleClass = tollEstimate.TollVehicleClass,
            EstimatedTollAmount = tollEstimate.EstimatedTollAmount,
            TollCurrency = tollEstimate.Currency,
            TollStatus = tollEstimate.TollStatus,
            TollStationsJson = tollEstimate.Stations.Count == 0
                ? null
                : JsonSerializer.Serialize(tollEstimate.Stations, TripMapper.Json),
        };

        plan.AddDomainEvent(new TripDomainEvent(TripEventTypes.TripRoutePlanned, accountId, tripId, plan.RoutePlanId));

        await context.RoutePlans.AddAsync(plan, cancellationToken);

        trip.RoutePlanId = plan.RoutePlanId;

        await context.SaveChangesAsync(cancellationToken);

        return TripMapper.ToVm(plan);
    }

    public async Task<RoutePlanVm> SaveFailedPlanAsync(
        Guid tripId,
        Guid accountId,
        string provider,
        int corridorMeters,
        string errorCode,
        string errorMessage,
        CancellationToken cancellationToken)
    {
        // Deliberately does NOT repoint trip.RoutePlanId: a failed attempt must not blank out the
        // last plan that actually worked.
        await FindTripAsync(tripId, accountId, cancellationToken);

        var plan = new RoutePlan
        {
            AccountId = accountId,
            TripId = tripId,
            Provider = provider,
            CorridorMeters = corridorMeters,
            ComputedAt = DateTimeOffset.UtcNow,
            Status = RoutePlanStatuses.Failed,
            ErrorCode = errorCode,
            ErrorMessage = errorMessage,
            TollStatus = TollStatuses.NotComputed,
        };

        plan.AddDomainEvent(new TripDomainEvent(TripEventTypes.TripRoutePlanFailed, accountId, tripId, plan.RoutePlanId));

        await context.RoutePlans.AddAsync(plan, cancellationToken);
        await context.SaveChangesAsync(cancellationToken);

        return TripMapper.ToVm(plan);
    }

    private async Task<Trip> FindTripAsync(Guid tripId, Guid accountId, CancellationToken cancellationToken)
        => await context.Trips.AsTracking().FirstOrDefaultAsync(t => t.TripId == tripId && t.AccountId == accountId, cancellationToken)
            ?? throw new NotFoundException($"{tripId}", nameof(Trip));
}
