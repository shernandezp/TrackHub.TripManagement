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

using TrackHub.TripManagement.Application.Deliveries.Commands.UpdateOutcome;
using TrackHub.TripManagement.Application.TollCatalog.Commands.Tariffs;
using TrackHub.TripManagement.Application.TripStops.Commands;
using TrackHub.TripManagement.Application.TripStops.Commands.Progress;
using TrackHub.TripManagement.Application.Trips.Commands;
using TrackHub.TripManagement.Application.Trips.Commands.Lifecycle;
using TrackHub.TripManagement.Application.Trips.Commands.PlanRoute;
using TrackHub.TripManagement.Application.Trips.Queries.GetTrips;

namespace TrackHub.TripManagement.Application.UnitTests;

/// <summary>Validators: the cheap gate that keeps malformed input out of the handlers.</summary>
[TestFixture]
public class ValidatorTests
{
    [Test]
    public void TripDto_RejectsOutOfRangeOriginCoordinates()
    {
        var validator = new TripDtoValidator();

        var result = validator.Validate(Trip() with { OriginLatitude = 91d });

        Assert.That(result.IsValid, Is.False);
    }

    [Test]
    public void TripDto_RejectsAPlannedEndBeforeThePlannedStart()
    {
        var validator = new TripDtoValidator();
        var start = DateTimeOffset.UtcNow;

        var result = validator.Validate(Trip() with { PlannedStartAt = start, PlannedEndAt = start.AddHours(-1) });

        Assert.That(result.IsValid, Is.False);
    }

    [Test]
    public void TripDto_AcceptsAWellFormedTrip()
        => Assert.That(new TripDtoValidator().Validate(Trip()).IsValid, Is.True);

    [TestCase(49)]
    [TestCase(5001)]
    public void TripStopDto_RejectsAnArrivalRadiusOutsideTheSupportedRange(int radius)
    {
        var result = new TripStopDtoValidator().Validate(Stop() with { ArrivalRadiusMeters = radius });

        Assert.That(result.IsValid, Is.False);
    }

    [Test]
    public void TripStopDto_AcceptsTheDefaultRadius()
        => Assert.That(new TripStopDtoValidator().Validate(Stop()).IsValid, Is.True);

    [Test]
    public void RecordStopArrival_RequiresAClientEventId()
    {
        var result = new RecordStopArrivalValidator().Validate(
            new RecordStopArrivalCommand(TestFactory.TripId, TestFactory.StopId, DateTimeOffset.UtcNow, null, null, Guid.Empty));

        Assert.That(result.IsValid, Is.False, "without a client event id there is nothing to be idempotent on");
    }

    [Test]
    public void SkipStop_RequiresAReason()
    {
        var result = new SkipStopValidator().Validate(
            new SkipStopCommand(TestFactory.TripId, TestFactory.StopId, DateTimeOffset.UtcNow, "", Guid.NewGuid()));

        Assert.That(result.IsValid, Is.False);
    }

    [Test]
    public void CancelTrip_RequiresAReason()
    {
        var result = new CancelTripValidator().Validate(new CancelTripCommand(TestFactory.TripId, ""));

        Assert.That(result.IsValid, Is.False);
    }

    [TestCase(99)]
    [TestCase(5001)]
    public void PlanTripRoute_RejectsAnUnsupportedCorridorWidth(int corridorMeters)
    {
        var result = new PlanTripRouteValidator().Validate(new PlanTripRouteCommand(TestFactory.TripId, corridorMeters, null));

        Assert.That(result.IsValid, Is.False);
    }

    [Test]
    public void GetTrips_RejectsATakeAboveTheClamp()
    {
        var result = new GetTripsValidator().Validate(new GetTripsQuery(null, null, null, null, null, null, null, 0, 201));

        Assert.That(result.IsValid, Is.False);
    }

    [Test]
    public void GetTrips_RejectsAnUnknownStatusFilter()
    {
        var result = new GetTripsValidator().Validate(new GetTripsQuery(["Teleported"], null, null, null, null, null, null, null, null));

        Assert.That(result.IsValid, Is.False);
    }

    [Test]
    public void UpdateDeliveryOutcome_RejectsAnUnknownStatus()
    {
        var result = new UpdateDeliveryOutcomeValidator().Validate(
            new UpdateDeliveryOutcomeCommand(TestFactory.TripId, Guid.NewGuid(), "Teleported", null, Guid.NewGuid()));

        Assert.That(result.IsValid, Is.False);
    }

    [Test]
    public void TollTariff_RejectsAnEffectiveToBeforeTheEffectiveFrom()
    {
        var result = new TollTariffDtoValidator().Validate(new TollTariffDto(
            Guid.NewGuid(), "II", 1000m, "COP", new DateOnly(2026, 7, 21), new DateOnly(2026, 7, 1)));

        Assert.That(result.IsValid, Is.False);
    }

    [Test]
    public void TollTariff_RejectsANonIsoCurrency()
    {
        var result = new TollTariffDtoValidator().Validate(new TollTariffDto(
            Guid.NewGuid(), "II", 1000m, "PESOS", new DateOnly(2026, 7, 21), null));

        Assert.That(result.IsValid, Is.False);
    }

    private static TripDto Trip()
        => new("TRIP-001", TestFactory.TransporterId, null, null, null, "ACME", "Depot", 4.65, -74.05, null, TripGeometry.DefaultRadiusMeters,
            DateTimeOffset.UtcNow, null, null, null);

    private static TripStopDto Stop()
        => new("Customer site", null, null, 4.7, -74.0, null, 150, TripStopActivities.Unload, null, null, false, 0, null);
}
