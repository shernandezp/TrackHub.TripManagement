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

namespace TrackHub.TripManagement.Application.Trips.Commands;

/// <summary>Shared shape validation for the trip write contract (create and update).</summary>
public sealed class TripDtoValidator : AbstractValidator<TripDto>
{
    public TripDtoValidator()
    {
        RuleFor(v => v.Code).NotEmpty().MaximumLength(40);
        RuleFor(v => v.TransporterId).NotEmpty();
        RuleFor(v => v.ExternalReference).MaximumLength(80);
        RuleFor(v => v.CustomerName).MaximumLength(200);
        RuleFor(v => v.OriginName).NotEmpty().MaximumLength(200);
        RuleFor(v => v.OriginLatitude).InclusiveBetween(-90d, 90d);
        RuleFor(v => v.OriginLongitude).InclusiveBetween(-180d, 180d);
        RuleFor(v => v.OriginRadiusMeters)
            .InclusiveBetween(TripGeometry.MinRadiusMeters, TripGeometry.MaxRadiusMeters);
        RuleFor(v => v.Notes).MaximumLength(2000);
        RuleFor(v => v.TollVehicleClass).MaximumLength(20);
        RuleFor(v => v.PlannedStartAt).NotEqual(default(DateTimeOffset));
        RuleFor(v => v.PlannedEndAt)
            .GreaterThanOrEqualTo(v => v.PlannedStartAt)
            .When(v => v.PlannedEndAt.HasValue);
    }
}
