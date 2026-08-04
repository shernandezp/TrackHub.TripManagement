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
/// Read-only projection of the Manager-owned <c>map.points_of_interest</c> table (SVD-05), mapped
/// with <c>ExcludeFromMigrations</c>.
/// <para>
/// Read for one purpose: resolving a place NAME typed into a bulk-upload spreadsheet, after the
/// account's geofences have been tried (spec 11a §9.1). A POI has no shape, so a trip that lands on
/// one is measured against a radius buffer instead.
/// </para>
/// </summary>
public sealed class PointOfInterest
{
    public Guid PointOfInterestId { get; set; }
    public Guid AccountId { get; set; }
    public string Name { get; set; } = string.Empty;
    public double Latitude { get; set; }
    public double Longitude { get; set; }
    public bool Active { get; set; }
}
