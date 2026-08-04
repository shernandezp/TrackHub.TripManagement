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

using TrackHub.TripManagement.Application.Trips.Commands.Assign;
using TrackHub.TripManagement.Application.Trips.Commands.Create;
using TrackHub.TripManagement.Application.Trips.Commands.Delete;
using TrackHub.TripManagement.Application.Trips.Commands.ImportCsv;
using TrackHub.TripManagement.Application.Trips.Commands.Lifecycle;
using TrackHub.TripManagement.Application.Trips.Commands.PlanRoute;
using TrackHub.TripManagement.Application.Trips.Commands.Update;
using TrackHub.TripManagement.Application.Trips.Services.Interfaces;

namespace TrackHub.TripManagement.Web.GraphQL.Mutation;

/// <summary>Trip CRUD, assignment, route planning and the lifecycle transitions.</summary>
public partial class Mutation
{
    public async Task<TripVm> CreateTrip([Service] ISender sender, CreateTripCommand command, CancellationToken cancellationToken)
        => await sender.Send(command, cancellationToken);

    public async Task<bool> UpdateTrip([Service] ISender sender, UpdateTripCommand command, CancellationToken cancellationToken)
    {
        await sender.Send(command, cancellationToken);
        return true;
    }

    // Delete mutations return the deleted identifier, not a boolean (rules.md naming).
    public async Task<Guid> DeleteTrip([Service] ISender sender, Guid id, CancellationToken cancellationToken)
    {
        await sender.Send(new DeleteTripCommand(id), cancellationToken);
        return id;
    }

    public async Task<TripAssignmentVm> AssignTrip([Service] ISender sender, AssignTripCommand command, CancellationToken cancellationToken)
        => await sender.Send(command, cancellationToken);

    /// <summary>
    /// Bulk trip planning from a spreadsheet (spec 11a §9.1). Per-row results; one bad row never
    /// fails the batch.
    /// </summary>
    public async Task<TripCsvImportResultVm> ImportTripsCsv([Service] ISender sender, ImportTripsCsvCommand command, CancellationToken cancellationToken)
        => await sender.Send(command, cancellationToken);

    public async Task<RoutePlanVm> PlanTripRoute([Service] ISender sender, PlanTripRouteCommand command, CancellationToken cancellationToken)
        => await sender.Send(command, cancellationToken);

    public async Task<bool> StartTrip([Service] ISender sender, Guid id, CancellationToken cancellationToken)
    {
        await sender.Send(new StartTripCommand(id), cancellationToken);
        return true;
    }

    /// <summary>
    /// Records that a freshly planned trip is already under way, backfilling what Geofencing
    /// measured (spec 11a §5.4). Returns whether the start came from evidence or from the caller's
    /// declaration, so the dispatcher can see which.
    /// </summary>
    public async Task<TripStartBackfillResultVm> DeclareTripInTransit(
        [Service] ISender sender, DeclareTripInTransitCommand command, CancellationToken cancellationToken)
        => await sender.Send(command, cancellationToken);

    public async Task<bool> PauseTrip([Service] ISender sender, Guid id, CancellationToken cancellationToken)
    {
        await sender.Send(new PauseTripCommand(id), cancellationToken);
        return true;
    }

    public async Task<bool> ResumeTrip([Service] ISender sender, Guid id, CancellationToken cancellationToken)
    {
        await sender.Send(new ResumeTripCommand(id), cancellationToken);
        return true;
    }

    public async Task<bool> CompleteTrip([Service] ISender sender, CompleteTripCommand command, CancellationToken cancellationToken)
    {
        await sender.Send(command, cancellationToken);
        return true;
    }

    public async Task<bool> CancelTrip([Service] ISender sender, CancelTripCommand command, CancellationToken cancellationToken)
    {
        await sender.Send(command, cancellationToken);
        return true;
    }

    public async Task<bool> AbortTrip([Service] ISender sender, AbortTripCommand command, CancellationToken cancellationToken)
    {
        await sender.Send(command, cancellationToken);
        return true;
    }
}
