using StreamingServiceDownloader.Models.Slskd;

namespace StreamingServiceDownloader.Helpers;

public sealed class SoulseekFileRetrySchedule
{
    public static readonly TimeSpan MinimumRetryDelay = TimeSpan.FromSeconds(30);
    public static readonly TimeSpan MaximumRetryDelay = TimeSpan.FromMinutes(5);
    private readonly Dictionary<string, (DateTimeOffset NextAttempt, TimeSpan Delay)> _attempts = new();
    private DateTimeOffset _nextSubmission;

    public SlskdFileDownloadRequest? TakeNext(IReadOnlyList<SlskdFileDownloadRequest> missingFiles,
        DateTimeOffset now)
    {
        if (now < _nextSubmission) return null;
        foreach (var file in missingFiles)
        {
            var key = SlskdSourceIdentity.NormalizePath(file.Filename);
            var previous = _attempts.GetValueOrDefault(key);
            if (now < previous.NextAttempt) continue;
            var delay = previous.Delay == TimeSpan.Zero ? MinimumRetryDelay
                : TimeSpan.FromTicks(Math.Min(previous.Delay.Ticks * 2, MaximumRetryDelay.Ticks));
            _attempts[key] = (now + delay, delay);
            _nextSubmission = now + MinimumRetryDelay;
            return file;
        }
        return null;
    }
}
