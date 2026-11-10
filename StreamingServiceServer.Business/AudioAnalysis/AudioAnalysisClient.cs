using System.Net;
using System.Net.Http.Headers;
using System.Text.Json;
using StreamingServiceServer.Data.Models;

namespace StreamingServiceServer.Business.AudioAnalysis;

public sealed class AudioAnalysisClient(HttpClient client) : IAudioAnalysisClient
{
    private const string JsonMediaType = "application/json";
    private const string AudioMediaType = "application/octet-stream";
    private const string SafeFileName = "audio";
    private const string ProtocolVersionField = "protocolVersion";
    private const string FeatureSchemaVersionField = "featureSchemaVersion";
    private const string RequestIdField = "requestId";
    private const string AudioSha256Field = "audioSha256";
    private const string ProfileField = "profile";
    private const string EngineNameField = "engineName";
    private const string EngineVersionField = "engineVersion";
    private const string FeaturesField = "features";
    private const string EmbeddingField = "embedding";
    private const string ModelsField = "models";
    private const string ReadyField = "ready";
    private const string NameField = "name";
    private const string Sha256Field = "sha256";
    private const string TempoField = "tempo";
    private const string EnergyField = "energy";
    private const string DurationSecondsField = "durationSeconds";
    private const string MoodsField = "moods";
    private const string KeyField = "key";
    private const string ScaleField = "scale";
    private const string InvalidResponseFailure = "invalid_response";
    private const string ProviderBusyFailure = "provider_busy";
    private const string ProviderRejectedFailure = "provider_rejected";
    private const int RequiredModels = 2;
    private const int Sha256Characters = 64;
    private static readonly TimeSpan HealthTimeout = TimeSpan.FromSeconds(10);

    public async Task<bool> CheckHealthAsync(AudioAnalysisConfiguration configuration, CancellationToken cancellationToken)
    {
        using var deadline = CancellationTokenSource.CreateLinkedTokenSource(cancellationToken);
        deadline.CancelAfter(HealthTimeout);
        cancellationToken = deadline.Token;
        using var request = CreateRequest(HttpMethod.Get, AudioAnalysisProtocol.HealthPath, configuration);
        using var response = await client.SendAsync(request, HttpCompletionOption.ResponseHeadersRead, cancellationToken);
        response.EnsureSuccessStatusCode();
        using var document = await ReadDocumentAsync(response, cancellationToken);
        var health = document.RootElement;
        return health.GetProperty(ReadyField).GetBoolean()
            && health.GetProperty(ProtocolVersionField).GetInt32() == AudioAnalysisProtocol.Version
            && health.GetProperty(ProfileField).GetString() == configuration.Profile
            && health.GetProperty(EngineNameField).GetString() == AudioAnalysisProtocol.EngineName;
    }

    public async Task<AudioAnalysisResult> AnalyzeAsync(AudioAnalysisSource source, Guid requestId,
        AudioAnalysisConfiguration configuration, CancellationToken cancellationToken)
    {
        using var request = CreateRequest(HttpMethod.Post, AudioAnalysisProtocol.AnalysisPath, configuration);
        using var multipart = new MultipartFormDataContent();
        using var audioContent = new StreamContent(new NonOwningStream(source.Content));
        audioContent.Headers.ContentType = new MediaTypeHeaderValue(AudioMediaType);
        multipart.Add(audioContent, AudioAnalysisProtocol.AudioField, SafeFileName + source.Extension);
        multipart.Add(new StringContent(requestId.ToString()), AudioAnalysisProtocol.RequestField);
        multipart.Add(new StringContent(configuration.Profile), AudioAnalysisProtocol.ProfileField);
        request.Content = multipart;
        using var response = await client.SendAsync(request, HttpCompletionOption.ResponseHeadersRead, cancellationToken);
        if (!response.IsSuccessStatusCode)
            throw new AudioAnalysisException(response.StatusCode == HttpStatusCode.TooManyRequests ? ProviderBusyFailure : ProviderRejectedFailure);
        using var document = await ReadDocumentAsync(response, cancellationToken);
        return ValidateResult(document.RootElement, requestId, source.AudioSha256, configuration.Profile);
    }

    public static AudioAnalysisResult ValidateResult(JsonElement response, Guid requestId, string audioSha256, string profile)
    {
        try
        {
            var features = response.GetProperty(FeaturesField);
            var embedding = response.GetProperty(EmbeddingField);
            var models = response.GetProperty(ModelsField);
            var engineVersion = response.GetProperty(EngineVersionField).GetString();
            if (response.GetProperty(ProtocolVersionField).GetInt32() != AudioAnalysisProtocol.Version
                || response.GetProperty(FeatureSchemaVersionField).GetInt32() != AudioAnalysisProtocol.FeatureSchemaVersion
                || response.GetProperty(RequestIdField).GetGuid() != requestId
                || response.GetProperty(AudioSha256Field).GetString() != audioSha256
                || response.GetProperty(ProfileField).GetString() != profile
                || response.GetProperty(EngineNameField).GetString() != AudioAnalysisProtocol.EngineName
                || string.IsNullOrWhiteSpace(engineVersion) || engineVersion.Length > AudioAnalysisStoragePolicy.EngineVersionMaximumLength
                || embedding.GetArrayLength() != AudioAnalysisProtocol.EmbeddingDimensions
                || embedding.EnumerateArray().Any(value => !double.IsFinite(value.GetDouble()))
                || models.GetArrayLength() != RequiredModels) throw InvalidResponse();
            var modelNames = new HashSet<string>(StringComparer.Ordinal);
            foreach (var model in models.EnumerateArray())
            {
                var name = model.GetProperty(NameField).GetString();
                var checksum = model.GetProperty(Sha256Field).GetString();
                if (string.IsNullOrWhiteSpace(name) || name.Length > AudioAnalysisStoragePolicy.EngineNameMaximumLength || !modelNames.Add(name)
                    || checksum?.Length != Sha256Characters || checksum.Any(character => !char.IsAsciiHexDigitLower(character))) throw InvalidResponse();
            }
            var tempo = ReadNonnegative(features, TempoField);
            var energy = ReadNonnegative(features, EnergyField);
            var duration = ReadNonnegative(features, DurationSecondsField);
            if (duration <= 0) throw InvalidResponse();
            var moods = features.GetProperty(MoodsField);
            if (!moods.EnumerateObject().Any()) throw InvalidResponse();
            foreach (var mood in moods.EnumerateObject())
            {
                var score = mood.Value.GetDouble();
                if (string.IsNullOrWhiteSpace(mood.Name) || !double.IsFinite(score) || score < 0 || score > 1) throw InvalidResponse();
            }
            var musicalKey = features.GetProperty(KeyField).GetString();
            var scale = features.GetProperty(ScaleField).GetString();
            if (string.IsNullOrWhiteSpace(musicalKey) || string.IsNullOrWhiteSpace(scale)
                || musicalKey.Length > AudioAnalysisStoragePolicy.MusicalKeyMaximumLength || scale.Length > AudioAnalysisStoragePolicy.ScaleMaximumLength) throw InvalidResponse();
            return new AudioAnalysisResult(AudioAnalysisProtocol.Version, AudioAnalysisProtocol.FeatureSchemaVersion,
                AudioAnalysisProtocol.EngineName, engineVersion, profile, audioSha256, models.GetRawText(), embedding.GetRawText(),
                features.GetRawText(), AudioAnalysisProtocol.EmbeddingDimensions, duration, tempo, energy, musicalKey, scale);
        }
        catch (Exception exception) when (exception is InvalidOperationException or KeyNotFoundException or FormatException)
        {
            throw InvalidResponse();
        }
    }

    private static double ReadNonnegative(JsonElement features, string name)
    {
        var value = features.GetProperty(name).GetDouble();
        return double.IsFinite(value) && value >= 0 ? value : throw InvalidResponse();
    }

    private static AudioAnalysisException InvalidResponse() => new(InvalidResponseFailure);

    private static HttpRequestMessage CreateRequest(HttpMethod method, string path, AudioAnalysisConfiguration configuration)
    {
        var request = new HttpRequestMessage(method, new Uri(new Uri(configuration.EndpointUrl.TrimEnd('/') + "/"), path));
        request.Headers.Authorization = new AuthenticationHeaderValue(AudioAnalysisProtocol.AuthorizationScheme, configuration.ApiToken);
        request.Headers.Accept.Add(new MediaTypeWithQualityHeaderValue(JsonMediaType));
        return request;
    }

    private static async Task<JsonDocument> ReadDocumentAsync(HttpResponseMessage response, CancellationToken cancellationToken)
    {
        await response.Content.LoadIntoBufferAsync(AudioAnalysisProtocol.MaximumResponseBytes, cancellationToken);
        var bytes = await response.Content.ReadAsByteArrayAsync(cancellationToken);
        if (bytes.Length > AudioAnalysisProtocol.MaximumResponseBytes) throw InvalidResponse();
        try { return JsonDocument.Parse(bytes); }
        catch (JsonException) { throw InvalidResponse(); }
    }

    private sealed class NonOwningStream(Stream inner) : Stream
    {
        public override bool CanRead => inner.CanRead;
        public override bool CanSeek => inner.CanSeek;
        public override bool CanWrite => false;
        public override long Length => inner.Length;
        public override long Position { get => inner.Position; set => inner.Position = value; }
        public override void Flush() => inner.Flush();
        public override int Read(byte[] buffer, int offset, int count) => inner.Read(buffer, offset, count);
        public override ValueTask<int> ReadAsync(Memory<byte> buffer, CancellationToken cancellationToken = default) => inner.ReadAsync(buffer, cancellationToken);
        public override long Seek(long offset, SeekOrigin origin) => inner.Seek(offset, origin);
        public override void SetLength(long value) => throw new NotSupportedException();
        public override void Write(byte[] buffer, int offset, int count) => throw new NotSupportedException();
    }
}
