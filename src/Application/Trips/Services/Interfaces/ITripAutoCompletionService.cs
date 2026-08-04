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

namespace TrackHub.TripManagement.Application.Trips.Services.Interfaces;

/// <summary>
/// Closes a trip whose route is done (spec 11a §5.2). One implementation, two callers: the detection
/// pipeline evaluates it the instant a stop closes, and the <c>trip-eta-refresh</c> loop sweeps for
/// the trips detection can never reach — a truck that arrives home, parks and whose tracker goes
/// quiet produces no further fix, so nothing would ever ask again.
/// </summary>
public interface ITripAutoCompletionService
{
    /// <summary>
    /// Completes the trip when either rule fires: every stop closed (<c>ActualEndAt</c> = the last
    /// measured departure), or every non-final stop closed and the final one <c>Arrived</c> for at
    /// least <c>finalStopCompletionMinutes</c> (<c>ActualEndAt</c> = that arrival).
    /// <para>
    /// The dwell rule exists for the depot reality: a truck that comes home and parks never
    /// "departs" its final stop, and the trip still has to close.
    /// </para>
    /// Returns true only when this call actually completed the trip.
    /// </summary>
    Task<bool> TryCompleteAsync(
        Guid accountId,
        Guid tripId,
        DateTimeOffset evaluatedAt,
        int finalStopCompletionMinutes,
        CancellationToken cancellationToken);

    /// <summary>
    /// The fallback sweep across every trip-enabled account, for devices that went dark.
    /// <para>
    /// Returns the number of trips completed. It rides the existing <c>trip-eta-refresh</c> cycle
    /// rather than adding a <c>BackgroundJobKeys</c> entry, and reports zero when it did nothing so
    /// the on-work-only recorder (SVD-11) stays on-work-only.
    /// </para>
    /// </summary>
    Task<int> SweepAsync(CancellationToken cancellationToken);
}
