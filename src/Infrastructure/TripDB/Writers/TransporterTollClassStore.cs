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

using TrackHub.TripManagement.Infrastructure.TripDB.Readers;

namespace TrackHub.TripManagement.Infrastructure.TripDB.Writers;

/// <summary>
/// The account-scoped half of toll configuration. Account-scoped precisely because fleet
/// composition is tenant data, unlike the catalog itself (spec 11 section 5).
/// </summary>
public sealed class TransporterTollClassStore(IApplicationDbContext context) : ITransporterTollClassStore
{
    public async Task<string?> ResolveClassAsync(Guid accountId, Guid transporterId, CancellationToken cancellationToken)
    {
        // A row-level TransporterId override wins over the TransporterTypeId mapping.
        var overrideCode = await context.TransporterTollClasses
            .Where(m => m.AccountId == accountId && m.TransporterId == transporterId)
            .Select(m => m.TollVehicleClassCode)
            .FirstOrDefaultAsync(cancellationToken);

        if (!string.IsNullOrWhiteSpace(overrideCode))
        {
            return overrideCode;
        }

        // Fall back to the transporter-type mapping, resolving the type from the read-only
        // app.transporters projection rather than a cross-service call. Null means "unclassified",
        // which the estimate reports honestly instead of guessing a class.
        return await (from t in context.Transporters
                      join m in context.TransporterTollClasses
                        on new { t.TransporterTypeId, AccountId = accountId }
                        equals new { TransporterTypeId = m.TransporterTypeId!.Value, m.AccountId }
                      where t.TransporterId == transporterId
                        && t.AccountId == accountId
                        && m.TransporterId == null
                      select m.TollVehicleClassCode)
            .FirstOrDefaultAsync(cancellationToken);
    }

    public async Task<IReadOnlyCollection<TransporterTollClassVm>> GetMappingsAsync(Guid accountId, CancellationToken cancellationToken)
    {
        var mappings = await context.TransporterTollClasses
            .Where(m => m.AccountId == accountId)
            .OrderBy(m => m.TransporterTypeId)
            .ThenBy(m => m.TransporterId)
            .ToListAsync(cancellationToken);

        return [.. mappings.Select(TripMapper.ToVm)];
    }

    public async Task<TransporterTollClassVm> SetMappingAsync(
        Guid accountId,
        short? transporterTypeId,
        Guid? transporterId,
        string vehicleClassCode,
        CancellationToken cancellationToken)
    {
        var existing = await context.TransporterTollClasses
            .AsTracking()
            .FirstOrDefaultAsync(
                m => m.AccountId == accountId
                    && m.TransporterTypeId == transporterTypeId
                    && m.TransporterId == transporterId,
                cancellationToken);

        if (existing is not null)
        {
            existing.TollVehicleClassCode = vehicleClassCode;
            await context.SaveChangesAsync(cancellationToken);
            return TripMapper.ToVm(existing);
        }

        var entity = new TransporterTollClass
        {
            AccountId = accountId,
            TransporterTypeId = transporterTypeId,
            TransporterId = transporterId,
            TollVehicleClassCode = vehicleClassCode,
        };

        await context.TransporterTollClasses.AddAsync(entity, cancellationToken);
        await context.SaveChangesAsync(cancellationToken);

        return TripMapper.ToVm(entity);
    }
}
