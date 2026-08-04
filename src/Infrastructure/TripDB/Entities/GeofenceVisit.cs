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

namespace TrackHub.TripManagement.Infrastructure.TripDB.Entities;

/// <summary>
/// Read-only projection of the Geofencing-owned <c>geofencing.geofenceevents</c> table (the SVD-05
/// shared-database pattern this module already uses for <see cref="Geofence"/>), mapped with
/// <c>ExcludeFromMigrations</c>.
/// <para>
/// One row is one VISIT: an entry instant and, once the vehicle leaves, a departure. It is read for
/// exactly one purpose — replaying what a late-created trip's vehicle already did, so a trip created
/// after its truck left carries measurements rather than a dispatcher's estimate (spec 11a §5.4).
/// Nothing here is ever written.
/// </para>
/// </summary>
public sealed class GeofenceVisit
{
    public Guid GeofenceVisitId { get; set; }
    public Guid TransporterId { get; set; }
    public Guid GeofenceId { get; set; }
    public Guid AccountId { get; set; }

    /// <summary>Entry instant — the measured arrival at the zone.</summary>
    public DateTimeOffset EnteredAt { get; set; }

    /// <summary>Exit instant, null while the vehicle is still inside.</summary>
    public DateTimeOffset? DepartedAt { get; set; }
}
