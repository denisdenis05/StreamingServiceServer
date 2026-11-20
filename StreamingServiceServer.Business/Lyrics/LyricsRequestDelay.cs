using Microsoft.Extensions.Options;

namespace StreamingServiceServer.Business.Lyrics;

public sealed class LyricsRequestDelay(TimeProvider clock, IOptions<LyricsOptions> configuration) : ILyricsRequestDelay
{
    public Task WaitAsync(CancellationToken cancellationToken) => Task.Delay(configuration.Value.RequestInterval, clock, cancellationToken);
}
