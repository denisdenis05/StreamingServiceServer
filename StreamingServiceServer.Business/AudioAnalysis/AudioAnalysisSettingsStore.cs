using Microsoft.EntityFrameworkCore;
using StreamingServiceServer.Data;
using StreamingServiceServer.Data.Models;

namespace StreamingServiceServer.Business.AudioAnalysis;

public sealed record AudioAnalysisCounts(int Pending, int Processing, int Completed, int Unavailable, int Failed);

public sealed record AudioAnalysisPublicSettings(bool Enabled, string EndpointUrl, string Profile, Guid Revision,
    DateTimeOffset UpdatedAt, bool ApiTokenConfigured, AudioAnalysisCounts Counts);

public sealed record UpdateAudioAnalysisSettings(bool Enabled, string EndpointUrl, string Profile,
    Guid ExpectedRevision, string? ApiToken, bool ClearApiToken);

public interface IAudioAnalysisAdministrationStore
{
    Task<AudioAnalysisPublicSettings> GetSettingsAsync(CancellationToken cancellationToken);
    Task<AudioAnalysisPublicSettings?> UpdateSettingsAsync(UpdateAudioAnalysisSettings update, CancellationToken cancellationToken);
}

public sealed class AudioAnalysisSettingsValidationException(string message) : ArgumentException(message);

public static class AudioAnalysisSettingsValidation
{
    public const int ApiTokenMaximumLength = 4096;
    public const string InvalidConfigurationMessage = "Use the musicnn-v1 profile and a valid HTTP or HTTPS service URL without credentials, a query or a fragment.";
    public const string MissingTokenMessage = "Save a bearer token before enabling audio analysis processing.";
    public const string InvalidTokenMessage = "Provide a bounded bearer token, or remove the saved token, but not both.";
    private const char FirstVisibleAsciiCharacter = '!';
    private const char LastVisibleAsciiCharacter = '~';

    public static void Validate(UpdateAudioAnalysisSettings update)
    {
        if (update.Profile != AudioAnalysisStoragePolicy.DefaultProfile || update.ExpectedRevision == Guid.Empty
            || update.EndpointUrl is null || update.EndpointUrl.Length > AudioAnalysisStoragePolicy.EndpointUrlMaximumLength
            || ((update.Enabled || !string.IsNullOrWhiteSpace(update.EndpointUrl)) && !IsValidEndpoint(update.EndpointUrl)))
            throw new AudioAnalysisSettingsValidationException(InvalidConfigurationMessage);
        if (update.ApiToken is not null && (update.ApiToken.Length > ApiTokenMaximumLength
            || update.ApiToken.Any(character => character < FirstVisibleAsciiCharacter || character > LastVisibleAsciiCharacter)
            || (update.ClearApiToken && update.ApiToken.Length > 0)))
            throw new AudioAnalysisSettingsValidationException(InvalidTokenMessage);
    }

    private static bool IsValidEndpoint(string endpoint)
    {
        return Uri.TryCreate(endpoint, UriKind.Absolute, out var address)
            && (address.Scheme == Uri.UriSchemeHttp || address.Scheme == Uri.UriSchemeHttps)
            && !string.IsNullOrWhiteSpace(address.Host)
            && string.IsNullOrEmpty(address.UserInfo) && string.IsNullOrEmpty(address.Query) && string.IsNullOrEmpty(address.Fragment);
    }
}

public sealed class AudioAnalysisSettingsStore : IAudioAnalysisSettingsStore, IAudioAnalysisAdministrationStore
{
    private const int NoUpdatedSettings = 0;
    private const char TrailingPathDelimiter = '/';
    private readonly StreamingDbContext database;
    private readonly IAudioAnalysisSecretStore secrets;
    private readonly TimeProvider timeProvider;

    public AudioAnalysisSettingsStore(StreamingDbContext database, IAudioAnalysisSecretStore secrets, TimeProvider timeProvider)
    {
        this.database = database;
        this.secrets = secrets;
        this.timeProvider = timeProvider;
    }

    public async Task<AudioAnalysisConfiguration> GetConfigurationAsync(CancellationToken cancellationToken)
    {
        var settings = await ReadSettingsAsync(cancellationToken);
        return new AudioAnalysisConfiguration(settings.Enabled, settings.EndpointUrl,
            string.IsNullOrEmpty(settings.ProtectedApiToken) ? string.Empty : secrets.Unprotect(settings.ProtectedApiToken),
            settings.Profile, settings.Revision);
    }

    public async Task<AudioAnalysisPublicSettings> GetSettingsAsync(CancellationToken cancellationToken)
    {
        var settings = await ReadSettingsAsync(cancellationToken);
        var counts = await database.AudioAnalysisJobs.AsNoTracking().GroupBy(job => job.State)
            .Select(group => new { State = group.Key, Count = group.Count() }).ToDictionaryAsync(group => group.State, group => group.Count, cancellationToken);
        return new AudioAnalysisPublicSettings(settings.Enabled, settings.EndpointUrl, settings.Profile, settings.Revision,
            settings.UpdatedAt, !string.IsNullOrEmpty(settings.ProtectedApiToken), new AudioAnalysisCounts(
                counts.GetValueOrDefault(AudioAnalysisJobState.Pending), counts.GetValueOrDefault(AudioAnalysisJobState.Processing),
                counts.GetValueOrDefault(AudioAnalysisJobState.Completed), counts.GetValueOrDefault(AudioAnalysisJobState.Unavailable),
                counts.GetValueOrDefault(AudioAnalysisJobState.Failed)));
    }

    public async Task<AudioAnalysisPublicSettings?> UpdateSettingsAsync(UpdateAudioAnalysisSettings update, CancellationToken cancellationToken)
    {
        cancellationToken.ThrowIfCancellationRequested();
        AudioAnalysisSettingsValidation.Validate(update);
        var previous = await ReadSettingsAsync(cancellationToken);
        if (previous.Revision != update.ExpectedRevision) return null;
        var token = update.ClearApiToken ? string.Empty : string.IsNullOrEmpty(update.ApiToken)
            ? previous.ProtectedApiToken : secrets.Protect(update.ApiToken);
        if (update.Enabled && string.IsNullOrEmpty(token))
            throw new AudioAnalysisSettingsValidationException(AudioAnalysisSettingsValidation.MissingTokenMessage);
        var revision = Guid.NewGuid();
        var updatedAt = timeProvider.GetUtcNow();
        var endpoint = update.EndpointUrl.Trim().TrimEnd(TrailingPathDelimiter);
        var affected = await database.AudioAnalysisSettings.Where(settings => settings.Id == AudioAnalysisStoragePolicy.SettingsIdentifier
                && settings.Revision == update.ExpectedRevision)
            .ExecuteUpdateAsync(setters => setters.SetProperty(settings => settings.Enabled, update.Enabled)
                .SetProperty(settings => settings.EndpointUrl, endpoint)
                .SetProperty(settings => settings.Profile, update.Profile)
                .SetProperty(settings => settings.ProtectedApiToken, token)
                .SetProperty(settings => settings.Revision, revision)
                .SetProperty(settings => settings.UpdatedAt, updatedAt), cancellationToken);
        return affected == NoUpdatedSettings ? null : await GetSettingsAsync(cancellationToken);
    }

    private async Task<AudioAnalysisSettings> ReadSettingsAsync(CancellationToken cancellationToken)
    {
        return await database.AudioAnalysisSettings.AsNoTracking()
            .SingleOrDefaultAsync(settings => settings.Id == AudioAnalysisStoragePolicy.SettingsIdentifier, cancellationToken)
            ?? new AudioAnalysisSettings { Revision = AudioAnalysisStoragePolicy.InitialSettingsRevision, UpdatedAt = DateTimeOffset.UnixEpoch };
    }
}
