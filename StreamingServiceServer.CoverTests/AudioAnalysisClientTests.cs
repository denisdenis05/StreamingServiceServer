using System.Net;
using System.Text;
using System.Text.Json;
using StreamingServiceServer.Business.AudioAnalysis;
using Xunit;

namespace StreamingServiceServer.CoverTests;

public sealed class AudioAnalysisClientTests
{
    private static readonly Guid RequestIdentifier = Guid.Parse("2b348c96-05dd-486a-bd76-e403f4165167");
    private static readonly string AudioChecksum = new('a', 64);
    private const string SecondChecksum = "bbbbbbbbbbbbbbbbbbbbbbbbbbbbbbbbbbbbbbbbbbbbbbbbbbbbbbbbbbbbbbbb";
    private const string Endpoint = "http://localhost:8000/";
    private const string Token = "test-token";
    private const string EngineVersion = "3.7.1";
    private const string InvalidResponseCode = "invalid_response";
    private const string ProviderBusyCode = "provider_busy";
    private const string EmbeddingModel = "embedding";
    private const string PredictionModel = "prediction";
    private const string MoodLabel = "happy";
    private const double MoodScore = 0.5;
    private const double Tempo = 100;
    private const double DurationSeconds = 180;
    private const string MusicalKey = "C";
    private const string Scale = "major";
    private const string AudioExtension = ".flac";
    private const string SourceVersion = "source-version";
    private const string JsonMediaType = "application/json";

    [Fact]
    public void AcceptsCompleteFiniteResultAndPreservesFeatures()
    {
        using var document = JsonDocument.Parse(JsonSerializer.Serialize(ValidPayload()));
        var result = AudioAnalysisClient.ValidateResult(document.RootElement, RequestIdentifier, AudioChecksum, AudioAnalysisProtocol.Profile);
        Assert.Equal(AudioAnalysisProtocol.EmbeddingDimensions, result.EmbeddingDimensions);
        Assert.Equal(Tempo, result.Tempo);
        Assert.Equal(DurationSeconds, result.DurationSeconds);
        Assert.Contains(MoodLabel, result.FeaturesJson);
        Assert.Contains(SecondChecksum, result.ModelsJson);
    }

    [Theory]
    [InlineData("requestId")]
    [InlineData("audioSha256")]
    [InlineData("profile")]
    [InlineData("protocolVersion")]
    [InlineData("featureSchemaVersion")]
    [InlineData("engineName")]
    [InlineData("embedding")]
    [InlineData("models")]
    [InlineData("features")]
    public void RejectsMissingContractFields(string missingProperty)
    {
        var payload = ValidPayload();
        payload.Remove(missingProperty);
        using var document = JsonDocument.Parse(JsonSerializer.Serialize(payload));
        var failure = Assert.Throws<AudioAnalysisException>(() => AudioAnalysisClient.ValidateResult(document.RootElement,
            RequestIdentifier, AudioChecksum, AudioAnalysisProtocol.Profile));
        Assert.Equal(InvalidResponseCode, failure.FailureCode);
    }

    [Fact]
    public void RejectsResultFromAnotherRequest()
    {
        using var document = JsonDocument.Parse(JsonSerializer.Serialize(ValidPayload()));
        Assert.Throws<AudioAnalysisException>(() => AudioAnalysisClient.ValidateResult(document.RootElement,
            Guid.Empty, AudioChecksum, AudioAnalysisProtocol.Profile));
    }

    [Fact]
    public void RejectsResultFromDifferentAudio()
    {
        using var document = JsonDocument.Parse(JsonSerializer.Serialize(ValidPayload()));
        Assert.Throws<AudioAnalysisException>(() => AudioAnalysisClient.ValidateResult(document.RootElement,
            RequestIdentifier, SecondChecksum, AudioAnalysisProtocol.Profile));
    }

    [Fact]
    public void RejectsWrongEmbeddingDimensions()
    {
        var payload = ValidPayload();
        payload["embedding"] = Array.Empty<double>();
        using var document = JsonDocument.Parse(JsonSerializer.Serialize(payload));
        Assert.Throws<AudioAnalysisException>(() => AudioAnalysisClient.ValidateResult(document.RootElement,
            RequestIdentifier, AudioChecksum, AudioAnalysisProtocol.Profile));
    }

    [Fact]
    public void RejectsOutOfRangeMoodScore()
    {
        var payload = ValidPayload();
        var features = Assert.IsType<Dictionary<string, object>>(payload["features"]);
        features["moods"] = new Dictionary<string, double> { [MoodLabel] = double.MaxValue };
        using var document = JsonDocument.Parse(JsonSerializer.Serialize(payload));
        Assert.Throws<AudioAnalysisException>(() => AudioAnalysisClient.ValidateResult(document.RootElement,
            RequestIdentifier, AudioChecksum, AudioAnalysisProtocol.Profile));
    }

    [Theory]
    [InlineData("protocolVersion", -1)]
    [InlineData("featureSchemaVersion", -1)]
    [InlineData("engineName", "unrecognized-engine")]
    [InlineData("engineVersion", "")]
    [InlineData("profile", "unknown-profile")]
    [InlineData("embedding", "invalid-shape")]
    [InlineData("models", "invalid-shape")]
    public void RejectsIncompatibleOrMalformedContract(string property, object value)
    {
        var payload = ValidPayload();
        payload[property] = value;
        using var document = JsonDocument.Parse(JsonSerializer.Serialize(payload));
        Assert.Throws<AudioAnalysisException>(() => AudioAnalysisClient.ValidateResult(document.RootElement,
            RequestIdentifier, AudioChecksum, AudioAnalysisProtocol.Profile));
    }

    [Theory]
    [InlineData("tempo", -1)]
    [InlineData("energy", -1)]
    [InlineData("durationSeconds", 0)]
    [InlineData("key", "")]
    [InlineData("scale", "")]
    public void RejectsInvalidFeatureValues(string property, object value)
    {
        var payload = ValidPayload();
        var features = Assert.IsType<Dictionary<string, object>>(payload["features"]);
        features[property] = value;
        using var document = JsonDocument.Parse(JsonSerializer.Serialize(payload));
        Assert.Throws<AudioAnalysisException>(() => AudioAnalysisClient.ValidateResult(document.RootElement,
            RequestIdentifier, AudioChecksum, AudioAnalysisProtocol.Profile));
    }

    [Fact]
    public async Task RejectsMalformedProviderJson()
    {
        const string malformedBody = "{";
        var handler = new CapturingHandler(request => Task.FromResult(new HttpResponseMessage(HttpStatusCode.OK)
        { Content = new StringContent(malformedBody, Encoding.UTF8, JsonMediaType) }));
        var client = new AudioAnalysisClient(new HttpClient(handler));
        await using var source = new AudioAnalysisSource(new MemoryStream(), SourceVersion, AudioChecksum, AudioExtension);
        var failure = await Assert.ThrowsAsync<AudioAnalysisException>(() => client.AnalyzeAsync(source,
            RequestIdentifier, Configuration(), CancellationToken.None));
        Assert.Equal(InvalidResponseCode, failure.FailureCode);
    }

    [Fact]
    public async Task UploadUsesOpaqueFilenameAndBearerAuthentication()
    {
        var handler = new CapturingHandler(async request =>
        {
            Assert.Equal(AudioAnalysisProtocol.AuthorizationScheme, request.Headers.Authorization?.Scheme);
            Assert.Equal(Token, request.Headers.Authorization?.Parameter);
            Assert.Equal(new Uri(new Uri(Endpoint), AudioAnalysisProtocol.AnalysisPath), request.RequestUri);
            var body = await request.Content!.ReadAsStringAsync();
            Assert.Contains("audio.flac", body);
            Assert.Contains(RequestIdentifier.ToString(), body);
            return JsonResponse(ValidPayload());
        });
        var client = new AudioAnalysisClient(new HttpClient(handler));
        await using var source = new AudioAnalysisSource(new MemoryStream([1, 2, 3]), SourceVersion, AudioChecksum, AudioExtension);
        var result = await client.AnalyzeAsync(source, RequestIdentifier, Configuration(), CancellationToken.None);
        Assert.Equal(AudioChecksum, result.AudioSha256);
        Assert.True(source.Content.CanRead);
    }

    [Fact]
    public async Task BusyProviderReturnsSafeFailureCode()
    {
        var handler = new CapturingHandler(request => Task.FromResult(new HttpResponseMessage(HttpStatusCode.TooManyRequests)));
        var client = new AudioAnalysisClient(new HttpClient(handler));
        await using var source = new AudioAnalysisSource(new MemoryStream(), SourceVersion, AudioChecksum, AudioExtension);
        var failure = await Assert.ThrowsAsync<AudioAnalysisException>(() => client.AnalyzeAsync(source,
            RequestIdentifier, Configuration(), CancellationToken.None));
        Assert.Equal(ProviderBusyCode, failure.FailureCode);
    }

    [Fact]
    public async Task CallerCancellationPropagates()
    {
        var handler = new CapturingHandler(request => Task.FromCanceled<HttpResponseMessage>(new CancellationToken(true)));
        var client = new AudioAnalysisClient(new HttpClient(handler));
        await Assert.ThrowsAnyAsync<OperationCanceledException>(() => client.CheckHealthAsync(Configuration(), CancellationToken.None));
    }

    private static AudioAnalysisConfiguration Configuration() => new(true, Endpoint, Token, AudioAnalysisProtocol.Profile, RequestIdentifier);
    private static Dictionary<string, object> ValidPayload() => new()
    {
        ["protocolVersion"] = AudioAnalysisProtocol.Version,
        ["requestId"] = RequestIdentifier,
        ["audioSha256"] = AudioChecksum,
        ["profile"] = AudioAnalysisProtocol.Profile,
        ["engineName"] = AudioAnalysisProtocol.EngineName,
        ["engineVersion"] = EngineVersion,
        ["featureSchemaVersion"] = AudioAnalysisProtocol.FeatureSchemaVersion,
        ["models"] = new[] { new { name = EmbeddingModel, sha256 = AudioChecksum }, new { name = PredictionModel, sha256 = SecondChecksum } },
        ["embedding"] = Enumerable.Repeat(MoodScore, AudioAnalysisProtocol.EmbeddingDimensions).ToArray(),
        ["features"] = new Dictionary<string, object>
        {
            ["tempo"] = Tempo, ["energy"] = MoodScore, ["durationSeconds"] = DurationSeconds,
            ["key"] = MusicalKey, ["scale"] = Scale, ["moods"] = new Dictionary<string, double> { [MoodLabel] = MoodScore }
        }
    };
    private static HttpResponseMessage JsonResponse(object payload) => new(HttpStatusCode.OK)
    {
        Content = new StringContent(JsonSerializer.Serialize(payload), Encoding.UTF8, JsonMediaType)
    };
    private sealed class CapturingHandler(Func<HttpRequestMessage, Task<HttpResponseMessage>> respond) : HttpMessageHandler
    {
        protected override Task<HttpResponseMessage> SendAsync(HttpRequestMessage request, CancellationToken cancellationToken) => respond(request);
    }
}
