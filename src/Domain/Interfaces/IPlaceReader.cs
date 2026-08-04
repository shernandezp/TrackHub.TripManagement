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

namespace TrackHub.TripManagement.Domain.Interfaces;

/// <summary>
/// Resolves the account's named places for bulk planning (spec 11a §9.1).
/// <para>
/// A dispatcher planning a week in a spreadsheet types "Plant 3" — never a uuid, and pointedly never
/// a coordinate. Geofences are tried first because a geofence has a real SHAPE, and a trip measured
/// against a shape beats one measured against a 150 m circle; a POI is the fallback.
/// </para>
/// </summary>
public interface IPlaceReader
{
    /// <summary>
    /// Every active place in the account, geofences and POIs together, for one bulk resolution pass.
    /// <para>
    /// The whole catalog in one query rather than a lookup per row: a 500-row upload naming twenty
    /// distinct places would otherwise cost a thousand round trips to answer twenty questions.
    /// </para>
    /// </summary>
    Task<IReadOnlyCollection<PlaceVm>> GetPlacesAsync(Guid accountId, CancellationToken cancellationToken);
}

/// <summary>
/// A named place. <paramref name="GeofenceId"/> is set only for a geofence — that is what tells the
/// trip writer to snapshot the real polygon instead of buffering the point.
/// </summary>
public readonly record struct PlaceVm(
    string Name,
    Guid? GeofenceId,
    double Latitude,
    double Longitude);
