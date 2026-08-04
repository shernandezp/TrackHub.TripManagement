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
using TrackHub.TripManagement.Infrastructure.TripDB;
using TrackHub.TripManagement.Infrastructure.TripDB.Writers;
using TrackHub.TripManagement.Domain.Constants;
using TrackHub.TripManagement.Infrastructure.TripDB.Entities;

namespace Infrastructure.UnitTests;

/// <summary>Minimal trip/stop rows for the writer fixtures.</summary>
internal static class WriterTestData
{
    internal static readonly Guid AccountId = Guid.Parse("11111111-1111-1111-1111-111111111111");

    internal static readonly Guid TransporterId = Guid.Parse("55555555-5555-5555-5555-555555555555");

    /// <summary>
    /// The detection unit of work, loaded the way detection loads it: one query for the working set,
    /// then mutations against the rows already in hand. Tests go through <c>LoadAsync</c> first
    /// because that is the only way production ever reaches those mutators — a fixture that skipped
    /// it would be exercising a state the pipeline cannot produce.
    /// </summary>
    internal static async Task<TripDetectionUnitOfWork> LoadedUnitAsync(
        ApplicationDbContext context, DateTimeOffset? armableUntil = null)
    {
        var unit = new TripDetectionUnitOfWork(
            context, Microsoft.Extensions.Logging.Abstractions.NullLogger<TripDetectionUnitOfWork>.Instance);

        await unit.LoadAsync(AccountId, [TransporterId], armableUntil, CancellationToken.None);
        return unit;
    }

    internal static Trip Trip(Guid tripId, string code)
        => new()
        {
            TripId = tripId,
            AccountId = AccountId,
            Code = code,
            Status = TripStatuses.InProgress,
            TransporterId = Guid.Parse("55555555-5555-5555-5555-555555555555"),
            OriginName = "Depot",
            OriginPoint = Point(4.65, -74.05),
            PlannedStartAt = new DateTimeOffset(2026, 7, 21, 12, 0, 0, TimeSpan.Zero),
        };

    internal static TripStop Stop(Guid tripStopId, Guid tripId, string status, int sequence = 1)
        => new()
        {
            TripStopId = tripStopId,
            AccountId = AccountId,
            TripId = tripId,
            Sequence = sequence,
            Name = "Customer site",
            Point = Point(4.7, -74.0),
            Status = status,
            EtaSource = EtaSources.Unavailable,
        };

    internal static Point Point(double latitude, double longitude)
        => new(longitude, latitude) { SRID = 4326 };
}
