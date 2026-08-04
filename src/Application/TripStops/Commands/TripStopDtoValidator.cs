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

namespace TrackHub.TripManagement.Application.TripStops.Commands;

/// <summary>Shared shape validation for the stop write contract (add and update).</summary>
public sealed class TripStopDtoValidator : AbstractValidator<TripStopDto>
{
    public TripStopDtoValidator()
    {
        RuleFor(v => v.Name).NotEmpty().MaximumLength(200);
        RuleFor(v => v.Address).MaximumLength(500);
        // Matches the column width. Without this an over-long city surfaces as a
        // DbUpdateException (500) instead of a validation failure (400).
        RuleFor(v => v.City).MaximumLength(200);
        RuleFor(v => v.Latitude).InclusiveBetween(-90d, 90d);
        RuleFor(v => v.Longitude).InclusiveBetween(-180d, 180d);
        RuleFor(v => v.ArrivalRadiusMeters)
            .InclusiveBetween(TripGeometry.MinRadiusMeters, TripGeometry.MaxRadiusMeters);

        // Deliberately not NotEmpty: an omitted activity is normalized to Unload by the writer, so
        // a client that predates the field keeps working. Only a value that is present AND wrong is
        // a validation failure.
        RuleFor(v => v.Activity)
            .Must(TripStopActivities.IsValid)
            .When(v => !string.IsNullOrWhiteSpace(v.Activity))
            .WithMessage("Unknown stop activity.");
        RuleFor(v => v.Observations).MaximumLength(1000);
        RuleFor(v => v.PlannedArrivalTo)
            .GreaterThanOrEqualTo(v => v.PlannedArrivalFrom!.Value)
            .When(v => v.PlannedArrivalFrom.HasValue && v.PlannedArrivalTo.HasValue);
    }
}
