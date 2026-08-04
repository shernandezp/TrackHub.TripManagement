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

using Common.Application.Exceptions;
using TrackHub.TripManagement.Infrastructure.TripDB.Events;
using TrackHub.TripManagement.Infrastructure.TripDB.Readers;

namespace TrackHub.TripManagement.Infrastructure.TripDB.Writers;

public sealed class DeliveryWriter(IApplicationDbContext context) : IDeliveryWriter
{
    public async Task<DeliveryVm> CreateDeliveryAsync(Guid tripStopId, Guid accountId, DeliveryDto delivery, CancellationToken cancellationToken)
    {
        var stop = await context.TripStops.FirstOrDefaultAsync(s => s.TripStopId == tripStopId && s.AccountId == accountId, cancellationToken)
            ?? throw new NotFoundException($"{tripStopId}", nameof(TripStop));

        var entity = new Delivery
        {
            AccountId = accountId,
            TripStopId = stop.TripStopId,
            Reference = delivery.Reference,
            ClientName = delivery.ClientName,
            BranchName = delivery.BranchName,
            ProductsSummary = delivery.ProductsSummary,
            Status = DeliveryStatuses.Pending,
            Observations = delivery.Observations,
            SequenceIndex = delivery.SequenceIndex,
        };

        entity.AddDomainEvent(new TripDomainEvent(TripEventTypes.TripUpdated, accountId, stop.TripId, entity.DeliveryId));
        await context.Deliveries.AddAsync(entity, cancellationToken);
        await context.SaveChangesAsync(cancellationToken);

        return TripMapper.ToVm(entity);
    }

    public async Task UpdateDeliveryAsync(Guid deliveryId, Guid accountId, DeliveryDto delivery, CancellationToken cancellationToken)
    {
        var entity = await FindAsync(deliveryId, accountId, cancellationToken);

        // Cross-stop moves are rejected by omission: TripStopId is never assignable here, a
        // delivery belongs to the stop it was created on.
        entity.Reference = delivery.Reference;
        entity.ClientName = delivery.ClientName;
        entity.BranchName = delivery.BranchName;
        entity.ProductsSummary = delivery.ProductsSummary;
        entity.Observations = delivery.Observations;
        entity.SequenceIndex = delivery.SequenceIndex;

        await context.SaveChangesAsync(cancellationToken);
    }

    public async Task<bool> UpdateDeliveryOutcomeAsync(
        Guid deliveryId,
        Guid accountId,
        string status,
        string? observations,
        string idempotencyKey,
        CancellationToken cancellationToken)
    {
        var entity = await FindAsync(deliveryId, accountId, cancellationToken);
        var tripId = await context.TripStops
            .Where(s => s.TripStopId == entity.TripStopId)
            .Select(s => s.TripId)
            .FirstAsync(cancellationToken);

        // The outcome and its event are one unit of work: the unique idempotency index is what
        // makes a retried offline submission a no-op rather than a second state change.
        var tripEvent = new TripEvent
        {
            AccountId = accountId,
            TripId = tripId,
            TripStopId = entity.TripStopId,
            EventType = TripEventTypes.TripDeliveryOutcomeRecorded,
            OccurredAt = DateTimeOffset.UtcNow,
            Source = TripEventSources.Portal,
            IdempotencyKey = idempotencyKey,
        };

        entity.Status = status;
        if (!string.IsNullOrWhiteSpace(observations))
        {
            entity.Observations = observations;
        }

        entity.AddDomainEvent(new TripDomainEvent(
            TripEventTypes.TripDeliveryOutcomeRecorded, accountId, tripId, entity.DeliveryId));

        await context.TripEvents.AddAsync(tripEvent, cancellationToken);

        try
        {
            await context.SaveChangesAsync(cancellationToken);
            return true;
        }
        catch (DbUpdateException exception) when (UniqueViolation.Matches(exception, "ux_trip_events_idempotencykey"))
        {
            // A retry, not an error - but the dead insert and the outcome mutation must leave the
            // change tracker, or the next SaveChangesAsync on this scoped context replays them.
            context.TripEvents.Entry(tripEvent).State = EntityState.Detached;
            context.Deliveries.Entry(entity).State = EntityState.Detached;
            return false;
        }
    }

    public async Task DeleteDeliveryAsync(Guid deliveryId, Guid accountId, CancellationToken cancellationToken)
    {
        var entity = await FindAsync(deliveryId, accountId, cancellationToken);
        context.Deliveries.Remove(entity);
        await context.SaveChangesAsync(cancellationToken);
    }

    public async Task MarkStopDeliveriesAsync(Guid tripStopId, Guid accountId, string status, CancellationToken cancellationToken)
    {
        var deliveries = await context.Deliveries
            .AsTracking()
            .Where(d => d.TripStopId == tripStopId
                && d.AccountId == accountId
                && d.Status == DeliveryStatuses.Pending)
            .ToListAsync(cancellationToken);

        if (deliveries.Count == 0)
        {
            return;
        }

        foreach (var delivery in deliveries)
        {
            delivery.Status = status;
        }

        await context.SaveChangesAsync(cancellationToken);
    }

    private async Task<Delivery> FindAsync(Guid deliveryId, Guid accountId, CancellationToken cancellationToken)
        => await context.Deliveries.AsTracking().FirstOrDefaultAsync(d => d.DeliveryId == deliveryId && d.AccountId == accountId, cancellationToken)
            ?? throw new NotFoundException($"{deliveryId}", nameof(Delivery));
}
