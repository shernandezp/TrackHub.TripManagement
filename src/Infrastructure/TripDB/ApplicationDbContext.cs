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
using Common.Infrastructure;

namespace TrackHub.TripManagement.Infrastructure.TripDB;

public partial class ApplicationDbContext(DbContextOptions<ApplicationDbContext> options) : DbContext(options), IApplicationDbContext
{
    public DbSet<Trip> Trips { get; set; }
    public DbSet<TripStop> TripStops { get; set; }
    public DbSet<Delivery> Deliveries { get; set; }
    public DbSet<TripAssignment> TripAssignments { get; set; }
    public DbSet<RoutePlan> RoutePlans { get; set; }
    public DbSet<TripEvent> TripEvents { get; set; }
    public DbSet<ProofOfDelivery> ProofsOfDelivery { get; set; }
    public DbSet<TripDocument> TripDocuments { get; set; }
    public DbSet<TripShare> TripShares { get; set; }
    public DbSet<TransporterTollClass> TransporterTollClasses { get; set; }
    public DbSet<TollVehicleClass> TollVehicleClasses { get; set; }
    public DbSet<TollStation> TollStations { get; set; }
    public DbSet<TollTariff> TollTariffs { get; set; }
    public DbSet<Geofence> Geofences { get; set; }
    public DbSet<GeofenceVisit> GeofenceVisits { get; set; }
    public DbSet<PointOfInterest> PointsOfInterest { get; set; }
    public DbSet<Transporter> Transporters { get; set; }
    public DbSet<Driver> Drivers { get; set; }
    public DbSet<VwVisibleTransporter> VisibleTransporters { get; set; }
    public DbSet<VwUser> Users { get; set; }
    public DbSet<AccountFeature> AccountFeatures { get; set; }
    public DbSet<Account> Accounts { get; set; }
    public DbSet<AuditEvent> AuditEvents { get; set; }

    protected override void OnModelCreating(ModelBuilder builder)
    {
        builder.ApplyConfigurationsFromAssembly(Assembly.GetExecutingAssembly());
        base.OnModelCreating(builder);
    }

    protected override void ConfigureConventions(ModelConfigurationBuilder configurationBuilder)
    {
        configurationBuilder.UseUtcTimestamps();
        base.ConfigureConventions(configurationBuilder);
    }
}
