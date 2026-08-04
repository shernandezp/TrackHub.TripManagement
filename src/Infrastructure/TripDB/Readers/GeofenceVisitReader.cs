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

/// <summary>Read side of Geofencing's visit history — see <see cref="IGeofenceVisitReader"/>.</summary>
public sealed class GeofenceVisitReader(IApplicationDbContext context) : IGeofenceVisitReader
{
    /// <summary>
    /// Defensive cap on one vehicle's lookback window. A tracker bouncing on a zone edge can produce
    /// a surprising number of visits in a day, and a backfill must not materialize them all.
    /// </summary>
    private const int MaxVisits = 500;

    public async Task<IReadOnlyCollection<GeofenceVisitVm>> GetVisitsAsync(
        Guid accountId,
        Guid transporterId,
        IReadOnlyCollection<Guid> geofenceIds,
        DateTimeOffset since,
        CancellationToken cancellationToken)
    {
        if (geofenceIds.Count == 0)
        {
            return [];
        }

        var ids = geofenceIds.ToList();

        // Ordering on entity columns, before the projection - see TripMapper for why.
        var visits = await context.GeofenceVisits
            .Where(v => v.AccountId == accountId
                && v.TransporterId == transporterId
                && ids.Contains(v.GeofenceId)
                && v.EnteredAt >= since)
            .OrderBy(v => v.EnteredAt)
            .Take(MaxVisits)
            .Select(v => new { v.GeofenceId, v.EnteredAt, v.DepartedAt })
            .ToListAsync(cancellationToken);

        return [.. visits.Select(v => new GeofenceVisitVm(v.GeofenceId, v.EnteredAt, v.DepartedAt))];
    }
}
