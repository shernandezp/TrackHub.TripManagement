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

using Common.Domain.Constants;
using Microsoft.EntityFrameworkCore.Metadata.Builders;

namespace TrackHub.TripManagement.Infrastructure.TripDB.Configurations;

public sealed class TripStopConfiguration : IEntityTypeConfiguration<TripStop>
{
    public void Configure(EntityTypeBuilder<TripStop> builder)
    {
        builder.ToTable(name: TableMetadata.TripStop, schema: SchemaMetadata.Trip);
        builder.HasKey(x => x.TripStopId);

        builder.Property(x => x.TripStopId).HasColumnName("id");
        builder.Property(x => x.AccountId).HasColumnName("accountid");
        builder.Property(x => x.TripId).HasColumnName("tripid");
        builder.Property(x => x.Sequence).HasColumnName("sequence");
        builder.Property(x => x.Name).HasColumnName("name").HasMaxLength(ColumnMetadata.DefaultNameLength).IsRequired();
        builder.Property(x => x.Address).HasColumnName("address").HasMaxLength(ColumnMetadata.DefaultDescriptionLength);

        // Deliberately a separate, shorter column from `address`: this is the only locality label
        // the public snapshot may expose (§7.8), so it must not be able to hold a street address.
        builder.Property(x => x.City).HasColumnName("city").HasMaxLength(ColumnMetadata.DefaultNameLength);
        builder.Property(x => x.Point).HasColumnName("point").HasColumnType("geometry (Point, 4326)").IsRequired();
        builder.Property(x => x.GeofenceId).HasColumnName("geofenceid");
        builder.Property(x => x.ArrivalGeom).HasColumnName("arrivalgeom").HasColumnType("geometry (Polygon, 4326)");
        builder.Property(x => x.ArrivalRadiusMeters).HasColumnName("arrivalradiusmeters")
            .HasDefaultValue(TripGeometryDefaults.ArrivalRadiusMeters);
        builder.Property(x => x.PlannedArrivalFrom).HasColumnName("plannedarrivalfrom");
        builder.Property(x => x.PlannedArrivalTo).HasColumnName("plannedarrivalto");
        builder.Property(x => x.Activity).HasColumnName("activity").HasMaxLength(10).IsRequired()
            .HasDefaultValue(TripStopActivities.Unload);
        builder.Property(x => x.Status).HasColumnName("status").HasMaxLength(40).IsRequired();
        builder.Property(x => x.ActualArrivalAt).HasColumnName("actualarrivalat");
        builder.Property(x => x.ActualDepartureAt).HasColumnName("actualdepartureat");
        builder.Property(x => x.EtaAt).HasColumnName("etaat");
        builder.Property(x => x.EtaSource).HasColumnName("etasource").HasMaxLength(40).IsRequired();
        builder.Property(x => x.DelayAlertedAt).HasColumnName("delayalertedat");
        builder.Property(x => x.OutsideSinceAt).HasColumnName("outsidesinceat");
        builder.Property(x => x.RequiresPod).HasColumnName("requirespod").HasDefaultValue(false);
        builder.Property(x => x.Priority).HasColumnName("priority");
        builder.Property(x => x.Observations).HasColumnName("observations").HasMaxLength(1000);

        builder.HasIndex(x => new { x.TripId, x.Sequence })
            .HasDatabaseName("ux_trip_stops_tripid_sequence")
            .IsUnique();
        builder.HasIndex(x => new { x.AccountId, x.TripId })
            .HasDatabaseName("ix_trip_stops_accountid_tripid");

        // Arrival detection is ST_Contains(arrivalgeom, point) over the InProgress working set.
        builder.HasIndex(x => x.ArrivalGeom)
            .HasDatabaseName("ix_trip_stops_arrivalgeom_gist")
            .HasMethod("gist");

        builder.HasOne(x => x.Trip)
            .WithMany()
            .HasForeignKey(x => x.TripId)
            .OnDelete(DeleteBehavior.Cascade);
    }
}
