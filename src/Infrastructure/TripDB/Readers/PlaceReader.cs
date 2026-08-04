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

namespace TrackHub.TripManagement.Infrastructure.TripDB.Readers;

/// <summary>See <see cref="IPlaceReader"/>. Geofences first, POIs second — shape beats point.</summary>
public sealed class PlaceReader(IApplicationDbContext context) : IPlaceReader
{
    public async Task<IReadOnlyCollection<PlaceVm>> GetPlacesAsync(Guid accountId, CancellationToken cancellationToken)
    {
        var geofences = await context.Geofences
            .Where(g => g.AccountId == accountId && g.Active)
            .Select(g => new { g.GeofenceId, g.Name, g.CircleCenter, g.Geom })
            .ToListAsync(cancellationToken);

        var pois = await context.PointsOfInterest
            .Where(p => p.AccountId == accountId && p.Active)
            .Select(p => new { p.Name, p.Latitude, p.Longitude })
            .ToListAsync(cancellationToken);

        // A circle carries its centre; a drawn polygon's first vertex is the best available
        // representative point — the same rule the portal's place pickers apply, so a name typed
        // into a spreadsheet and a name picked from a dropdown produce the identical trip.
        var places = geofences
            .Where(g => g.Geom is not null && g.Geom.Coordinates.Length > 0)
            .Select(g => new PlaceVm(
                g.Name,
                g.GeofenceId,
                g.CircleCenter?.Y ?? g.Geom.Coordinates[0].Y,
                g.CircleCenter?.X ?? g.Geom.Coordinates[0].X))
            .ToList();

        places.AddRange(pois.Select(p => new PlaceVm(p.Name, null, p.Latitude, p.Longitude)));

        return places;
    }
}
