using Microsoft.Extensions.Options;
using StreamingServiceServer.Business.Lyrics;
using Xunit;

namespace StreamingServiceServer.CoverTests;

public sealed class LyricsRequestDelayTests
{
    [Fact]
    public async Task ConfiguredIntervalUsesInjectedClockAndWaitsUntilItsDeadline()
    {
        var clock = new ManualDelayClock();
        var configuration = new LyricsOptions { RequestInterval = LyricsTestData.ProviderCooldown };
        var delay = new LyricsRequestDelay(clock, Options.Create(configuration));
        var waiting = delay.WaitAsync(CancellationToken.None);
        Assert.Equal(configuration.RequestInterval, clock.Timer!.DueTime);
        Assert.False(waiting.IsCompleted);
        clock.Advance(configuration.RequestInterval - LyricsTestData.RetrySafetyMargin);
        Assert.False(waiting.IsCompleted);
        clock.Advance(LyricsTestData.RetrySafetyMargin);
        await waiting;
    }

    [Fact]
    public async Task CancellationStopsWaitingAndDisposesItsTimer()
    {
        var clock = new ManualDelayClock();
        using var cancellation = new CancellationTokenSource();
        var delay = new LyricsRequestDelay(clock, Options.Create(new LyricsOptions()));
        var waiting = delay.WaitAsync(cancellation.Token);
        cancellation.Cancel();
        await Assert.ThrowsAnyAsync<OperationCanceledException>(() => waiting);
        Assert.True(clock.Timer!.Disposed);
    }

    private sealed class ManualDelayClock : TimeProvider
    {
        private TimeSpan elapsed;
        public ManualDelayTimer? Timer { get; private set; }
        public override ITimer CreateTimer(TimerCallback callback, object? state, TimeSpan dueTime, TimeSpan period)
        {
            Timer = new ManualDelayTimer(callback, state, elapsed, dueTime);
            return Timer;
        }
        public void Advance(TimeSpan duration)
        {
            elapsed += duration;
            Timer?.FireIfDue(elapsed);
        }
    }

    private sealed class ManualDelayTimer(TimerCallback callback, object? state, TimeSpan startedAt, TimeSpan dueTime) : ITimer
    {
        public TimeSpan DueTime { get; private set; } = dueTime;
        public bool Disposed { get; private set; }
        public bool Change(TimeSpan changedDueTime, TimeSpan period) { DueTime = changedDueTime; return !Disposed; }
        public void FireIfDue(TimeSpan elapsed)
        {
            if (!Disposed && elapsed >= startedAt + DueTime) callback(state);
        }
        public void Dispose() => Disposed = true;
        public ValueTask DisposeAsync() { Dispose(); return ValueTask.CompletedTask; }
    }
}
