namespace StreamingServiceDownloader.Helpers;

public sealed class SoulseekSongRetry(Func<TimeSpan, CancellationToken, Task> delay)
{
    public const int MaximumRetries = 5;
    public const int MaximumAttempts = MaximumRetries + 1;
    public static readonly TimeSpan RetryDelay = TimeSpan.FromSeconds(30);

    public async Task<TResult?> RunAsync<TCandidate, TResult>(IReadOnlyList<TCandidate> candidates,
        Func<TCandidate, int, CancellationToken, Task<TResult?>> download, CancellationToken cancellationToken)
        where TResult : class
    {
        if (candidates.Count == 0) return null;
        for (var attempt = 0; attempt < MaximumAttempts; attempt++)
        {
            cancellationToken.ThrowIfCancellationRequested();
            var result = await download(candidates[attempt % candidates.Count], attempt + 1, cancellationToken);
            if (result is not null) return result;
            if (attempt < MaximumRetries) await delay(RetryDelay, cancellationToken);
        }
        return null;
    }
}
