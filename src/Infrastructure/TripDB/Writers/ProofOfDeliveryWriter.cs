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

using TrackHub.TripManagement.Infrastructure.TripDB.Events;
using TrackHub.TripManagement.Infrastructure.TripDB.Readers;

namespace TrackHub.TripManagement.Infrastructure.TripDB.Writers;

/// <summary>
/// Proof-of-delivery capture.
/// <para>
/// Document ids are validated by the caller against the account and <c>ScanStatus = Clean</c>
/// BEFORE this is reached - this store LINKS, it does not authorize (spec 11 section 7.3).
/// </para>
/// </summary>
public sealed class ProofOfDeliveryWriter(IApplicationDbContext context) : IProofOfDeliveryWriter
{
    public async Task<(ProofOfDeliveryVm ProofOfDelivery, bool Created)> RecordAsync(
        Guid accountId,
        Guid tripId,
        ProofOfDeliveryDto proofOfDelivery,
        CancellationToken cancellationToken)
    {
        // Resolved by (stop, account, TRIP). Without the trip predicate a caller could pass the
        // terminal-status check for trip X and attach the POD to a stop of trip Y.
        var stop = await context.TripStops
            .FirstOrDefaultAsync(
                s => s.TripStopId == proofOfDelivery.TripStopId && s.AccountId == accountId && s.TripId == tripId,
                cancellationToken)
            ?? throw new NotFoundException($"{proofOfDelivery.TripStopId}", nameof(TripStop));

        var entity = new ProofOfDelivery
        {
            AccountId = accountId,
            TripStopId = stop.TripStopId,
            DeliveryId = proofOfDelivery.DeliveryId,
            ReceiverName = proofOfDelivery.ReceiverName,
            ReceiverDocument = proofOfDelivery.ReceiverDocument,
            CapturedAt = proofOfDelivery.CapturedAt,
            Latitude = proofOfDelivery.Latitude,
            Longitude = proofOfDelivery.Longitude,
            Notes = proofOfDelivery.Notes,
            ClientEventId = proofOfDelivery.ClientEventId,
        };

        var documents = proofOfDelivery.DocumentIds
            .Distinct()
            .Select(documentId => new TripDocument
            {
                AccountId = accountId,
                TripId = tripId,
                TripStopId = stop.TripStopId,
                ProofOfDeliveryId = entity.ProofOfDeliveryId,
                DocumentId = documentId,
                Kind = TripDocumentKinds.Signature,
            })
            .ToList();

        entity.AddDomainEvent(new TripDomainEvent(
            TripEventTypes.TripPodSubmitted, accountId, tripId, entity.ProofOfDeliveryId));

        await context.ProofsOfDelivery.AddAsync(entity, cancellationToken);
        await context.TripDocuments.AddRangeAsync(documents, cancellationToken);

        try
        {
            await context.SaveChangesAsync(cancellationToken);
        }
        catch (DbUpdateException exception) when (UniqueViolation.Matches(exception))
        {
            // Retried offline submission: return the POD that already exists rather than
            // throwing (acceptance 15). The unique (TripStopId, ClientEventId) index - not a
            // pre-flight read - is what makes this race-safe.
            //
            // The failed inserts MUST leave the change tracker first. The context is scoped to the
            // request, so a still-Added POD replays on the next SaveChangesAsync - which is both
            // how a duplicate submission surfaced as a 500 and how later genuine writes in the same
            // request were lost (contrast TripEventWriter, which detaches).
            context.ProofsOfDelivery.Entry(entity).State = EntityState.Detached;
            foreach (var document in documents)
            {
                context.TripDocuments.Entry(document).State = EntityState.Detached;
            }

            var existing = await ExistingAsync(accountId, proofOfDelivery, cancellationToken);
            if (existing is null)
            {
                throw;
            }

            return (existing.Value, false);
        }

        return (TripMapper.ToVm(entity, [.. documents.Select(TripMapper.ToVm)]), true);
    }

    public async Task<bool> HasAsync(Guid accountId, Guid tripStopId, Guid clientEventId, CancellationToken cancellationToken)
        => await context.ProofsOfDelivery
            .AnyAsync(p => p.TripStopId == tripStopId && p.ClientEventId == clientEventId && p.AccountId == accountId,
                cancellationToken);

    private async Task<ProofOfDeliveryVm?> ExistingAsync(
        Guid accountId,
        ProofOfDeliveryDto proofOfDelivery,
        CancellationToken cancellationToken)
    {
        var existing = await context.ProofsOfDelivery
            .AsNoTracking()
            .FirstOrDefaultAsync(
                p => p.TripStopId == proofOfDelivery.TripStopId
                    && p.ClientEventId == proofOfDelivery.ClientEventId
                    && p.AccountId == accountId,
                cancellationToken);

        if (existing is null)
        {
            return null;
        }

        var documents = await context.TripDocuments
            .AsNoTracking()
            .Where(d => d.ProofOfDeliveryId == existing.ProofOfDeliveryId)
            .ToListAsync(cancellationToken);

        return TripMapper.ToVm(existing, [.. documents.Select(TripMapper.ToVm)]);
    }
}
