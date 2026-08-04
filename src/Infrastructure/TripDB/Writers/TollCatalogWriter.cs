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
using Common.Application.Interfaces;
using TrackHub.TripManagement.Infrastructure.TripDB.Events;
using TrackHub.TripManagement.Infrastructure.TripDB.Readers;

namespace TrackHub.TripManagement.Infrastructure.TripDB.Writers;

/// <summary>
/// Administrator-only toll administration. Every write is audited - station and tariff
/// changes move money on estimates (spec 11 section 7.6, acceptance 26).
/// </summary>
public sealed class TollCatalogWriter(IApplicationDbContext context, ITollCatalogReader reader, IUser user) : ITollCatalogWriter
{
    public async Task<TollVehicleClassVm> CreateVehicleClassAsync(TollVehicleClassDto vehicleClass, CancellationToken cancellationToken)
    {
        var entity = new TollVehicleClass
        {
            Code = vehicleClass.Code,
            Name = vehicleClass.Name,
            Description = vehicleClass.Description,
            SortOrder = vehicleClass.SortOrder,
            Active = true,
        };

        entity.AddDomainEvent(new TollCatalogDomainEvent(TripEventTypes.TollStationChanged, entity.TollVehicleClassId, entity.Code));
        await context.TollVehicleClasses.AddAsync(entity, cancellationToken);
        AddAuditEvent("CreateTollVehicleClass", entity.TollVehicleClassId);

        await SaveUniqueAsync("ux_toll_vehicle_classes_code", TripErrorCodes.DuplicateTollVehicleClass, cancellationToken);
        return TripMapper.ToVm(entity);
    }

    public async Task UpdateVehicleClassAsync(Guid tollVehicleClassId, TollVehicleClassDto vehicleClass, CancellationToken cancellationToken)
    {
        var entity = await context.TollVehicleClasses
            .AsTracking()
            .FirstOrDefaultAsync(c => c.TollVehicleClassId == tollVehicleClassId, cancellationToken)
            ?? throw new NotFoundException($"{tollVehicleClassId}", nameof(TollVehicleClass));

        entity.Code = vehicleClass.Code;
        entity.Name = vehicleClass.Name;
        entity.Description = vehicleClass.Description;
        entity.SortOrder = vehicleClass.SortOrder;
        entity.AddDomainEvent(new TollCatalogDomainEvent(TripEventTypes.TollStationChanged, entity.TollVehicleClassId, entity.Code));
        AddAuditEvent("UpdateTollVehicleClass", entity.TollVehicleClassId);

        await SaveUniqueAsync("ux_toll_vehicle_classes_code", TripErrorCodes.DuplicateTollVehicleClass, cancellationToken);
    }

    public async Task DeactivateVehicleClassAsync(Guid tollVehicleClassId, CancellationToken cancellationToken)
    {
        var entity = await context.TollVehicleClasses
            .AsTracking()
            .FirstOrDefaultAsync(c => c.TollVehicleClassId == tollVehicleClassId, cancellationToken)
            ?? throw new NotFoundException($"{tollVehicleClassId}", nameof(TollVehicleClass));

        // Deactivated, never deleted: historical estimates reference the class by code.
        entity.Active = false;
        AddAuditEvent("DeactivateTollVehicleClass", entity.TollVehicleClassId);
        await context.SaveChangesAsync(cancellationToken);
    }

    public async Task<TollStationVm> CreateStationAsync(TollStationDto station, CancellationToken cancellationToken)
    {
        var entity = new TollStation
        {
            Name = station.Name,
            Code = station.Code,
            Point = TripGeometryFactory.Point(station.Latitude, station.Longitude),
            Country = station.Country,
            Region = station.Region,
            RoadName = station.RoadName,
            Direction = station.Direction,
            Operator = station.Operator,
            Notes = station.Notes,
            Active = true,
        };

        entity.AddDomainEvent(new TollCatalogDomainEvent(TripEventTypes.TollStationChanged, entity.TollStationId, entity.Code));
        await context.TollStations.AddAsync(entity, cancellationToken);
        AddAuditEvent("CreateTollStation", entity.TollStationId);

        await SaveUniqueAsync(["ux_toll_stations_name_code", "ux_toll_stations_name_nocode"], TripErrorCodes.DuplicateTollStation, cancellationToken);
        return TripMapper.ToVm(entity);
    }

    public async Task UpdateStationAsync(Guid tollStationId, TollStationDto station, CancellationToken cancellationToken)
    {
        var entity = await FindStationAsync(tollStationId, cancellationToken);

        entity.Name = station.Name;
        entity.Code = station.Code;
        entity.Point = TripGeometryFactory.Point(station.Latitude, station.Longitude);
        entity.Country = station.Country;
        entity.Region = station.Region;
        entity.RoadName = station.RoadName;
        entity.Direction = station.Direction;
        entity.Operator = station.Operator;
        entity.Notes = station.Notes;
        entity.AddDomainEvent(new TollCatalogDomainEvent(TripEventTypes.TollStationChanged, entity.TollStationId, entity.Code));
        AddAuditEvent("UpdateTollStation", entity.TollStationId);

        await SaveUniqueAsync(["ux_toll_stations_name_code", "ux_toll_stations_name_nocode"], TripErrorCodes.DuplicateTollStation, cancellationToken);
    }

    public async Task DeactivateStationAsync(Guid tollStationId, CancellationToken cancellationToken)
    {
        var entity = await FindStationAsync(tollStationId, cancellationToken);

        entity.Active = false;
        entity.AddDomainEvent(new TollCatalogDomainEvent(TripEventTypes.TollStationChanged, entity.TollStationId, entity.Code));
        AddAuditEvent("DeactivateTollStation", entity.TollStationId);
        await context.SaveChangesAsync(cancellationToken);
    }

    public async Task<TollTariffVm> CreateTariffAsync(TollTariffDto tariff, CancellationToken cancellationToken)
    {
        _ = await FindStationAsync(tariff.TollStationId, cancellationToken);

        var open = await OpenTariffAsync(tariff.TollStationId, tariff.TollVehicleClassCode, cancellationToken);

        // Overlap check EXCLUDES the row this insert is about to close: closing it at
        // EffectiveFrom - 1 day is exactly how a price change is expressed, and that is not a
        // conflict. Anything else overlapping is a 409 (spec 11 section 6.2).
        var overlapping = await reader.HasOverlappingTariffAsync(
            tariff.TollStationId,
            tariff.TollVehicleClassCode,
            tariff.EffectiveFrom,
            tariff.EffectiveTo,
            open?.TollTariffId,
            cancellationToken);

        if (overlapping)
        {
            throw ConflictException.WithCode(TripErrorCodes.OverlappingTariff);
        }

        if (open is not null)
        {
            if (open.EffectiveFrom >= tariff.EffectiveFrom)
            {
                throw ConflictException.WithCode(TripErrorCodes.OverlappingTariff);
            }

            // CLOSE, never overwrite: the old price stays queryable so a historical trip's
            // estimate remains reproducible (acceptance 21).
            open.EffectiveTo = tariff.EffectiveFrom.AddDays(-1);
        }

        var entity = new TollTariff
        {
            TollStationId = tariff.TollStationId,
            TollVehicleClassCode = tariff.TollVehicleClassCode,
            Amount = tariff.Amount,
            Currency = tariff.Currency,
            EffectiveFrom = tariff.EffectiveFrom,
            EffectiveTo = tariff.EffectiveTo,
        };

        entity.AddDomainEvent(new TollCatalogDomainEvent(
            TripEventTypes.TollTariffChanged, entity.TollTariffId, entity.TollVehicleClassCode));

        await context.TollTariffs.AddAsync(entity, cancellationToken);
        AddAuditEvent("CreateTollTariff", entity.TollTariffId);

        await SaveUniqueAsync("ux_toll_tariffs_station_class_open", TripErrorCodes.OverlappingTariff, cancellationToken);
        return TripMapper.ToVm(entity);
    }

    public async Task UpdateTariffAsync(Guid tollTariffId, TollTariffDto tariff, CancellationToken cancellationToken)
    {
        var entity = await context.TollTariffs
            .AsTracking()
            .FirstOrDefaultAsync(t => t.TollTariffId == tollTariffId, cancellationToken)
            ?? throw new NotFoundException($"{tollTariffId}", nameof(TollTariff));

        var overlapping = await reader.HasOverlappingTariffAsync(
            entity.TollStationId,
            tariff.TollVehicleClassCode,
            tariff.EffectiveFrom,
            tariff.EffectiveTo,
            tollTariffId,
            cancellationToken);

        if (overlapping)
        {
            throw ConflictException.WithCode(TripErrorCodes.OverlappingTariff);
        }

        entity.TollVehicleClassCode = tariff.TollVehicleClassCode;
        entity.Amount = tariff.Amount;
        entity.Currency = tariff.Currency;
        entity.EffectiveFrom = tariff.EffectiveFrom;
        entity.EffectiveTo = tariff.EffectiveTo;
        entity.AddDomainEvent(new TollCatalogDomainEvent(
            TripEventTypes.TollTariffChanged, entity.TollTariffId, entity.TollVehicleClassCode));
        AddAuditEvent("UpdateTollTariff", entity.TollTariffId);

        await SaveUniqueAsync("ux_toll_tariffs_station_class_open", TripErrorCodes.OverlappingTariff, cancellationToken);
    }

    public async Task DeleteTariffAsync(Guid tollTariffId, CancellationToken cancellationToken)
    {
        var entity = await context.TollTariffs
            .AsTracking()
            .FirstOrDefaultAsync(t => t.TollTariffId == tollTariffId, cancellationToken)
            ?? throw new NotFoundException($"{tollTariffId}", nameof(TollTariff));

        entity.AddDomainEvent(new TollCatalogDomainEvent(
            TripEventTypes.TollTariffChanged, entity.TollTariffId, entity.TollVehicleClassCode));
        AddAuditEvent("DeleteTollTariff", entity.TollTariffId);
        context.TollTariffs.Remove(entity);
        await context.SaveChangesAsync(cancellationToken);
    }

    public async Task<TollCatalogImportResultVm> ImportAsync(IReadOnlyCollection<TollCatalogImportRowDto> rows, CancellationToken cancellationToken)
    {
        var errors = new List<TollCatalogImportErrorVm>();
        var stationsCreated = 0;
        var stationsUpdated = 0;
        var tariffsCreated = 0;

        foreach (var row in rows)
        {
            // No partial-failure rollback: a bad row is reported and skipped, the batch continues
            // (the spec 08 geofence-import contract).
            if (string.IsNullOrWhiteSpace(row.StationName) || row.Latitude is null || row.Longitude is null)
            {
                errors.Add(new TollCatalogImportErrorVm(row.RowNumber, "INVALID_STATION", "Station name and coordinates are required."));
                continue;
            }

            try
            {
                var station = await UpsertStationAsync(row, cancellationToken);
                if (station.Created)
                {
                    stationsCreated++;
                }
                else
                {
                    stationsUpdated++;
                }

                if (!string.IsNullOrWhiteSpace(row.VehicleClassCode)
                    && row.Amount is { } amount
                    && !string.IsNullOrWhiteSpace(row.Currency)
                    && row.EffectiveFrom is { } effectiveFrom)
                {
                    // Pre-flight the class code rather than letting the FK reject the insert. Two
                    // reasons: "unknown vehicle class 'IIl'" is an actionable row error where an FK
                    // violation string is not, and it keeps the failed insert from ever entering
                    // the change tracker (see the catch below).
                    if (!await context.TollVehicleClasses
                        .AnyAsync(c => c.Code == row.VehicleClassCode, cancellationToken))
                    {
                        errors.Add(new TollCatalogImportErrorVm(
                            row.RowNumber,
                            "UNKNOWN_VEHICLE_CLASS",
                            $"Vehicle class '{row.VehicleClassCode}' is not in the toll catalog."));
                        continue;
                    }

                    await CreateTariffAsync(
                        new TollTariffDto(station.TollStationId, row.VehicleClassCode, amount, row.Currency, effectiveFrom, null),
                        cancellationToken);
                    tariffsCreated++;
                }
            }
            catch (ConflictException exception)
            {
                // The thrown code, not a hardcoded one: this block also catches a duplicate station
                // and a backdated EffectiveFrom, and labelling those "overlapping tariff" left the
                // administrator with a row-level report that named the wrong problem.
                errors.Add(new TollCatalogImportErrorVm(row.RowNumber, exception.Message, exception.Message));
            }
            catch (DbUpdateException exception)
            {
                // The failed Added entries MUST leave the change tracker. The context is scoped to
                // the request, so a dead insert left behind is replayed by the NEXT row's
                // SaveChangesAsync — one bad row would cascade into failing the entire remainder of
                // the batch, which is precisely the partial-failure behaviour spec 11 §7.6 forbids
                // ("row-level error report, no partial-failure rollback").
                DetachFailedEntries();
                errors.Add(new TollCatalogImportErrorVm(row.RowNumber, "ROW_REJECTED", exception.Message));
            }
        }

        return new TollCatalogImportResultVm(rows.Count, stationsCreated, stationsUpdated, tariffsCreated, errors);
    }

    private async Task<(Guid TollStationId, bool Created)> UpsertStationAsync(TollCatalogImportRowDto row, CancellationToken cancellationToken)
    {
        // Upsert by station code when supplied, otherwise by name + coordinates (spec 11 7.6).
        var existing = string.IsNullOrWhiteSpace(row.StationCode)
            ? await context.TollStations.AsTracking().FirstOrDefaultAsync(s => s.Name == row.StationName, cancellationToken)
            : await context.TollStations.AsTracking().FirstOrDefaultAsync(s => s.Code == row.StationCode, cancellationToken);

        if (existing is not null)
        {
            existing.Name = row.StationName!;
            existing.Point = TripGeometryFactory.Point(row.Latitude!.Value, row.Longitude!.Value);
            existing.Country = row.Country ?? existing.Country;
            existing.Region = row.Region ?? existing.Region;
            existing.RoadName = row.RoadName ?? existing.RoadName;
            existing.Direction = row.Direction ?? existing.Direction;
            await context.SaveChangesAsync(cancellationToken);
            return (existing.TollStationId, false);
        }

        var station = new TollStation
        {
            Name = row.StationName!,
            Code = row.StationCode,
            Point = TripGeometryFactory.Point(row.Latitude!.Value, row.Longitude!.Value),
            Country = row.Country,
            Region = row.Region,
            RoadName = row.RoadName,
            Direction = row.Direction,
            Active = true,
        };

        await context.TollStations.AddAsync(station, cancellationToken);
        await context.SaveChangesAsync(cancellationToken);
        return (station.TollStationId, true);
    }

    private async Task<TollTariff?> OpenTariffAsync(Guid tollStationId, string vehicleClassCode, CancellationToken cancellationToken)
        => await context.TollTariffs.AsTracking().FirstOrDefaultAsync(
            t => t.TollStationId == tollStationId
                && t.TollVehicleClassCode == vehicleClassCode
                && t.EffectiveTo == null,
            cancellationToken);

    private async Task<TollStation> FindStationAsync(Guid tollStationId, CancellationToken cancellationToken)
        => await context.TollStations.AsTracking().FirstOrDefaultAsync(s => s.TollStationId == tollStationId, cancellationToken)
            ?? throw new NotFoundException($"{tollStationId}", nameof(TollStation));

    /// <summary>
    /// Saves, translating a violation of <paramref name="indexName"/> into <paramref name="errorCode"/>.
    /// <para>
    /// The code is a REQUIRED argument rather than a constant. Every caller previously got
    /// <c>TOLL_OVERLAPPING_TARIFF</c>, so a duplicate station name or a duplicate vehicle-class
    /// code was reported to the administrator as an overlapping tariff window — an error about a
    /// different entity entirely, which is unactionable in the §7.6 row-level import report.
    /// </para>
    /// </summary>
    private Task SaveUniqueAsync(string indexName, string errorCode, CancellationToken cancellationToken)
        => SaveUniqueAsync([indexName], errorCode, cancellationToken);

    /// <summary>
    /// The multi-index overload. A station's uniqueness is split across two PARTIAL indexes (coded
    /// and code-less), so a duplicate can violate either one and both must map to the same code.
    /// </summary>
    private async Task SaveUniqueAsync(string[] indexNames, string errorCode, CancellationToken cancellationToken)
    {
        try
        {
            await context.SaveChangesAsync(cancellationToken);
        }
        catch (DbUpdateException exception) when (indexNames.Any(name => UniqueViolation.Matches(exception, name)))
        {
            // The failed Added entries must leave the change tracker, or the next save in this
            // request-scoped context replays the dead insert.
            DetachFailedEntries();
            throw ConflictException.WithCode(errorCode);
        }
    }

    /// <summary>
    /// Toll writes are platform-level and carry no tenant, so the audit row records an empty
    /// account - the actor and the resource are what matter for a reference-data change.
    /// </summary>
    private void AddAuditEvent(string action, Guid resourceId)
        => context.AuditEvents.Add(new AuditEvent(
            Guid.Empty,
            user.PrincipalType.ToString(),
            user.UserId?.ToString() ?? user.ClientId ?? user.SubjectId ?? string.Empty,
            action,
            "TollCatalog",
            resourceId.ToString(),
            "Success",
            null,
            null,
            null,
            null,
            null,
            user.CorrelationId));

    /// <summary>
    /// Clears entries the rejected save left behind so the next row starts from a clean tracker.
    /// <para>
    /// <c>Added</c> entries are detached outright — they were never persisted. <c>Modified</c>
    /// entries are reverted to their original values rather than detached, so a partially-applied
    /// station upsert cannot leak into a later row's save. Without this, one rejected row cascades
    /// into failing the entire remainder of the batch, which is exactly the partial-failure
    /// behaviour spec 11 §7.6 forbids.
    /// </para>
    /// </summary>
    private void DetachFailedEntries()
    {
        foreach (var entry in context.ChangeTracker.Entries().ToList())
        {
            switch (entry.State)
            {
                case EntityState.Added:
                    entry.State = EntityState.Detached;
                    break;
                case EntityState.Modified:
                case EntityState.Deleted:
                    entry.CurrentValues.SetValues(entry.OriginalValues);
                    entry.State = EntityState.Unchanged;
                    break;
                default:
                    break;
            }
        }
    }
}
