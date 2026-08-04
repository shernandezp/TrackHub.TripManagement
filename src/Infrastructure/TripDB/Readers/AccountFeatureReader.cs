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

using System.Text.Json;
using Common.Application.Exceptions;
using Common.Domain.Constants;

namespace TrackHub.TripManagement.Infrastructure.TripDB.Readers;

/// <summary>
/// Reads the Manager-owned <c>app.account_features</c> table for explicit feature checks and for
/// this module's per-account job configuration in <c>ConfigurationJson</c>.
/// </summary>
public sealed class AccountFeatureReader(IApplicationDbContext context) : IAccountFeatureReader
{
    public async Task EnsureFeatureEnabledAsync(Guid accountId, string featureKey, CancellationToken cancellationToken)
    {
        if (!await IsFeatureEnabledAsync(accountId, featureKey, cancellationToken))
        {
            throw new FeatureDisabledException(featureKey, accountId);
        }
    }

    public async Task<bool> IsFeatureEnabledAsync(Guid accountId, string featureKey, CancellationToken cancellationToken)
    {
        if (accountId == Guid.Empty || string.IsNullOrWhiteSpace(featureKey))
        {
            return false;
        }

        var now = DateTimeOffset.UtcNow;
        return await context.AccountFeatures.AnyAsync(x =>
            x.AccountId == accountId
            && x.FeatureKey == featureKey
            && x.Enabled
            && (!x.EffectiveFrom.HasValue || x.EffectiveFrom <= now)
            && (!x.EffectiveTo.HasValue || x.EffectiveTo >= now),
            cancellationToken);
    }

    public async Task<IReadOnlyCollection<Guid>> GetEnabledAccountIdsAsync(string featureKey, CancellationToken cancellationToken)
    {
        var now = DateTimeOffset.UtcNow;
        return await context.AccountFeatures
            .Where(x => x.FeatureKey == featureKey
                && x.Enabled
                && (!x.EffectiveFrom.HasValue || x.EffectiveFrom <= now)
                && (!x.EffectiveTo.HasValue || x.EffectiveTo >= now))
            .Select(x => x.AccountId)
            .Distinct()
            .ToListAsync(cancellationToken);
    }

    public async Task<TripAccountConfigVm> GetAccountConfigAsync(Guid accountId, CancellationToken cancellationToken)
    {
        var configurationJson = await context.AccountFeatures
            .Where(x => x.AccountId == accountId && x.FeatureKey == FeatureKeys.TripManagement)
            .Select(x => x.ConfigurationJson)
            .FirstOrDefaultAsync(cancellationToken);

        if (string.IsNullOrWhiteSpace(configurationJson))
        {
            return TripAccountConfigVm.Default;
        }

        try
        {
            using var document = JsonDocument.Parse(configurationJson);
            var root = document.RootElement;

            return new TripAccountConfigVm(
                ReadInt(root, "delayThresholdMinutes", TripAccountConfigVm.DefaultDelayThresholdMinutes),
                ReadInt(root, "scheduleLeadMinutes", TripAccountConfigVm.DefaultScheduleLeadMinutes),
                ReadDouble(root, "tollMatchToleranceMeters", TripAccountConfigVm.DefaultTollMatchToleranceMeters),
                ReadInt(root, "activationLeadMinutes", TripAccountConfigVm.DefaultActivationLeadMinutes),
                ReadInt(root, "backfillLookbackHours", TripAccountConfigVm.DefaultBackfillLookbackHours),
                ReadInt(root, "finalStopCompletionMinutes", TripAccountConfigVm.DefaultFinalStopCompletionMinutes),
                ReadInt(root, "overdueGraceMinutes", TripAccountConfigVm.DefaultOverdueGraceMinutes),
                ReadBool(root, "autoLifecycle", TripAccountConfigVm.DefaultAutoLifecycle));
        }
        catch (JsonException)
        {
            // Operator-supplied JSON: malformed configuration falls back to the documented
            // defaults rather than taking every trip job down for the account.
            return TripAccountConfigVm.Default;
        }
    }

    private static int ReadInt(JsonElement root, string propertyName, int fallback)
        => root.ValueKind == JsonValueKind.Object
            && root.TryGetProperty(propertyName, out var value)
            && value.ValueKind == JsonValueKind.Number
            && value.TryGetInt32(out var parsed)
            && parsed > 0
                ? parsed
                : fallback;

    /// <summary>
    /// Booleans get their own reader rather than riding the <c>&gt; 0</c> numeric guard: <c>false</c>
    /// is a legitimate value an operator sets on purpose, so "absent" and "off" must stay
    /// distinguishable. Anything that is not a JSON boolean falls back to the default.
    /// </summary>
    private static bool ReadBool(JsonElement root, string propertyName, bool fallback)
        => root.ValueKind == JsonValueKind.Object
            && root.TryGetProperty(propertyName, out var value)
            && value.ValueKind is JsonValueKind.True or JsonValueKind.False
                ? value.GetBoolean()
                : fallback;

    private static double ReadDouble(JsonElement root, string propertyName, double fallback)
        => root.ValueKind == JsonValueKind.Object
            && root.TryGetProperty(propertyName, out var value)
            && value.ValueKind == JsonValueKind.Number
            && value.TryGetDouble(out var parsed)
            && parsed > 0d
                ? parsed
                : fallback;
}
