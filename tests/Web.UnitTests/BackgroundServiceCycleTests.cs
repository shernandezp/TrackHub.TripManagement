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

using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Logging.Abstractions;
using Common.Domain.Constants;
using Moq;
using TrackHub.TripManagement.Application.Trips.Services.Interfaces;
using TrackHub.TripManagement.Domain.Interfaces;
using TrackHub.TripManagement.Web.BackgroundServices;

namespace Web.UnitTests;

/// <summary>
/// One cycle of each background service. Neither had any test.
/// <para>
/// <b>SVD-11, the on-work-only recorder.</b> A <c>BackgroundJobRun</c> row is written ONLY when the
/// cycle actually did something. This is not a micro-optimisation: <c>/status</c> reads the LAST run
/// of each job key, and if these jobs recorded every cycle then the row would tick over every 5 and
/// 15 minutes regardless of whether anything worked, so a genuinely broken job would look perfectly
/// healthy. With the rule in place an OLD timestamp is the healthy steady state for a fleet with no
/// trips in progress, and <c>/status</c> renders it neutrally.
/// </para>
/// <para>
/// The second rule is that recording is BEST EFFORT. The Manager call that writes the row is a
/// cross-service HTTP hop; letting it fail the cycle would mean a Manager outage silently stops
/// every ETA refresh in the platform — the observability tail wagging the dog.
/// </para>
/// </summary>
[TestFixture]
public class BackgroundServiceCycleTests
{
    // ----- ETA refresh --------------------------------------------------------------------------

    [Test]
    public async Task EtaRefresh_WithNoWorkDone_WritesNoJobRunRow()
    {
        // The steady state for a fleet with nothing in progress. A row here would turn the job's
        // /status entry into a heartbeat and destroy its diagnostic value.
        var harness = new CycleHarness(refreshed: 0);

        await harness.EtaService().RunOnceAsync(CancellationToken.None);

        harness.Recorder.Verify(r => r.RecordAsync(
            It.IsAny<string>(), It.IsAny<Guid?>(), It.IsAny<string?>(), It.IsAny<string>(), It.IsAny<string>(),
            It.IsAny<DateTimeOffset>(), It.IsAny<DateTimeOffset>(), It.IsAny<string?>(), It.IsAny<string?>(),
            It.IsAny<CancellationToken>()), Times.Never);
    }

    [Test]
    public async Task EtaRefresh_WithWorkDone_RecordsTheRunUnderItsOwnJobKey()
    {
        var harness = new CycleHarness(refreshed: 7);

        await harness.EtaService().RunOnceAsync(CancellationToken.None);

        // The payload counts both kinds of work the cycle does — ETAs refreshed and trips closed by
        // the auto-completion sweep, which shares this job key rather than adding one (spec 11a §5.2).
        harness.Recorder.Verify(r => r.RecordAsync(
            BackgroundJobKeys.TripEtaRefresh, null, "7/0", It.IsAny<string>(), "Succeeded",
            It.IsAny<DateTimeOffset>(), It.IsAny<DateTimeOffset>(), null, null,
            It.IsAny<CancellationToken>()), Times.Once);
    }

    /// <summary>
    /// The sweep alone counts as work. A cycle that refreshed no ETA but auto-completed a trip whose
    /// tracker went dark has genuinely done something, and SVD-11's on-work-only rule must record it
    /// — otherwise the one job that closes those trips leaves no trace it ever ran.
    /// </summary>
    [Test]
    public async Task EtaRefresh_WithOnlyAnAutoCompletion_StillRecordsTheRun()
    {
        var harness = new CycleHarness(refreshed: 0, completed: 2);

        await harness.EtaService().RunOnceAsync(CancellationToken.None);

        harness.Recorder.Verify(r => r.RecordAsync(
            BackgroundJobKeys.TripEtaRefresh, null, "0/2", It.IsAny<string>(), "Succeeded",
            It.IsAny<DateTimeOffset>(), It.IsAny<DateTimeOffset>(), null, null,
            It.IsAny<CancellationToken>()), Times.Once);
    }

    [Test]
    public async Task EtaRefresh_WhenTheRecorderFails_TheCycleStillSucceeds()
    {
        // A Manager outage must not take the ETA job down with it: the ETAs were already written.
        var harness = new CycleHarness(refreshed: 3);
        harness.Recorder
            .Setup(r => r.RecordAsync(
                It.IsAny<string>(), It.IsAny<Guid?>(), It.IsAny<string?>(), It.IsAny<string>(), It.IsAny<string>(),
                It.IsAny<DateTimeOffset>(), It.IsAny<DateTimeOffset>(), It.IsAny<string?>(), It.IsAny<string?>(),
                It.IsAny<CancellationToken>()))
            .ThrowsAsync(new HttpRequestException("Manager is unreachable"));

        Assert.DoesNotThrowAsync(() => harness.EtaService().RunOnceAsync(CancellationToken.None));
        await Task.CompletedTask;
    }

    [Test]
    public void EtaRefresh_AFailureInTheWorkItselfSurfaces()
    {
        // The complement: only the RECORDING is swallowed. A broken refresh must reach the outer
        // loop's log rather than being quietly absorbed and reported as a healthy cycle.
        var harness = new CycleHarness(refreshed: 0);
        harness.EtaServiceMock
            .Setup(s => s.RefreshEtasAsync(It.IsAny<CancellationToken>()))
            .ThrowsAsync(new InvalidOperationException("the database is gone"));

        Assert.ThrowsAsync<InvalidOperationException>(
            () => harness.EtaService().RunOnceAsync(CancellationToken.None));
    }

    [Test]
    public async Task EtaRefresh_WithNoWorkDone_DoesNotEvenResolveTheRecorder()
    {
        // Resolving the recorder is a scoped service activation and, in the deployment, an
        // authenticated Manager client. A no-work cycle should cost nothing at all.
        var harness = new CycleHarness(refreshed: 0);

        await harness.EtaService().RunOnceAsync(CancellationToken.None);

        Assert.That(harness.RecorderResolutions, Is.Zero);
    }

    // ----- Schedule reminder --------------------------------------------------------------------

    [Test]
    public async Task ScheduleReminder_WithNoReminderRaised_WritesNoJobRunRow()
    {
        var harness = new CycleHarness(raised: 0);

        await harness.ReminderService().RunOnceAsync(CancellationToken.None);

        harness.Recorder.Verify(r => r.RecordAsync(
            It.IsAny<string>(), It.IsAny<Guid?>(), It.IsAny<string?>(), It.IsAny<string>(), It.IsAny<string>(),
            It.IsAny<DateTimeOffset>(), It.IsAny<DateTimeOffset>(), It.IsAny<string?>(), It.IsAny<string?>(),
            It.IsAny<CancellationToken>()), Times.Never);
    }

    [Test]
    public async Task ScheduleReminder_WithAReminderRaised_RecordsTheRunUnderItsOwnJobKey()
    {
        // Its OWN key: sharing a key with the ETA job would let one job's activity mask the
        // other's silence on /status.
        var harness = new CycleHarness(raised: 2);

        await harness.ReminderService().RunOnceAsync(CancellationToken.None);

        harness.Recorder.Verify(r => r.RecordAsync(
            BackgroundJobKeys.TripScheduleReminder, null, "2", It.IsAny<string>(), "Succeeded",
            It.IsAny<DateTimeOffset>(), It.IsAny<DateTimeOffset>(), null, null,
            It.IsAny<CancellationToken>()), Times.Once);
    }

    [Test]
    public void ScheduleReminder_WhenTheRecorderFails_TheCycleStillSucceeds()
    {
        var harness = new CycleHarness(raised: 5);
        harness.Recorder
            .Setup(r => r.RecordAsync(
                It.IsAny<string>(), It.IsAny<Guid?>(), It.IsAny<string?>(), It.IsAny<string>(), It.IsAny<string>(),
                It.IsAny<DateTimeOffset>(), It.IsAny<DateTimeOffset>(), It.IsAny<string?>(), It.IsAny<string?>(),
                It.IsAny<CancellationToken>()))
            .ThrowsAsync(new HttpRequestException("Manager is unreachable"));

        Assert.DoesNotThrowAsync(() => harness.ReminderService().RunOnceAsync(CancellationToken.None));
    }

    [Test]
    public async Task ScheduleReminder_NeverAsksTheEtaServiceToRefreshAnything()
    {
        // The two jobs share ITripEtaService; crossing the wires would make the 15-minute reminder
        // job silently drive the 5-minute ETA path as well.
        var harness = new CycleHarness(raised: 1);

        await harness.ReminderService().RunOnceAsync(CancellationToken.None);

        harness.EtaServiceMock.Verify(s => s.RefreshEtasAsync(It.IsAny<CancellationToken>()), Times.Never);
    }

    /// <summary>
    /// A real <see cref="IServiceScopeFactory"/> over a real container, so the services resolve
    /// exactly as they do in <c>Program.cs</c> — and so a recorder resolution can be counted.
    /// </summary>
    private sealed class CycleHarness
    {
        private readonly IServiceScopeFactory scopeFactory;

        public CycleHarness(int refreshed = 0, int raised = 0, int completed = 0)
        {
            EtaServiceMock.Setup(s => s.RefreshEtasAsync(It.IsAny<CancellationToken>())).ReturnsAsync(refreshed);
            EtaServiceMock.Setup(s => s.RaiseStartRemindersAsync(It.IsAny<CancellationToken>())).ReturnsAsync(raised);
            AutoCompletionMock.Setup(s => s.SweepAsync(It.IsAny<CancellationToken>())).ReturnsAsync(completed);

            var services = new ServiceCollection();
            services.AddScoped(_ => EtaServiceMock.Object);
            services.AddScoped(_ => AutoCompletionMock.Object);
            services.AddScoped(_ =>
            {
                RecorderResolutions++;
                return Recorder.Object;
            });

            scopeFactory = services.BuildServiceProvider().GetRequiredService<IServiceScopeFactory>();
        }

        public Mock<ITripEtaService> EtaServiceMock { get; } = new();

        public Mock<ITripAutoCompletionService> AutoCompletionMock { get; } = new();

        public Mock<IBackgroundJobRunRecorder> Recorder { get; } = new();

        public int RecorderResolutions { get; private set; }

        public TripEtaRefreshService EtaService()
            => new(scopeFactory, NullLogger<TripEtaRefreshService>.Instance);

        public TripScheduleReminderService ReminderService()
            => new(scopeFactory, NullLogger<TripScheduleReminderService>.Instance);
    }
}
