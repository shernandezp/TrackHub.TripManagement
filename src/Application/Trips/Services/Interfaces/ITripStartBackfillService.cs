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
/// Puts a late-created trip into the state its vehicle is already in (spec 11a §5.4).
/// <para>
/// Real operations plan after the fact: a bulk upload lands on Monday for trucks that left on
/// Sunday, and a dispatcher opening the single-trip dialog is often recording something already
/// under way. Such a trip must not sit in <c>Created</c> waiting for an origin arrival that
/// happened hours ago.
/// </para>
/// <para>
/// Evidence beats declaration. When Geofencing recorded the vehicle's visit to the origin zone,
/// those timestamps are replayed as <c>Detection</c> measurements — including any stop visits that
/// followed. Only when there is no recorded visit does the user's declared start time stand in, and
/// then <c>OriginArrivedAt</c> stays null: loading was not measured, and saying so is more useful
/// than inventing it.
/// </para>
/// <para>
/// It runs AFTER the trip's stops exist, which is why it is a step of its own rather than a field on
/// the create contract — replaying a route needs the route.
/// </para>
/// </summary>
public interface ITripStartBackfillService
{
    Task<TripStartBackfillResultVm> ApplyAsync(
        Guid tripId,
        Guid accountId,
        Guid? scopeUserId,
        DateTimeOffset? declaredStartAt,
        CancellationToken cancellationToken);
}

/// <summary>
/// What the declaration actually did. <paramref name="Backfilled"/> distinguishes a start built
/// from recorded evidence from one the user declared — the portal says which, so a dispatcher can
/// see that the system found the departure rather than took their word for it.
/// </summary>
public readonly record struct TripStartBackfillResultVm(
    bool Started,
    bool Backfilled,
    DateTimeOffset? StartedAt,
    int StopsReplayed);
