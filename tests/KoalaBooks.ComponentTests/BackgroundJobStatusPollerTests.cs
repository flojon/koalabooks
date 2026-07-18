using KoalaBooks.Application.Services;
using KoalaBooks.Components.Shared;
using KoalaBooks.Domain.Entities;
using KoalaBooks.Domain.Enums;
using Microsoft.AspNetCore.Components;
using Microsoft.Extensions.DependencyInjection;
using NSubstitute.ReceivedExtensions;

namespace KoalaBooks.ComponentTests;

public class BackgroundJobStatusPollerTests : BunitContext
{
    private readonly IBackgroundJobRunService _service = Substitute.For<IBackgroundJobRunService>();

    public BackgroundJobStatusPollerTests()
    {
        Services.AddSingleton(_service);
    }

    [Fact]
    public void CompletedRun_InvokesCallbackAndAcknowledges()
    {
        var run = new BackgroundJobRun { Id = 1, JobType = BackgroundJobType.SieImport, Status = BackgroundJobStatus.Completed, CreatedAt = DateTime.UtcNow };
        _service.GetOpenRunsAsync(BackgroundJobType.SieImport).Returns([run]);

        BackgroundJobRun? completed = null;
        var cut = Render<BackgroundJobStatusPoller>(parameters => parameters
            .Add(p => p.JobType, BackgroundJobType.SieImport)
            .Add(p => p.OnRunCompleted, EventCallback.Factory.Create<BackgroundJobRun>(this, r => completed = r)));

        cut.WaitForAssertion(() => Assert.NotNull(completed), TimeSpan.FromSeconds(2));
        Assert.Equal(1, completed!.Id);
        _ = _service.Received(1).AcknowledgeAsync(1);
    }

    [Fact]
    public void OpenNonStaleRun_KeepsPollingWithoutInvokingCallback()
    {
        // PollInterval overridden to 20ms so the timer actually ticks within this test's
        // lifetime — the real 5s default would make this test impractically slow. This
        // is what distinguishes "still polling" from "gave up": if UpdatePolling had
        // wrongly decided this run is stale (or terminal), GetOpenRunsAsync would only
        // ever be called once, from OnInitializedAsync, and this assertion would time out.
        var run = new BackgroundJobRun { Id = 2, JobType = BackgroundJobType.SieImport, Status = BackgroundJobStatus.Running, CreatedAt = DateTime.UtcNow };
        _service.GetOpenRunsAsync(BackgroundJobType.SieImport).Returns([run]);

        var invoked = false;
        var cut = Render<BackgroundJobStatusPoller>(parameters => parameters
            .Add(p => p.JobType, BackgroundJobType.SieImport)
            .Add(p => p.PollInterval, TimeSpan.FromMilliseconds(20))
            .Add(p => p.OnRunCompleted, EventCallback.Factory.Create<BackgroundJobRun>(this, _ => invoked = true)));

        // NSubstitute 6.0.0's Quantity has no AtLeast(int) factory; Within(min, int.MaxValue)
        // is the equivalent open-ended lower bound.
        cut.WaitForAssertion(
            () => _service.Received(Quantity.Within(2, int.MaxValue)).GetOpenRunsAsync(BackgroundJobType.SieImport),
            TimeSpan.FromSeconds(2));
        Assert.False(invoked);
        _ = _service.DidNotReceive().AcknowledgeAsync(Arg.Any<int>());
    }

    [Fact]
    public async Task StaleRun_NeverStartsPollingAgainAfterInitialCheck()
    {
        // Older than StaleAfter at render time, so UpdatePolling's very first call (from
        // the initial PollAsync in OnInitializedAsync) must decide not to create a timer
        // at all. PollInterval is still overridden to 20ms — if UpdatePolling wrongly
        // scheduled a timer anyway, waiting past several intervals would catch it as a
        // second GetOpenRunsAsync call.
        var run = new BackgroundJobRun { Id = 3, JobType = BackgroundJobType.SieImport, Status = BackgroundJobStatus.Running, CreatedAt = DateTime.UtcNow.AddMinutes(-20) };
        _service.GetOpenRunsAsync(BackgroundJobType.SieImport).Returns([run]);

        var invoked = false;
        Render<BackgroundJobStatusPoller>(parameters => parameters
            .Add(p => p.JobType, BackgroundJobType.SieImport)
            .Add(p => p.StaleAfter, TimeSpan.FromMinutes(10))
            .Add(p => p.PollInterval, TimeSpan.FromMilliseconds(20))
            .Add(p => p.OnRunCompleted, EventCallback.Factory.Create<BackgroundJobRun>(this, _ => invoked = true)));

        await Task.Delay(150);
        _ = _service.Received(1).GetOpenRunsAsync(BackgroundJobType.SieImport);
        Assert.False(invoked);
    }

    [Fact]
    public void NoOpenRuns_CallsGetOpenRunsOnceOnInit()
    {
        _service.GetOpenRunsAsync(BackgroundJobType.SieImport).Returns([]);

        Render<BackgroundJobStatusPoller>(parameters => parameters
            .Add(p => p.JobType, BackgroundJobType.SieImport)
            .Add(p => p.OnRunCompleted, EventCallback.Factory.Create<BackgroundJobRun>(this, _ => { })));

        _ = _service.Received(1).GetOpenRunsAsync(BackgroundJobType.SieImport);
    }
}
