using KoalaBooks.Application.Services;
using KoalaBooks.Components.Shared;
using KoalaBooks.Domain.Entities;
using KoalaBooks.Domain.Enums;
using Microsoft.AspNetCore.Components;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Logging;
using NSubstitute.ReceivedExtensions;

namespace KoalaBooks.ComponentTests;

public class BackgroundJobStatusPollerTests : BunitContext
{
    private readonly IBackgroundJobRunService _service = Substitute.For<IBackgroundJobRunService>();

    public BackgroundJobStatusPollerTests()
    {
        Services.AddSingleton(_service);
        Services.AddSingleton(Substitute.For<ILogger<BackgroundJobStatusPoller>>());
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

    [Fact]
    public void PollTickThrows_DoesNotCrashAndPollingContinues()
    {
        // First call is the initial PollAsync from OnInitializedAsync (unguarded, outside
        // OnPollTick) and must succeed so a timer gets scheduled. The second call happens
        // on the first timer tick, inside OnPollTick's try/catch, and throws — this is the
        // exact path the finding is about. If OnPollTick's catch were missing, the
        // exception would escape the discarded InvokeAsync task, and _isPolling would never
        // be observed to reset from a test's perspective in a way that lets us assert
        // recovery. Subsequent calls succeed again, proving a single bad tick doesn't kill
        // polling or leave the guard stuck.
        var run = new BackgroundJobRun { Id = 4, JobType = BackgroundJobType.SieImport, Status = BackgroundJobStatus.Running, CreatedAt = DateTime.UtcNow };
        var callCount = 0;
        _service.GetOpenRunsAsync(BackgroundJobType.SieImport).Returns(_ =>
        {
            callCount++;
            if (callCount == 2) throw new InvalidOperationException("transient DB failure");
            return [run];
        });

        var invoked = false;
        var cut = Render<BackgroundJobStatusPoller>(parameters => parameters
            .Add(p => p.JobType, BackgroundJobType.SieImport)
            .Add(p => p.PollInterval, TimeSpan.FromMilliseconds(20))
            .Add(p => p.OnRunCompleted, EventCallback.Factory.Create<BackgroundJobRun>(this, _ => invoked = true)));

        // Waiting for a third call proves polling survived the second call's exception:
        // the timer kept firing and _isPolling was reset in the finally block despite the
        // catch (or lack thereof) in the try.
        cut.WaitForAssertion(
            () => _service.Received(Quantity.Within(3, int.MaxValue)).GetOpenRunsAsync(BackgroundJobType.SieImport),
            TimeSpan.FromSeconds(2));
        Assert.False(invoked);
    }

    [Fact]
    public async Task PollNowAsync_CalledDirectly_PollsImmediately()
    {
        _service.GetOpenRunsAsync(BackgroundJobType.SieImport).Returns([]);

        var cut = Render<BackgroundJobStatusPoller>(parameters => parameters
            .Add(p => p.JobType, BackgroundJobType.SieImport)
            .Add(p => p.OnRunCompleted, EventCallback.Factory.Create<BackgroundJobRun>(this, _ => { })));

        _ = _service.Received(1).GetOpenRunsAsync(BackgroundJobType.SieImport);

        await cut.Instance.PollNowAsync();

        _ = _service.Received(2).GetOpenRunsAsync(BackgroundJobType.SieImport);
    }

    [Fact]
    public async Task PollNowAsync_WhileAPollIsInFlight_CoalescesIntoOneMoreGuaranteedPoll()
    {
        // Simulates a host page's PollNowAsync racing an already-in-flight poll (e.g. the
        // timer's tick straddling the moment a host calls PollNowAsync right after
        // enqueuing a job). The guard means this must not start a second, concurrent
        // GetOpenRunsAsync of its own — but the in-flight poll must still be guaranteed to
        // loop once more before releasing the guard, so the caller's trigger isn't
        // silently dropped (the exact "best-effort" gap the guard alone would leave open).
        var callCount = 0;
        var firstCallGate = new TaskCompletionSource<List<BackgroundJobRun>>();
        _service.GetOpenRunsAsync(BackgroundJobType.SieImport).Returns(_ =>
        {
            callCount++;
            return callCount == 1 ? firstCallGate.Task : Task.FromResult(new List<BackgroundJobRun>());
        });

        var cut = Render<BackgroundJobStatusPoller>(parameters => parameters
            .Add(p => p.JobType, BackgroundJobType.SieImport)
            .Add(p => p.OnRunCompleted, EventCallback.Factory.Create<BackgroundJobRun>(this, _ => { })));

        // OnInitializedAsync's poll is suspended awaiting firstCallGate, still holding the
        // guard — this call must not issue its own GetOpenRunsAsync round-trip.
        await cut.Instance.PollNowAsync();
        Assert.Equal(1, callCount);

        firstCallGate.SetResult([]);

        // The still-in-flight initial poll must observe the coalesced request and loop
        // once more instead of releasing the guard with it unserviced.
        cut.WaitForAssertion(() => Assert.Equal(2, callCount), TimeSpan.FromSeconds(2));
    }

    [Fact]
    public async Task PollNowAsync_AfterDisposal_DoesNotPollAgain()
    {
        _service.GetOpenRunsAsync(BackgroundJobType.SieImport).Returns([]);

        var cut = Render<BackgroundJobStatusPoller>(parameters => parameters
            .Add(p => p.JobType, BackgroundJobType.SieImport)
            .Add(p => p.OnRunCompleted, EventCallback.Factory.Create<BackgroundJobRun>(this, _ => { })));

        ((IDisposable)cut.Instance).Dispose();

        await cut.Instance.PollNowAsync();

        _ = _service.Received(1).GetOpenRunsAsync(BackgroundJobType.SieImport);
    }
}
