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

namespace TrackHub.TripManagement.Infrastructure.TripDB.Interfaces;

public interface IApplicationDbContext
{
    DbSet<Trip> Trips { get; set; }
    DbSet<TripStop> TripStops { get; set; }
    DbSet<Delivery> Deliveries { get; set; }
    DbSet<TripAssignment> TripAssignments { get; set; }
    DbSet<RoutePlan> RoutePlans { get; set; }
    DbSet<TripEvent> TripEvents { get; set; }
    DbSet<ProofOfDelivery> ProofsOfDelivery { get; set; }
    DbSet<TripDocument> TripDocuments { get; set; }
    DbSet<TripShare> TripShares { get; set; }
    DbSet<TransporterTollClass> TransporterTollClasses { get; set; }
    DbSet<TollVehicleClass> TollVehicleClasses { get; set; }
    DbSet<TollStation> TollStations { get; set; }
    DbSet<TollTariff> TollTariffs { get; set; }

    // Read-only, cross-service.
    DbSet<Geofence> Geofences { get; set; }
    DbSet<GeofenceVisit> GeofenceVisits { get; set; }
    DbSet<PointOfInterest> PointsOfInterest { get; set; }
    DbSet<Transporter> Transporters { get; set; }
    DbSet<Driver> Drivers { get; set; }
    DbSet<VwVisibleTransporter> VisibleTransporters { get; set; }
    DbSet<VwUser> Users { get; set; }
    DbSet<AccountFeature> AccountFeatures { get; set; }
    DbSet<Account> Accounts { get; set; }
    DbSet<AuditEvent> AuditEvents { get; set; }

    /// <summary>
    /// Exposed so a writer that catches a constraint violation can DETACH the failed entries.
    /// <para>
    /// The context is request-scoped, so an <c>Added</c> entry left behind after a rejected
    /// <c>SaveChangesAsync</c> is replayed by the next save on the same request — turning one
    /// rejected row into a cascade that kills every later write in the batch. Every catch of a
    /// unique/FK violation in this assembly must clear the tracker before continuing.
    /// </para>
    /// </summary>
    Microsoft.EntityFrameworkCore.ChangeTracking.ChangeTracker ChangeTracker { get; }

    Task<int> SaveChangesAsync(CancellationToken cancellationToken);
}
