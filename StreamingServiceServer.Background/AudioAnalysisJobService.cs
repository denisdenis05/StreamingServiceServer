using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Hosting;
using Microsoft.Extensions.Logging;
using Microsoft.Extensions.Options;
using StreamingServiceServer.Business.AudioAnalysis;
using StreamingServiceServer.Data.Models;

namespace StreamingServiceServer.Background;

public sealed class AudioAnalysisJobOptions
{
    public const string ConfigurationSection = "AudioAnalysis:Worker";
    public int DiscoveryBatchSize { get; set; } = 32;
    public TimeSpan PollInterval { get; set; } = TimeSpan.FromSeconds(5);
    public TimeSpan SettingsCheckInterval { get; set; } = TimeSpan.FromSeconds(2);
    public TimeSpan LeaseLifetime { get; set; } = TimeSpan.FromMinutes(12);
    public TimeSpan OperationTimeout { get; set; } = TimeSpan.FromMinutes(10);
    public TimeSpan ProviderFailureCooldown { get; set; } = TimeSpan.FromMinutes(1);
    public TimeSpan InitialRetryDelay { get; set; } = TimeSpan.FromMinutes(1);
    public TimeSpan MaximumRetryDelay { get; set; } = TimeSpan.FromHours(6);
}

public sealed class AudioAnalysisJobService(IServiceScopeFactory scopes, IOptions<AudioAnalysisJobOptions> options,
    TimeProvider clock, ILogger<AudioAnalysisJobService> logger) : BackgroundService
{
    private const string MissingAudioFailure = "audio_unavailable";
    private const string AnalysisFailure = "analysis_failed";
    private const string AnalysisTimeoutFailure = "analysis_timeout";
    private const string ChangedSourceFailure = "source_changed";
    private const string ProviderResponseFailure = "invalid_response";
    private const string ProviderBusyFailure = "provider_busy";
    private const string ProviderRejectedFailure = "provider_rejected";
    private const int FirstAttempt = 1;
    private const int RetryMultiplier = 2;
    private DateTimeOffset nextAdmissionAt = DateTimeOffset.MinValue;
    private readonly SemaphoreSlim processing = new(FirstAttempt, FirstAttempt);

    protected override async Task ExecuteAsync(CancellationToken stoppingToken)
    {
        while (!stoppingToken.IsCancellationRequested)
        {
            var processed = false;
            try { processed = await ProcessNextAsync(stoppingToken); }
            catch (OperationCanceledException) when (stoppingToken.IsCancellationRequested) { return; }
            catch (Exception) { logger.LogWarning("Audio analysis could not complete its current pass."); }
            if (!processed) await Task.Delay(options.Value.PollInterval, clock, stoppingToken);
        }
    }

    public async Task<bool> ProcessNextAsync(CancellationToken cancellationToken)
    {
        await processing.WaitAsync(cancellationToken);
        try { return await ProcessOneAsync(cancellationToken); }
        finally { processing.Release(); }
    }

    private async Task<bool> ProcessOneAsync(CancellationToken cancellationToken)
    {
        if (clock.GetUtcNow() < nextAdmissionAt) return false;
        using var scope = scopes.CreateScope();
        var settingsStore = scope.ServiceProvider.GetRequiredService<IAudioAnalysisSettingsStore>();
        var configuration = await settingsStore.GetConfigurationAsync(cancellationToken);
        if (!configuration.Enabled) return false;
        var store = scope.ServiceProvider.GetRequiredService<IAudioAnalysisJobStore>();
        await store.DiscoverAsync(options.Value.DiscoveryBatchSize, cancellationToken);
        var lease = await store.ClaimAsync(configuration, options.Value.LeaseLifetime, cancellationToken);
        if (lease is null) return false;
        using var operationCancellation = CancellationTokenSource.CreateLinkedTokenSource(cancellationToken);
        using var operationDeadline = new CancellationTokenSource(options.Value.OperationTimeout, clock);
        using var deadlineRegistration = operationDeadline.Token.Register(operationCancellation.Cancel);
        using var monitorCancellation = CancellationTokenSource.CreateLinkedTokenSource(cancellationToken);
        var monitor = MonitorSettingsAsync(configuration, operationCancellation, monitorCancellation.Token);
        try
        {
            var sourceProvider = scope.ServiceProvider.GetRequiredService<IAudioAnalysisSource>();
            await using var source = await sourceProvider.OpenAsync(lease.RecordingId, operationCancellation.Token);
            if (source is null)
            {
                await store.FailAsync(lease, MissingAudioFailure, clock.GetUtcNow() + AudioAnalysisJobStore.SourceRecheckInterval,
                    true, operationCancellation.Token);
                return true;
            }
            var existing = await store.GetCurrentAsync(lease.RecordingId, operationCancellation.Token);
            AudioAnalysisResult result;
            if (MatchesCurrent(existing, source, configuration)) result = FromCurrent(existing!);
            else
            {
                var client = scope.ServiceProvider.GetRequiredService<IAudioAnalysisClient>();
                result = await client.AnalyzeAsync(source, lease.Token, configuration, operationCancellation.Token);
                if (!string.Equals(result.AudioSha256, source.AudioSha256, StringComparison.Ordinal)
                    || !string.Equals(result.Profile, configuration.Profile, StringComparison.Ordinal))
                    throw new AudioAnalysisException(ProviderResponseFailure);
            }
            await using var verifiedSource = await sourceProvider.OpenAsync(lease.RecordingId, operationCancellation.Token);
            if (verifiedSource is null || !string.Equals(verifiedSource.SourceVersion, source.SourceVersion, StringComparison.Ordinal)
                || !string.Equals(verifiedSource.AudioSha256, source.AudioSha256, StringComparison.Ordinal))
            {
                await store.FailAsync(lease, ChangedSourceFailure, clock.GetUtcNow() + options.Value.InitialRetryDelay,
                    false, operationCancellation.Token);
                return true;
            }
            if (!await SettingsMatchAsync(configuration, operationCancellation.Token)
                || !await store.CompleteAsync(lease, configuration, source, result, operationCancellation.Token))
                await store.ReleaseAsync(lease, cancellationToken);
            return true;
        }
        catch (OperationCanceledException) when (operationCancellation.IsCancellationRequested)
        {
            if (operationDeadline.IsCancellationRequested && !cancellationToken.IsCancellationRequested
                && await SettingsMatchAsync(configuration, CancellationToken.None))
            {
                nextAdmissionAt = clock.GetUtcNow() + options.Value.ProviderFailureCooldown;
                await store.FailAsync(lease, AnalysisTimeoutFailure, clock.GetUtcNow() + RetryDelay(lease.Attempts, options.Value), false, CancellationToken.None);
            }
            else await store.ReleaseAsync(lease, CancellationToken.None);
            cancellationToken.ThrowIfCancellationRequested();
            return true;
        }
        catch (Exception failure)
        {
            nextAdmissionAt = clock.GetUtcNow() + options.Value.ProviderFailureCooldown;
            var failureCode = failure is AudioAnalysisException analysisFailure
                && analysisFailure.FailureCode is ProviderResponseFailure or ProviderBusyFailure or ProviderRejectedFailure
                ? analysisFailure.FailureCode : AnalysisFailure;
            await store.FailAsync(lease, failureCode, clock.GetUtcNow() + RetryDelay(lease.Attempts, options.Value), false, cancellationToken);
            logger.LogWarning("Audio analysis deferred a recording after a processing failure.");
            return true;
        }
        finally
        {
            monitorCancellation.Cancel();
            try { await monitor; }
            catch (OperationCanceledException) when (monitorCancellation.IsCancellationRequested) { }
        }
    }

    private async Task MonitorSettingsAsync(AudioAnalysisConfiguration expected, CancellationTokenSource operationCancellation, CancellationToken cancellationToken)
    {
        try
        {
            while (!cancellationToken.IsCancellationRequested)
            {
                await Task.Delay(options.Value.SettingsCheckInterval, clock, cancellationToken);
                if (await SettingsMatchAsync(expected, cancellationToken)) continue;
                operationCancellation.Cancel();
                return;
            }
        }
        catch (OperationCanceledException) when (cancellationToken.IsCancellationRequested) { }
        catch (Exception)
        {
            operationCancellation.Cancel();
            logger.LogWarning("Audio analysis stopped because processing settings could not be verified.");
        }
    }

    private async Task<bool> SettingsMatchAsync(AudioAnalysisConfiguration expected, CancellationToken cancellationToken)
    {
        using var scope = scopes.CreateScope();
        var actual = await scope.ServiceProvider.GetRequiredService<IAudioAnalysisSettingsStore>().GetConfigurationAsync(cancellationToken);
        return actual.Enabled && actual.Revision == expected.Revision;
    }

    public static TimeSpan RetryDelay(int attempts, AudioAnalysisJobOptions configuration)
    {
        var delay = configuration.InitialRetryDelay;
        for (var attempt = FirstAttempt; attempt < attempts && delay < configuration.MaximumRetryDelay; attempt++)
            delay = TimeSpan.FromTicks(Math.Min(configuration.MaximumRetryDelay.Ticks, delay.Ticks * RetryMultiplier));
        return delay <= configuration.MaximumRetryDelay ? delay : configuration.MaximumRetryDelay;
    }

    private static bool MatchesCurrent(RecordingAudioAnalysis? current, AudioAnalysisSource source, AudioAnalysisConfiguration configuration) =>
        current is not null && string.Equals(current.SourceVersion, source.SourceVersion, StringComparison.Ordinal)
        && string.Equals(current.AudioSha256, source.AudioSha256, StringComparison.Ordinal)
        && string.Equals(current.Profile, configuration.Profile, StringComparison.Ordinal);

    private static AudioAnalysisResult FromCurrent(RecordingAudioAnalysis current) => new(current.ProtocolVersion, current.FeatureSchemaVersion,
        current.EngineName, current.EngineVersion, current.Profile, current.AudioSha256, current.ModelsJson, current.EmbeddingJson,
        current.FeaturesJson, current.EmbeddingDimensions, current.DurationSeconds, current.Tempo, current.Energy, current.MusicalKey, current.Scale);
}
