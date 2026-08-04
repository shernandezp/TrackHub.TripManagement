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

public sealed class GeofenceVisitConfiguration : IEntityTypeConfiguration<GeofenceVisit>
{
    public void Configure(EntityTypeBuilder<GeofenceVisit> builder)
    {
        // Geofencing-owned table: read-only here, never part of this repo's migrations. Read only by
        // the late-created-trip backfill (spec 11a §5.4).
        builder.ToTable(name: TableMetadata.GeofenceEvent, schema: SchemaMetadata.Geofencing, t => t.ExcludeFromMigrations());
        builder.HasKey(x => x.GeofenceVisitId);

        builder.Property(x => x.GeofenceVisitId).HasColumnName("id");
        builder.Property(x => x.TransporterId).HasColumnName("transporterid");
        builder.Property(x => x.GeofenceId).HasColumnName("geofenceid");
        builder.Property(x => x.AccountId).HasColumnName("accountid");
        builder.Property(x => x.EnteredAt).HasColumnName("datetime");
        builder.Property(x => x.DepartedAt).HasColumnName("departuretimestamp");
    }
}
