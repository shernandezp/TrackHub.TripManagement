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

using NetTopologySuite.Geometries;

namespace TrackHub.TripManagement.Infrastructure.TripDB.Entities;

// Read-only projection of the Geofencing-owned geofencing.geofences table, mapped with
// ExcludeFromMigrations. The geometry is read exactly ONCE per trip - at ARMING, to snapshot the
// origin zone and every stop's ArrivalGeom (spec 11a section 6.2). A geofence edited afterwards
// therefore cannot move a watched trip's detection geometry.
//
// Name and CircleCenter serve a second, separate purpose: bulk planning resolves places BY NAME
// (spec 11a section 9.1), because a dispatcher building a week of trips in a spreadsheet types
// "Plant 3", not a uuid and never a coordinate.
public sealed class Geofence
{
    public Guid GeofenceId { get; set; }
    public Guid AccountId { get; set; }
    public string Name { get; set; } = string.Empty;
    public Polygon Geom { get; set; } = default!;

    /// <summary>Centre of a circle geofence; null for a drawn polygon, whose first vertex stands in.</summary>
    public Point? CircleCenter { get; set; }
    public bool Active { get; set; }
}
