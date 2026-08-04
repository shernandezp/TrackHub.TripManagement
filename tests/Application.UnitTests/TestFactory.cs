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

using Common.Application.Interfaces;
using Microsoft.Extensions.Logging;
using Microsoft.Extensions.Logging.Abstractions;

namespace TrackHub.TripManagement.Application.UnitTests;

/// <summary>Shared fixtures so each test states only what it is actually asserting.</summary>
internal static class TestFactory
{
    public static readonly Guid AccountId = Guid.Parse("11111111-1111-1111-1111-111111111111");
    public static readonly Guid UserId = Guid.Parse("22222222-2222-2222-2222-222222222222");
    public static readonly Guid TripId = Guid.Parse("33333333-3333-3333-3333-333333333333");
    public static readonly Guid StopId = Guid.Parse("44444444-4444-4444-4444-444444444444");
    public static readonly Guid TransporterId = Guid.Parse("55555555-5555-5555-5555-555555555555");
    public static readonly Guid RoutePlanId = Guid.Parse("66666666-6666-6666-6666-666666666666");

    public static Mock<IUser> User(string role = Roles.Administrator)
    {
        var user = new Mock<IUser>();
        user.SetupGet(u => u.Id).Returns(UserId.ToString());
        user.SetupGet(u => u.Role).Returns(role);
        user.SetupGet(u => u.PrincipalType).Returns(PrincipalType.User);
        return user;
    }

    public static Mock<IUserReader> UserReader()
    {
        var reader = new Mock<IUserReader>();
        reader.Setup(r => r.GetUserAsync(UserId, It.IsAny<CancellationToken>()))
            .ReturnsAsync(new UserVm(UserId, AccountId, "dispatcher"));
        return reader;
    }

    public static ILogger<T> Logger<T>() => NullLogger<T>.Instance;

    public static TripVm Trip(
        string status = TripStatuses.InProgress,
        Guid? tripId = null,
        Guid? originGeofenceId = null,
        DateTimeOffset? actualStartAt = null)
        => new(
            tripId ?? TripId,
            AccountId,
            "TRIP-001",
            status,
            TransporterId,
            null,
            null,
            null,
            null,
            "ACME",
            "Depot",
            4.65,
            -74.05,
            originGeofenceId,
            TripGeometry.DefaultRadiusMeters,
            DateTimeOffset.UtcNow,
            null,
            actualStartAt,
            null,
            null,
            null,
            null,
            TripPhases.Scheduled,
            null,
            null,
            null,
            false,
            null,
            null,
            null,
            null,
            0d,
            "II",
            null,
            null,
            2,
            2,
            DateTimeOffset.UtcNow);

    public static TripStopVm Stop(string status = TripStopStatuses.Pending, int sequence = 1, Guid? stopId = null)
        => new(
            stopId ?? StopId,
            AccountId,
            TripId,
            sequence,
            "Customer site",
            null,
            // City — the coarse locality, distinct from the full street Address above.
            null,
            4.7,
            -74.0,
            null,
            150,
            TripStopActivities.Unload,
            null,
            null,
            status,
            null,
            null,
            null,
            EtaSources.Unavailable,
            null,
            false,
            0,
            null,
            []);

    // lastLatitude/lastLongitude are the PERSISTED Trip.LastPoint the detection reader projects.
    // They are what makes the odometer accumulate across calls: Router delivers one fix per call,
    // so the previous point can only come from the row, never from per-request memory.
    // The status/origin arguments are what widened with zero-touch: a watched trip may be a Created
    // one waiting at the gate or an InProgress one on the road, and every automatic step is gated on
    // which. Defaults describe a running trip that has already left its origin, so the tests written
    // before zero-touch keep describing exactly the scenario they always did.
    public static OpenTripVm OpenTrip(
        IReadOnlyCollection<OpenTripStopVm> stops,
        bool hasReadyPlan = false,
        DateTimeOffset? deviationOpenedAt = null,
        int consecutiveOutsideFixes = 0,
        double actualDistanceMeters = 0d,
        double? lastLatitude = null,
        double? lastLongitude = null,
        DateTimeOffset? lastPositionAt = null,
        string status = TripStatuses.InProgress,
        DateTimeOffset? plannedStartAt = null,
        DateTimeOffset? armedAt = null,
        bool hasOriginGeom = false,
        DateTimeOffset? originArrivedAt = null,
        DateTimeOffset? originDepartedAt = null,
        DateTimeOffset? originOutsideSinceAt = null)
        => new(
            TripId,
            AccountId,
            "TRIP-001",
            status,
            TransporterId,
            null,
            hasReadyPlan ? RoutePlanId : null,
            hasReadyPlan,
            plannedStartAt ?? DateTimeOffset.UtcNow,
            armedAt,
            hasOriginGeom,
            originArrivedAt,
            originDepartedAt,
            originOutsideSinceAt,
            deviationOpenedAt,
            consecutiveOutsideFixes,
            actualDistanceMeters,
            lastLatitude,
            lastLongitude,
            lastPositionAt,
            stops);

    public static OpenTripStopVm OpenStop(
        Guid stopId,
        string status = TripStopStatuses.Pending,
        int sequence = 1,
        DateTimeOffset? outsideSinceAt = null)
        => new(stopId, sequence, "Customer site", status, null, null, null, outsideSinceAt, 4.7, -74.0);

    public static TransporterPositionDto Position(double latitude, double longitude, DateTimeOffset at)
        => new(TransporterId, latitude, longitude, at);

    public static TollStationMatchVm Match(bool hasTariff, decimal? amount, string? currency = "COP")
        => new(Guid.NewGuid(), "Station", null, 4.7, -74.0, null, null, amount, hasTariff ? currency : null, hasTariff);
}
