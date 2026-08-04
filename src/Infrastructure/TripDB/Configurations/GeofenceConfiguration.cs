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

public sealed class GeofenceConfiguration : IEntityTypeConfiguration<Geofence>
{
    public void Configure(EntityTypeBuilder<Geofence> builder)
    {
        // Geofencing-owned table: read-only here, never part of this repo migrations. It is read
        // once per trip start to snapshot each stop arrival geometry (spec 11 section 18.4).
        builder.ToTable(name: TableMetadata.Geofence, schema: SchemaMetadata.Geofencing, t => t.ExcludeFromMigrations());
        builder.HasKey(x => x.GeofenceId);
        builder.Property(x => x.GeofenceId).HasColumnName("id");
        builder.Property(x => x.AccountId).HasColumnName("accountid");
        builder.Property(x => x.Name).HasColumnName("name");
        builder.Property(x => x.Geom).HasColumnName("geom").HasColumnType("geometry (Polygon, 4326)");
        builder.Property(x => x.CircleCenter).HasColumnName("circlecenter").HasColumnType("geometry (Point, 4326)");
        builder.Property(x => x.Active).HasColumnName("active");
    }
}
