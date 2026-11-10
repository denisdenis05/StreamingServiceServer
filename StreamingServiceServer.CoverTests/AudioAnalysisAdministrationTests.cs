using System.Reflection;
using System.Security.Cryptography;
using System.Text.Json;
using Microsoft.AspNetCore.Authorization;
using Microsoft.AspNetCore.DataProtection;
using Microsoft.AspNetCore.Http;
using Microsoft.AspNetCore.Mvc;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.DependencyInjection;
using StreamingServiceServer.API.AudioAnalysis;
using StreamingServiceServer.API.Controllers;
using StreamingServiceServer.Business.AudioAnalysis;
using StreamingServiceServer.Data;
using StreamingServiceServer.Data.Models;
using Xunit;

namespace StreamingServiceServer.CoverTests;

public sealed class AudioAnalysisAdministrationTests
{
    private const string Endpoint = "http://audiomuse:8000";
    private const string Token = "private-test-token";
    private const string PrivateFailure = "private provider response containing private-test-token";
    private const string ReplacementToken = "replacement-test-token";
    private const string SerializedTokenProperty = "ApiToken";
    private const string AdministratorRole = "Admin";
    private const int EndpointOverflow = 1;
    private const int TokenOverflow = 1;
    private const string OtherProfile = "unsupported-profile";
    private static readonly Guid Revision = Guid.Parse("3974d3d1-4a72-42dd-8fbe-ad495868a013");

    [Fact]
    public void EverySettingsEndpointRequiresAdministratorAuthorization()
    {
        var authorization = Assert.Single(typeof(AudioAnalysisSettingsController).GetCustomAttributes<AuthorizeAttribute>());
        Assert.Equal(AdministratorRole, authorization.Roles);
        Assert.Empty(typeof(AudioAnalysisSettingsController).GetCustomAttributes<AllowAnonymousAttribute>());
        foreach (var method in typeof(AudioAnalysisSettingsController).GetMethods(BindingFlags.Instance | BindingFlags.Public)
                     .Where(method => method.DeclaringType == typeof(AudioAnalysisSettingsController)))
            Assert.Empty(method.GetCustomAttributes<AllowAnonymousAttribute>());
    }

    [Theory]
    [InlineData("file:///library")]
    [InlineData("ftp://localhost/audio")]
    [InlineData("http://user:password@localhost:8000")]
    [InlineData("http://localhost:8000?token=secret")]
    [InlineData("http://localhost:8000#secret")]
    [InlineData("not-an-address")]
    public async Task InvalidEndpointIsRejectedBeforeStoreAccess(string endpoint)
    {
        var fixture = new ControllerFixture();
        Assert.IsType<BadRequestObjectResult>(await fixture.Controller.Put(Update() with { EndpointUrl = endpoint }, CancellationToken.None));
        Assert.Equal(default, fixture.Administration.UpdateCalls);
    }

    [Theory]
    [InlineData("http://localhost:8000")]
    [InlineData("http://127.0.0.1:8000")]
    [InlineData("http://[::1]:8000")]
    [InlineData("http://audiomuse:8000/api/")]
    [InlineData("https://192.168.1.25:8443")]
    public void ValidLocalServiceAddressesAreAccepted(string endpoint)
        => AudioAnalysisSettingsValidation.Validate(Update() with { EndpointUrl = endpoint });

    [Fact]
    public void DisabledUnconfiguredInstallationIsValidButEnabledRequiresEndpoint()
    {
        AudioAnalysisSettingsValidation.Validate(Update() with { Enabled = false, EndpointUrl = string.Empty });
        Assert.Throws<AudioAnalysisSettingsValidationException>(() => AudioAnalysisSettingsValidation.Validate(Update() with { EndpointUrl = string.Empty }));
    }

    [Fact]
    public void InvalidProfileRevisionEndpointAndTokenBoundsFailValidation()
    {
        Assert.Throws<AudioAnalysisSettingsValidationException>(() => AudioAnalysisSettingsValidation.Validate(Update() with { Profile = OtherProfile }));
        Assert.Throws<AudioAnalysisSettingsValidationException>(() => AudioAnalysisSettingsValidation.Validate(Update() with { ExpectedRevision = Guid.Empty }));
        Assert.Throws<AudioAnalysisSettingsValidationException>(() => AudioAnalysisSettingsValidation.Validate(Update() with {
            EndpointUrl = new string('a', AudioAnalysisStoragePolicy.EndpointUrlMaximumLength + EndpointOverflow) }));
        Assert.Throws<AudioAnalysisSettingsValidationException>(() => AudioAnalysisSettingsValidation.Validate(Update() with {
            ApiToken = new string('a', AudioAnalysisSettingsValidation.ApiTokenMaximumLength + TokenOverflow) }));
        Assert.Throws<AudioAnalysisSettingsValidationException>(() => AudioAnalysisSettingsValidation.Validate(Update() with { ApiToken = Token, ClearApiToken = true }));
        Assert.Throws<AudioAnalysisSettingsValidationException>(() => AudioAnalysisSettingsValidation.Validate(Update() with { ApiToken = Token + "\r\n" }));
        Assert.Throws<AudioAnalysisSettingsValidationException>(() => AudioAnalysisSettingsValidation.Validate(Update() with { ApiToken = " " }));
        Assert.Throws<AudioAnalysisSettingsValidationException>(() => AudioAnalysisSettingsValidation.Validate(Update() with { ApiToken = Token + "\t" }));
    }

    [Fact]
    public async Task StaleRevisionReturnsConflictWithoutLeakingSavedConfiguration()
    {
        var fixture = new ControllerFixture(); fixture.Administration.Updated = null;
        var result = Assert.IsType<ConflictObjectResult>(await fixture.Controller.Put(Update(), CancellationToken.None));
        Assert.DoesNotContain(Token, JsonSerializer.Serialize(result.Value));
    }

    [Fact]
    public async Task PublicSettingsExposeOnlyTokenPresence()
    {
        var fixture = new ControllerFixture();
        var result = Assert.IsType<OkObjectResult>(await fixture.Controller.Get(CancellationToken.None));
        var serialized = JsonSerializer.Serialize(result.Value);
        Assert.DoesNotContain(SerializedTokenProperty + "\"", serialized);
        Assert.DoesNotContain(Token, serialized);
        Assert.Contains(nameof(AudioAnalysisPublicSettings.ApiTokenConfigured), serialized);
    }

    [Fact]
    public async Task ConnectionTestUsesPersistedConfigurationAndReturnsOnlyReadiness()
    {
        var fixture = new ControllerFixture();
        var result = Assert.IsType<OkObjectResult>(await fixture.Controller.Test(CancellationToken.None));
        Assert.Equal(fixture.Configuration.Value, fixture.Client.Checked);
        Assert.DoesNotContain(Token, JsonSerializer.Serialize(result.Value));
    }

    [Fact]
    public async Task ProviderAndStorageExceptionsReturnSafeErrors()
    {
        var fixture = new ControllerFixture(); fixture.Administration.Failure = new InvalidOperationException(PrivateFailure);
        AssertSafeFailure(await fixture.Controller.Get(CancellationToken.None));
        AssertSafeFailure(await fixture.Controller.Put(Update(), CancellationToken.None));
        fixture.Client.Failure = new InvalidOperationException(PrivateFailure);
        AssertSafeFailure(await fixture.Controller.Test(CancellationToken.None));
    }

    [Fact]
    public async Task RequestCancellationPropagatesInsteadOfReportingServiceFailure()
    {
        var fixture = new ControllerFixture(); fixture.Client.Failure = new OperationCanceledException();
        using var cancellation = new CancellationTokenSource(); cancellation.Cancel();
        await Assert.ThrowsAnyAsync<OperationCanceledException>(() => fixture.Controller.Test(cancellation.Token));
    }

    [Fact]
    public async Task ProviderTimeoutReturnsSafeUnavailableResponse()
    {
        var fixture = new ControllerFixture(); fixture.Client.Failure = new OperationCanceledException();
        AssertSafeFailure(await fixture.Controller.Test(CancellationToken.None));
    }

    [Fact]
    public async Task MissingMergedTokenValidationReturnsSafeBadRequest()
    {
        var fixture = new ControllerFixture();
        fixture.Administration.Failure = new AudioAnalysisSettingsValidationException(AudioAnalysisSettingsValidation.MissingTokenMessage);
        Assert.IsType<BadRequestObjectResult>(await fixture.Controller.Put(Update(), CancellationToken.None));
    }

    [Fact]
    public void SecretProtectionRoundTripsAndRejectsDifferentKeys()
    {
        var secrets = new AudioAnalysisSecretStore(new EphemeralDataProtectionProvider());
        var protectedToken = secrets.Protect(Token);
        Assert.DoesNotContain(Token, protectedToken);
        Assert.Equal(Token, secrets.Unprotect(protectedToken));
        var otherSecrets = new AudioAnalysisSecretStore(new EphemeralDataProtectionProvider());
        Assert.Throws<CryptographicException>(() => otherSecrets.Unprotect(protectedToken));
    }

    [Fact]
    public async Task StoreValidationAndCancellationRunBeforeDatabaseAccess()
    {
        using var database = new StreamingDbContext(new DbContextOptionsBuilder<StreamingDbContext>().Options);
        var store = new AudioAnalysisSettingsStore(database, new AudioAnalysisSecretStore(new EphemeralDataProtectionProvider()), TimeProvider.System);
        await Assert.ThrowsAsync<AudioAnalysisSettingsValidationException>(() => store.UpdateSettingsAsync(Update() with { Profile = OtherProfile }, CancellationToken.None));
        using var cancellation = new CancellationTokenSource(); cancellation.Cancel();
        await Assert.ThrowsAnyAsync<OperationCanceledException>(() => store.UpdateSettingsAsync(Update(), cancellation.Token));
    }

    [PostgresFact]
    public async Task PostgreSqlSettingsStartDisabledAndPersistProtectedTokenWithCompareAndSwap()
    {
        await using var fixture = await DatabaseFixture.CreateAsync();
        using var scope = fixture.Services.CreateScope();
        var database = scope.ServiceProvider.GetRequiredService<StreamingDbContext>();
        var store = new AudioAnalysisSettingsStore(database, new AudioAnalysisSecretStore(new EphemeralDataProtectionProvider()), fixture.Clock);
        var initial = await store.GetSettingsAsync(CancellationToken.None);
        Assert.False(initial.Enabled); Assert.False(initial.ApiTokenConfigured);
        await Assert.ThrowsAsync<AudioAnalysisSettingsValidationException>(() => store.UpdateSettingsAsync(
            Update() with { ExpectedRevision = initial.Revision }, CancellationToken.None));
        var saved = await store.UpdateSettingsAsync(Update() with { ExpectedRevision = initial.Revision, ApiToken = Token }, CancellationToken.None);
        Assert.NotNull(saved); Assert.True(saved.Enabled); Assert.True(saved.ApiTokenConfigured); Assert.NotEqual(initial.Revision, saved.Revision);
        var configuration = await store.GetConfigurationAsync(CancellationToken.None);
        Assert.Equal(Token, configuration.ApiToken);
        var stored = await database.AudioAnalysisSettings.AsNoTracking().SingleAsync();
        Assert.DoesNotContain(Token, stored.ProtectedApiToken);
        Assert.Null(await store.UpdateSettingsAsync(Update() with { ExpectedRevision = initial.Revision, ApiToken = ReplacementToken }, CancellationToken.None));
        Assert.Equal(Token, (await store.GetConfigurationAsync(CancellationToken.None)).ApiToken);
        var retained = await store.UpdateSettingsAsync(Update() with { ExpectedRevision = saved.Revision, Enabled = false }, CancellationToken.None);
        Assert.NotNull(retained); Assert.False(retained.Enabled); Assert.True(retained.ApiTokenConfigured);
        var cleared = await store.UpdateSettingsAsync(Update() with { ExpectedRevision = retained.Revision, Enabled = false, ClearApiToken = true }, CancellationToken.None);
        Assert.NotNull(cleared); Assert.False(cleared.ApiTokenConfigured);
        Assert.Empty((await store.GetConfigurationAsync(CancellationToken.None)).ApiToken);
    }

    [PostgresFact]
    public async Task PostgreSqlConcurrentSettingsChangesHaveOnlyOneWinner()
    {
        await using var fixture = await DatabaseFixture.CreateAsync();
        using var firstScope = fixture.Services.CreateScope();
        using var secondScope = fixture.Services.CreateScope();
        var secrets = new AudioAnalysisSecretStore(new EphemeralDataProtectionProvider());
        var firstStore = new AudioAnalysisSettingsStore(firstScope.ServiceProvider.GetRequiredService<StreamingDbContext>(), secrets, fixture.Clock);
        var secondStore = new AudioAnalysisSettingsStore(secondScope.ServiceProvider.GetRequiredService<StreamingDbContext>(), secrets, fixture.Clock);
        var initial = await firstStore.GetSettingsAsync(CancellationToken.None);
        var updates = await Task.WhenAll(
            firstStore.UpdateSettingsAsync(Update() with { ExpectedRevision = initial.Revision, ApiToken = Token }, CancellationToken.None),
            secondStore.UpdateSettingsAsync(Update() with { ExpectedRevision = initial.Revision, ApiToken = ReplacementToken }, CancellationToken.None));
        var winner = Assert.Single(updates, settings => settings is not null);
        Assert.Single(updates, settings => settings is null);
        Assert.Equal(winner!.Revision, (await firstStore.GetSettingsAsync(CancellationToken.None)).Revision);
    }

    [PostgresFact]
    public async Task PostgreSqlStatusCountsIncludeEachDurableJobState()
    {
        await using var fixture = await DatabaseFixture.CreateAsync();
        var releaseIdentifier = await fixture.AddReleaseAsync();
        using var scope = fixture.Services.CreateScope();
        var database = scope.ServiceProvider.GetRequiredService<StreamingDbContext>();
        var release = await database.Releases.SingleAsync(release => release.Id == releaseIdentifier);
        foreach (var jobState in Enum.GetValues<AudioAnalysisJobState>())
        {
            var recording = new Recording { Id = Guid.NewGuid(), Title = nameof(AudioAnalysisJobState), Cover = string.Empty, Release = release };
            database.Recordings.Add(recording);
            database.AudioAnalysisJobs.Add(new AudioAnalysisJob { RecordingId = recording.Id, State = jobState, Version = Guid.NewGuid(),
                NextAttemptAt = DateTimeOffset.UnixEpoch, EnqueuedAt = DateTimeOffset.UnixEpoch, UpdatedAt = DateTimeOffset.UnixEpoch });
        }
        await database.SaveChangesAsync();
        var store = new AudioAnalysisSettingsStore(database, new AudioAnalysisSecretStore(new EphemeralDataProtectionProvider()), fixture.Clock);
        const int JobsPerState = 1;
        Assert.Equal(new AudioAnalysisCounts(JobsPerState, JobsPerState, JobsPerState, JobsPerState, JobsPerState),
            (await store.GetSettingsAsync(CancellationToken.None)).Counts);
    }

    private static UpdateAudioAnalysisSettings Update() => new(true, Endpoint, AudioAnalysisStoragePolicy.DefaultProfile, Revision, null, false);
    private static AudioAnalysisPublicSettings PublicSettings() => new(false, Endpoint, AudioAnalysisStoragePolicy.DefaultProfile,
        Revision, DateTimeOffset.UnixEpoch, true, new AudioAnalysisCounts(default, default, default, default, default));
    private static void AssertSafeFailure(IActionResult action)
    {
        var result = Assert.IsType<ObjectResult>(action);
        Assert.Equal(StatusCodes.Status503ServiceUnavailable, result.StatusCode);
        Assert.DoesNotContain(PrivateFailure, JsonSerializer.Serialize(result.Value));
        Assert.DoesNotContain(Token, JsonSerializer.Serialize(result.Value));
    }
    private sealed class ControllerFixture
    {
        public AdministrationStub Administration { get; } = new();
        public ConfigurationStub Configuration { get; } = new();
        public ClientStub Client { get; } = new();
        public AudioAnalysisSettingsController Controller => new(Administration, Configuration, Client);
    }
    private sealed class AdministrationStub : IAudioAnalysisAdministrationStore
    {
        public int UpdateCalls { get; private set; }
        public AudioAnalysisPublicSettings? Updated { get; set; } = PublicSettings();
        public Exception? Failure { get; set; }
        public Task<AudioAnalysisPublicSettings> GetSettingsAsync(CancellationToken cancellationToken)
            => Failure is null ? Task.FromResult(PublicSettings()) : Task.FromException<AudioAnalysisPublicSettings>(Failure);
        public Task<AudioAnalysisPublicSettings?> UpdateSettingsAsync(UpdateAudioAnalysisSettings update, CancellationToken cancellationToken)
        {
            UpdateCalls++;
            return Failure is null ? Task.FromResult(Updated) : Task.FromException<AudioAnalysisPublicSettings?>(Failure);
        }
    }
    private sealed class ConfigurationStub : IAudioAnalysisSettingsStore
    {
        public AudioAnalysisConfiguration Value { get; } = new(true, Endpoint, Token, AudioAnalysisStoragePolicy.DefaultProfile, Revision);
        public Task<AudioAnalysisConfiguration> GetConfigurationAsync(CancellationToken cancellationToken) => Task.FromResult(Value);
    }
    private sealed class ClientStub : IAudioAnalysisClient
    {
        public AudioAnalysisConfiguration? Checked { get; private set; }
        public Exception? Failure { get; set; }
        public Task<bool> CheckHealthAsync(AudioAnalysisConfiguration configuration, CancellationToken cancellationToken)
        {
            Checked = configuration;
            return Failure is null ? Task.FromResult(true) : Task.FromException<bool>(Failure);
        }
        public Task<AudioAnalysisResult> AnalyzeAsync(AudioAnalysisSource source, Guid requestId, AudioAnalysisConfiguration configuration,
            CancellationToken cancellationToken) => throw new NotSupportedException();
    }
}
