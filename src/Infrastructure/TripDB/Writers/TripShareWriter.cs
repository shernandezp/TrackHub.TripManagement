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

public sealed class TripShareWriter(IApplicationDbContext context) : ITripShareWriter
{
    public async Task<TripShareVm> CreateShareAsync(
        Guid tripId,
        Guid accountId,
        Guid publicLinkGrantId,
        TripShareFieldFlagsDto fieldFlags,
        string createdByPrincipalId,
        DateTimeOffset expiresAt,
        CancellationToken cancellationToken)
    {
        _ = await context.Trips.FirstOrDefaultAsync(t => t.TripId == tripId && t.AccountId == accountId, cancellationToken)
            ?? throw new NotFoundException($"{tripId}", nameof(Trip));

        var entity = new TripShare
        {
            AccountId = accountId,
            TripId = tripId,
            PublicLinkGrantId = publicLinkGrantId,
            IncludeDriverName = fieldFlags.IncludeDriverName,
            IncludeVehicle = fieldFlags.IncludeVehicle,
            IncludeLivePosition = fieldFlags.IncludeLivePosition,
            IncludeStopDetail = fieldFlags.IncludeStopDetail,
            IncludePodSummary = fieldFlags.IncludePodSummary,
            IncludeRoute = fieldFlags.IncludeRoute,
            CreatedByPrincipalId = createdByPrincipalId,
            ExpiresAt = expiresAt,
        };

        entity.AddDomainEvent(new TripDomainEvent(TripEventTypes.TripShared, accountId, tripId, entity.TripShareId));

        await context.TripShares.AddAsync(entity, cancellationToken);
        await context.SaveChangesAsync(cancellationToken);

        // The plaintext token belongs to Manager's grant response and is returned to the caller
        // exactly once, by the command - never re-read from this row (acceptance 23).
        return TripMapper.ToVm(entity);
    }

    public async Task<Guid> RevokeShareAsync(Guid tripShareId, Guid accountId, CancellationToken cancellationToken)
    {
        var entity = await context.TripShares
            .AsTracking()
            .FirstOrDefaultAsync(s => s.TripShareId == tripShareId && s.AccountId == accountId, cancellationToken)
            ?? throw new NotFoundException($"{tripShareId}", nameof(TripShare));

        // Idempotent: re-revoking keeps the first revocation instant.
        entity.RevokedAt ??= DateTimeOffset.UtcNow;
        entity.AddDomainEvent(new TripDomainEvent(
            TripEventTypes.TripShareRevoked, accountId, entity.TripId, entity.TripShareId));

        await context.SaveChangesAsync(cancellationToken);

        // The Manager grant id, NOT the local TripShareId: the caller feeds this straight into
        // Manager's revoke. Returning the local id would leave the public link live after a
        // successful-looking revoke (acceptance 24).
        return entity.PublicLinkGrantId;
    }
}
