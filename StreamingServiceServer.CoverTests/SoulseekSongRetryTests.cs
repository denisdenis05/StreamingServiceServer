using StreamingServiceDownloader.Helpers;
using Xunit;

namespace StreamingServiceServer.CoverTests;

public sealed class SoulseekSongRetryTests
{
    private const string FirstPeer = "first-peer";
    private const string SecondPeer = "second-peer";
    private const string SavedSong = "song.flac";

    [Fact]
    public async Task SongHasOneInitialAttemptAndFiveRetriesThenStops()
    {
        var attempts = new List<int>();
        var delays = new List<TimeSpan>();
        var retry = new SoulseekSongRetry((delay, cancellationToken) => { delays.Add(delay); return Task.CompletedTask; });
        var result = await retry.RunAsync<string, string>([FirstPeer], (candidate, attempt, cancellationToken) =>
        {
            Assert.Equal(FirstPeer, candidate);
            attempts.Add(attempt);
            return Task.FromResult<string?>(null);
        }, default);
        Assert.Null(result);
        Assert.Equal(Enumerable.Range(1, SoulseekSongRetry.MaximumAttempts), attempts);
        Assert.Equal(SoulseekSongRetry.MaximumRetries, delays.Count);
        Assert.All(delays, delay => Assert.Equal(SoulseekSongRetry.RetryDelay, delay));
    }

    [Fact]
    public async Task SongTriesOtherPeersAndCyclesWhenFewerThanSixCandidatesExist()
    {
        var peers = new List<string>();
        var retry = new SoulseekSongRetry((delay, cancellationToken) => Task.CompletedTask);
        await retry.RunAsync<string, string>([FirstPeer, SecondPeer], (candidate, attempt, cancellationToken) =>
        {
            peers.Add(candidate);
            return Task.FromResult<string?>(null);
        }, default);
        Assert.Equal([FirstPeer, SecondPeer, FirstPeer, SecondPeer, FirstPeer, SecondPeer], peers);
    }

    [Fact]
    public async Task SuccessfulSongStopsRetriesImmediately()
    {
        var attempts = 0;
        var retry = new SoulseekSongRetry((delay, cancellationToken) => throw new InvalidOperationException("Successful downloads must not wait."));
        var result = await retry.RunAsync([FirstPeer], (candidate, attempt, cancellationToken) =>
        {
            attempts++;
            return Task.FromResult<string?>(SavedSong);
        }, default);
        Assert.Equal(SavedSong, result);
        Assert.Equal(1, attempts);
    }

    [Fact]
    public async Task SuccessOnLastAllowedRetryStillCompletes()
    {
        var retry = new SoulseekSongRetry((delay, cancellationToken) => Task.CompletedTask);
        var result = await retry.RunAsync([FirstPeer], (candidate, attempt, cancellationToken) =>
            Task.FromResult(attempt == SoulseekSongRetry.MaximumAttempts ? SavedSong : null), default);
        Assert.Equal(SavedSong, result);
    }

    [Fact]
    public async Task CancelledSongDoesNotMakeAnotherAttempt()
    {
        using var cancellation = new CancellationTokenSource();
        var attempts = 0;
        var retry = new SoulseekSongRetry((delay, cancellationToken) =>
        {
            cancellation.Cancel();
            cancellationToken.ThrowIfCancellationRequested();
            return Task.CompletedTask;
        });
        await Assert.ThrowsAnyAsync<OperationCanceledException>(() => retry.RunAsync<string, string>([FirstPeer],
            (candidate, attempt, cancellationToken) => { attempts++; return Task.FromResult<string?>(null); }, cancellation.Token));
        Assert.Equal(1, attempts);
    }

    [Fact]
    public async Task DifferentSongsEachReceiveTheirOwnRetryBudget()
    {
        var retry = new SoulseekSongRetry((delay, cancellationToken) => Task.CompletedTask);
        var attempts = new Dictionary<string, int>();
        foreach (var song in new[] { "first-song.flac", "second-song.flac" })
            await retry.RunAsync<string, string>([song], (candidate, attempt, cancellationToken) =>
            {
                attempts[candidate] = attempt;
                return Task.FromResult<string?>(null);
            }, default);
        Assert.All(attempts.Values, count => Assert.Equal(SoulseekSongRetry.MaximumAttempts, count));
    }

    [Fact]
    public async Task EmptySearchDoesNotAttemptDownloadOrWait()
    {
        var retry = new SoulseekSongRetry((delay, cancellationToken) => throw new InvalidOperationException("No candidates."));
        Assert.Null(await retry.RunAsync<string, string>([],
            (candidate, attempt, cancellationToken) => throw new InvalidOperationException("No candidates."), default));
    }
}
