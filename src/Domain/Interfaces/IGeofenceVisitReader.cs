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
/// Reads Geofencing's recorded visit history so a trip created AFTER its vehicle already left can be
/// built from measurements instead of guesses (spec 11a §5.4).
/// <para>
/// This is the only place trip management looks at another module's event history, and it is
/// strictly read-only: the trip module replays what Geofencing observed, it never asserts anything
/// back into it.
/// </para>
/// </summary>
public interface IGeofenceVisitReader
{
    /// <summary>
    /// Every visit the transporter made to any of <paramref name="geofenceIds"/> at or after
    /// <paramref name="since"/>, oldest first.
    /// <para>
    /// One query for the whole route rather than one per stop: a ten-stop trip would otherwise cost
    /// eleven round trips to answer a question about a single vehicle's last day.
    /// </para>
    /// </summary>
    Task<IReadOnlyCollection<GeofenceVisitVm>> GetVisitsAsync(
        Guid accountId,
        Guid transporterId,
        IReadOnlyCollection<Guid> geofenceIds,
        DateTimeOffset since,
        CancellationToken cancellationToken);
}

/// <summary>One recorded stay inside a zone. <see cref="DepartedAt"/> is null while still inside.</summary>
public readonly record struct GeofenceVisitVm(
    Guid GeofenceId,
    DateTimeOffset EnteredAt,
    DateTimeOffset? DepartedAt);
