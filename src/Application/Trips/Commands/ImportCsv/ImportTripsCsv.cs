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

using System.Globalization;
using Common.Application.Interfaces;
using Microsoft.Extensions.Logging;
using TrackHub.TripManagement.Application.Common;
using TrackHub.TripManagement.Application.Trips.Services.Interfaces;

namespace TrackHub.TripManagement.Application.Trips.Commands.ImportCsv;

/// <summary>
/// Bulk trip planning from a spreadsheet — the input side of zero-touch (spec 11a §9.1).
/// <para>
/// A transportation company plans a whole week per vehicle at once. Without this the module is
/// coherent only in theory: a dispatcher who must not click Start per trip would still be creating
/// three hundred trips a week one dialog at a time.
/// </para>
/// <para>
/// This is the PORTAL command and it is a portal user's, not a service client's: rows are subject to
/// the caller's own group visibility, one transporter at a time, exactly as the create dialog is.
/// The partner-facing <c>ImportTrips</c> stays separate and stays ServiceClient-only (§9.2).
/// </para>
/// <para>
/// <b>No batch failure.</b> Every row reports its own outcome and the good ones land — rolling back
/// a 300-row plan because row 174 names a place that was renamed would make the operator re-run the
/// whole file to discover that row 175 is wrong too (the toll-catalog import precedent).
/// </para>
/// </summary>
[Authorize(Resource = Resources.Trips, Action = Actions.Write)]
[RequireFeature(FeatureKeys.TripManagement)]
// Enforcement: the handler derives the caller's own account and passes it to the reader/writer,
// which filters every row on it (TripVisibility is the single visibility resolver - spec 11).
[AccountScopeEnforcedInHandler]
public readonly record struct ImportTripsCsvCommand(string Csv) : IRequest<TripCsvImportResultVm>;

public sealed class ImportTripsCsvCommandHandler(
    ITripWriter writer,
    ITripReader reader,
    ITripStopWriter stopWriter,
    IPlaceReader placeReader,
    ITripStartBackfillService backfillService,
    IManagerValidationClient managerValidationClient,
    ITransporterTollClassStore tollClassStore,
    IUserReader userReader,
    IUser user,
    ILogger<ImportTripsCsvCommandHandler> logger) : IRequestHandler<ImportTripsCsvCommand, TripCsvImportResultVm>
{
    private const string InvalidRowCode = "TRIP_IMPORT_INVALID_ROW";

    /// <summary>Columns up to and including <c>plannedStart</c> — everything after it is optional.</summary>
    private const int RequiredColumns = 9;

    /// <summary>
    /// A defensive ceiling on one upload. A week of trips for a large fleet is hundreds of rows; a
    /// file with tens of thousands is a mistake, and processing it row by row would hold a request
    /// open for minutes.
    /// </summary>
    private const int MaxRows = 2000;

    private Guid UserId { get; } = TripVisibility.RequireUserId(user);

    public async Task<TripCsvImportResultVm> Handle(ImportTripsCsvCommand request, CancellationToken cancellationToken)
    {
        var caller = await userReader.GetUserAsync(UserId, cancellationToken);
        var scopeUserId = TripVisibility.ResolveScopeUserId(user, UserId);

        var places = await placeReader.GetPlacesAsync(caller.AccountId, cancellationToken);
        var transporters = await reader.GetTransporterNamesAsync(caller.AccountId, scopeUserId, cancellationToken);
        var drivers = await reader.GetDriverNamesAsync(caller.AccountId, cancellationToken);

        // Case- and accent-insensitive within the account: "plant 3" and "Plant 3" are the same
        // dock, and a dispatcher retyping a name is not making a new place.
        var placesByName = Index(places, p => p.Name);
        var transportersByName = Index(transporters, t => t.Name);
        var driversByName = Index(drivers, d => d.Name);

        var created = 0;
        var errors = new List<TripCsvImportErrorVm>();
        var rowNumber = 0;
        var rowsRead = 0;

        foreach (var line in request.Csv.Split(['\r', '\n'], StringSplitOptions.RemoveEmptyEntries))
        {
            rowNumber++;

            // Header row, recognised by its first column name rather than by position.
            if (rowNumber == 1 && line.StartsWith("code", StringComparison.OrdinalIgnoreCase))
                continue;

            if (rowsRead >= MaxRows)
            {
                errors.Add(new TripCsvImportErrorVm(rowNumber, InvalidRowCode, $"The file exceeds the {MaxRows}-row limit; split it and import again."));
                break;
            }

            rowsRead++;

            try
            {
                if (await ImportRowAsync(rowNumber, line, caller.AccountId, scopeUserId,
                        placesByName, transportersByName, driversByName, errors, cancellationToken))
                {
                    created++;
                }
            }
            catch (Exception ex)
            {
                logger.LogError(ex, "Trip CSV import failed on row {RowNumber}", rowNumber);
                errors.Add(new TripCsvImportErrorVm(rowNumber, InvalidRowCode, ex.Message));
            }
        }

        return new TripCsvImportResultVm(rowsRead, created, errors);
    }

    private async Task<bool> ImportRowAsync(
        int rowNumber,
        string line,
        Guid accountId,
        Guid? scopeUserId,
        Dictionary<string, PlaceVm> placesByName,
        Dictionary<string, NamedEntityVm> transportersByName,
        Dictionary<string, NamedEntityVm> driversByName,
        List<TripCsvImportErrorVm> errors,
        CancellationToken cancellationToken)
    {
        var fields = SplitRow(line);

        // Through plannedStart, which is column 9 and the last REQUIRED one. Accepting a shorter row
        // and letting it fail on "plannedStart is not a valid date-time" told the operator the wrong
        // thing about a line that was simply cut short.
        if (fields.Length < RequiredColumns)
        {
            errors.Add(new TripCsvImportErrorVm(
                rowNumber, InvalidRowCode, $"Expected at least {RequiredColumns} columns, found {fields.Length}."));
            return false;
        }

        var code = fields[0];
        if (string.IsNullOrWhiteSpace(code))
        {
            errors.Add(new TripCsvImportErrorVm(rowNumber, InvalidRowCode, "code is required."));
            return false;
        }

        if (!transportersByName.TryGetValue(Key(fields[1]), out var transporter))
        {
            // Deliberately the same error whether the vehicle does not exist or is not visible to
            // this dispatcher: the row must not become a way to enumerate another group's fleet.
            errors.Add(new TripCsvImportErrorVm(rowNumber, InvalidRowCode, $"Unknown transporter '{fields[1]}'."));
            return false;
        }

        Guid? driverId = null;
        if (!string.IsNullOrWhiteSpace(fields[2]))
        {
            if (!driversByName.TryGetValue(Key(fields[2]), out var driver))
            {
                errors.Add(new TripCsvImportErrorVm(rowNumber, InvalidRowCode, $"Unknown driver '{fields[2]}'."));
                return false;
            }

            // The SAME rule the create dialog applies: a driver qualifies for a unit only through
            // Manager's assignment. Skipping it here would have made bulk upload the one way to put
            // a driver on a vehicle they are not cleared for (acceptance 2).
            if (!await managerValidationClient.ValidateDriverAssignmentAsync(
                    driver.Id, "Transporter", transporter.Id, cancellationToken))
            {
                errors.Add(new TripCsvImportErrorVm(
                    rowNumber, TripErrorCodes.DriverNotAssignable, $"'{fields[2]}' is not assignable to '{fields[1]}'."));
                return false;
            }

            driverId = driver.Id;
        }

        if (!placesByName.TryGetValue(Key(fields[4]), out var origin))
        {
            errors.Add(new TripCsvImportErrorVm(rowNumber, InvalidRowCode, $"Unknown origin place '{fields[4]}'."));
            return false;
        }

        var destinationNames = fields[5].Split(';', StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries);
        if (destinationNames.Length == 0)
        {
            errors.Add(new TripCsvImportErrorVm(rowNumber, InvalidRowCode, "destinations is required — a trip is a route."));
            return false;
        }

        var destinations = new List<PlaceVm>(destinationNames.Length);
        foreach (var name in destinationNames)
        {
            if (!placesByName.TryGetValue(Key(name), out var destination))
            {
                errors.Add(new TripCsvImportErrorVm(rowNumber, InvalidRowCode, $"Unknown destination place '{name}'."));
                return false;
            }

            destinations.Add(destination);
        }

        var activities = Field(fields, 6).Split(';', StringSplitOptions.TrimEntries);
        var tripType = Field(fields, 7);

        if (!TryParseInstant(Field(fields, 8), out var plannedStartAt))
        {
            errors.Add(new TripCsvImportErrorVm(rowNumber, InvalidRowCode, "plannedStart is not a valid date-time."));
            return false;
        }

        DateTimeOffset? plannedEndAt = null;
        if (!string.IsNullOrWhiteSpace(Field(fields, 9)))
        {
            if (!TryParseInstant(fields[9], out var parsedEnd))
            {
                errors.Add(new TripCsvImportErrorVm(rowNumber, InvalidRowCode, "plannedEnd is not a valid date-time."));
                return false;
            }

            plannedEndAt = parsedEnd;
        }

        DateTimeOffset? startedAt = null;
        if (!string.IsNullOrWhiteSpace(Field(fields, 10)))
        {
            if (!TryParseInstant(fields[10], out var parsedStarted))
            {
                errors.Add(new TripCsvImportErrorVm(rowNumber, InvalidRowCode, "startedAt is not a valid date-time."));
                return false;
            }

            startedAt = parsedStarted;
        }

        var dto = new TripDto(
            code,
            transporter.Id,
            driverId,
            null,
            NullIfEmpty(Field(fields, 11)),
            NullIfEmpty(fields[3]),
            origin.Name,
            origin.Latitude,
            origin.Longitude,
            origin.GeofenceId,
            TripGeometry.DefaultRadiusMeters,
            plannedStartAt,
            plannedEndAt,
            NullIfEmpty(Field(fields, 12)),
            // Resolved from the account's transporter → toll-class rules, exactly as the create
            // dialog does. Left null, a bulk-uploaded trip would have nothing for the toll estimate
            // to price against and the estimate would silently never engage for the whole week.
            await tollClassStore.ResolveClassAsync(accountId, transporter.Id, cancellationToken));

        var trip = await writer.CreateTripAsync(dto, accountId, cancellationToken);

        for (var i = 0; i < destinations.Count; i++)
        {
            await stopWriter.AddStopAsync(trip.TripId, accountId, BuildStop(destinations[i], Activity(activities, i)), cancellationToken);
        }

        // A round trip closes where it began. The return leg is Other, not Unload: parking at the
        // depot is neither loading nor unloading, and labelling it either would put a fictional
        // duration into the dwell reports.
        if (string.Equals(tripType, "round", StringComparison.OrdinalIgnoreCase))
        {
            await stopWriter.AddStopAsync(trip.TripId, accountId, BuildStop(origin, TripStopActivities.Other), cancellationToken);
        }

        // Last, once the route exists: the declaration replays it (spec 11a §5.4). Recorded evidence
        // still wins over the declared instant.
        if (startedAt.HasValue)
        {
            await backfillService.ApplyAsync(trip.TripId, accountId, scopeUserId, startedAt, cancellationToken);
        }

        return true;
    }

    private static TripStopDto BuildStop(PlaceVm place, string activity)
        => new(
            place.Name,
            null,
            null,
            place.Latitude,
            place.Longitude,
            place.GeofenceId,
            TripGeometry.DefaultRadiusMeters,
            activity,
            null,
            null,
            false,
            0,
            null);

    /// <summary>
    /// One activity per destination, in order. A single value applies to every destination — the
    /// common case is a run of deliveries — and a missing one falls back to <c>Unload</c>.
    /// </summary>
    private static string Activity(string[] activities, int index)
    {
        if (activities.Length == 0)
            return TripStopActivities.Unload;

        return TripStopActivities.Normalize(
            index < activities.Length ? activities[index] : activities[0]);
    }

    /// <summary>
    /// RFC 4180 enough for the files a spreadsheet exports: quoted cells may contain commas, and a
    /// doubled quote inside a quoted cell is a literal one. A naive <c>Split(',')</c> tore
    /// "Bogota, Colombia" into two columns and shifted every field after it.
    /// </summary>
    private static string[] SplitRow(string line)
    {
        var fields = new List<string>();
        var current = new System.Text.StringBuilder();
        var quoted = false;

        for (var i = 0; i < line.Length; i++)
        {
            var character = line[i];

            if (quoted)
            {
                if (character != '"')
                {
                    current.Append(character);
                }
                else if (i + 1 < line.Length && line[i + 1] == '"')
                {
                    current.Append('"');
                    i++;
                }
                else
                {
                    quoted = false;
                }

                continue;
            }

            switch (character)
            {
                case '"':
                    quoted = true;
                    break;
                case ',':
                    fields.Add(current.ToString().Trim());
                    current.Clear();
                    break;
                default:
                    current.Append(character);
                    break;
            }
        }

        fields.Add(current.ToString().Trim());
        return [.. fields];
    }

    /// <summary>
    /// Round-trip ("2026-08-03T07:30:00Z") and plain local ("2026-08-03 07:30") both parse. A plain
    /// value is read as UTC rather than as the server's local time: the server's timezone is an
    /// implementation detail no spreadsheet author knows about (the UTC-everywhere decision).
    /// </summary>
    private static bool TryParseInstant(string value, out DateTimeOffset parsed)
        => DateTimeOffset.TryParse(
            value, CultureInfo.InvariantCulture, DateTimeStyles.AssumeUniversal | DateTimeStyles.AdjustToUniversal, out parsed);

    private static string Field(string[] fields, int index)
        => index < fields.Length ? fields[index] : string.Empty;

    private static string? NullIfEmpty(string value) => string.IsNullOrWhiteSpace(value) ? null : value;

    private static string Key(string value) => value.Trim().ToUpperInvariant();

    private static Dictionary<string, T> Index<T>(IReadOnlyCollection<T> items, Func<T, string> name)
    {
        var index = new Dictionary<string, T>(StringComparer.Ordinal);
        foreach (var item in items)
        {
            // First wins. Two places sharing a name is an account-data problem the import cannot
            // resolve; silently picking the later one would make the same file import differently
            // from one day to the next.
            index.TryAdd(Key(name(item)), item);
        }

        return index;
    }
}

public sealed class ImportTripsCsvValidator : AbstractValidator<ImportTripsCsvCommand>
{
    public ImportTripsCsvValidator()
        => RuleFor(v => v.Csv).NotEmpty();
}
