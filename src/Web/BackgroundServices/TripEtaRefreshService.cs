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

using TrackHub.TripManagement.Application.Trips.Services.Interfaces;

namespace TrackHub.TripManagement.Web.BackgroundServices;

/// <summary>
/// Recomputes ETAs for in-progress trips every 5 minutes, raises <c>TripDelayed</c> once per stop,
/// and sweeps for trips that are finished but whose tracker went dark. All three are driven by
/// elapsed time rather than an inbound request, so none of them can ride the SyncWorker-driven
/// detection path.
/// <para>
/// The auto-completion sweep deliberately shares this cycle and this job key instead of adding a
/// <c>BackgroundJobKeys</c> entry: it is a fallback for a case detection cannot see (no more fixes
/// are coming), not a schedule of its own (spec 11a §5.2).
/// </para>
/// <para>
/// <b>On-work-only recorder (SVD-11):</b> a <c>BackgroundJobRun</c> row is written ONLY when the
/// cycle actually did something — refreshed an ETA or closed a trip. An old row for
/// <c>trip-eta-refresh</c> is therefore the healthy steady state — a fleet with no trips in progress
/// is not a stuck job — and <c>/status</c> must render it neutrally rather than as a staleness alarm.
/// </para>
/// </summary>
public sealed class TripEtaRefreshService(
    IServiceScopeFactory scopeFactory,
    ILogger<TripEtaRefreshService> logger) : BackgroundService
{
    private static readonly TimeSpan Interval = TimeSpan.FromMinutes(5);
    private static readonly TimeSpan StartupDelay = TimeSpan.FromSeconds(45);

    protected override async Task ExecuteAsync(CancellationToken stoppingToken)
    {
        await Task.Delay(StartupDelay, stoppingToken);

        while (!stoppingToken.IsCancellationRequested)
        {
            try
            {
                await RunOnceAsync(stoppingToken);
            }
            catch (OperationCanceledException)
            {
                return;
            }
            catch (Exception ex)
            {
                logger.LogError(ex, "Trip ETA refresh cycle failed");
            }

            await Task.Delay(Interval, stoppingToken);
        }
    }

    // internal, not private, ONLY so the cycle can be driven directly by Web.UnitTests: ExecuteAsync
    // is wrapped in a 45/90 s startup delay and an infinite loop, so the on-work-only rule (SVD-11)
    // is otherwise unreachable from a test. Behaviour is unchanged.
    internal async Task RunOnceAsync(CancellationToken cancellationToken)
    {
        var startedAt = DateTimeOffset.UtcNow;

        using var scope = scopeFactory.CreateScope();
        var etaService = scope.ServiceProvider.GetRequiredService<ITripEtaService>();
        var refreshed = await etaService.RefreshEtasAsync(cancellationToken);

        var autoCompletion = scope.ServiceProvider.GetRequiredService<ITripAutoCompletionService>();
        var completed = await autoCompletion.SweepAsync(cancellationToken);

        if (refreshed == 0 && completed == 0)
            return;

        logger.LogInformation(
            "Trip ETA refresh updated {Refreshed} stop ETA(s) and auto-completed {Completed} trip(s)",
            refreshed, completed);

        // Recording is best-effort: a Manager outage must not take the job down with it.
        try
        {
            var recorder = scope.ServiceProvider.GetRequiredService<IBackgroundJobRunRecorder>();
            await recorder.RecordAsync(
                BackgroundJobKeys.TripEtaRefresh,
                null,
                $"{refreshed}/{completed}",
                $"trip-eta-refresh:{startedAt:yyyyMMddHHmmssfff}",
                "Succeeded",
                startedAt,
                DateTimeOffset.UtcNow,
                null,
                null,
                cancellationToken);
        }
        catch (Exception ex)
        {
            logger.LogError(ex, "Failed to record background job run for {JobKey}", BackgroundJobKeys.TripEtaRefresh);
        }
    }
}
