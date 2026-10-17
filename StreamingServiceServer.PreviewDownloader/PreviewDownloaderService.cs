using System.Diagnostics;
using System.Globalization;
using Microsoft.Extensions.Configuration;
using StreamingServiceServer.Business.Models.MusicSearch;

namespace StreamingServiceServer.PreviewDownloader;

public sealed class PreviewDownloaderService : IPreviewDownloaderService
{
    private const int MaximumPendingRecordings = 64;
    private const long MaximumPreviewBytes = 128 * 1024 * 1024;
    private static readonly string[] AudioExtensions = [".mp3", ".m4a", ".opus", ".ogg", ".flac", ".wav", ".aac"];
    private readonly IConfiguration configuration;
    private readonly IPreviewProcessRunner processRunner;
    private readonly SemaphoreSlim processSlots;
    private readonly TimeSpan deadline;
    private readonly object synchronization = new();
    private readonly Dictionary<Guid, SharedPreview> pending = new();

    public PreviewDownloaderService(IConfiguration configuration)
        : this(configuration, new PreviewProcessRunner()) { }

    public PreviewDownloaderService(IConfiguration configuration, IPreviewProcessRunner processRunner)
    {
        this.configuration = configuration;
        this.processRunner = processRunner;
        var concurrency = ReadPositiveInteger("Music:PreviewDownloader:MaximumConcurrency", 2);
        processSlots = new SemaphoreSlim(concurrency, concurrency);
        deadline = TimeSpan.FromSeconds(ReadPositiveInteger("Music:PreviewDownloader:DeadlineSeconds", 120));
    }

    public async Task<string> DownloadPreview(RecordingResponse recording, CancellationToken cancellationToken = default)
    {
        cancellationToken.ThrowIfCancellationRequested();
        SharedPreview shared;
        lock (synchronization)
        {
            if (!pending.TryGetValue(recording.Id, out shared!))
            {
                if (pending.Count >= MaximumPendingRecordings)
                    throw new PreviewCapacityException();
                shared = new SharedPreview();
                pending.Add(recording.Id, shared);
                shared.Work = Task.Run(() => ProduceAsync(recording, shared.Cancellation.Token));
            }
            shared.Waiters++;
        }
        try { return await shared.Work.WaitAsync(cancellationToken); }
        finally
        {
            lock (synchronization)
            {
                shared.Waiters--;
                if (shared.Waiters == 0)
                {
                    shared.Cancellation.Cancel();
                    if (pending.TryGetValue(recording.Id, out var current) && ReferenceEquals(current, shared))
                        pending.Remove(recording.Id);
                    _ = ObserveCompletionAsync(shared);
                }
            }
        }
    }

    private static async Task ObserveCompletionAsync(SharedPreview shared)
    {
        try { await shared.Work; }
        catch (Exception) { }
        finally { shared.Cancellation.Dispose(); }
    }

    private async Task<string> ProduceAsync(RecordingResponse recording, CancellationToken cancellationToken)
    {
        using var executionCancellation = CancellationTokenSource.CreateLinkedTokenSource(cancellationToken);
        executionCancellation.CancelAfter(deadline);
        await processSlots.WaitAsync(executionCancellation.Token);
        string? workingDirectory = null;
        try
        {
            var musicRoot = configuration["Music:Path"]
                ?? throw new InvalidOperationException("Music storage path is not configured.");
            File.GetAttributes(musicRoot);
            var previewDirectory = Path.Combine(musicRoot, "TEMP");
            Directory.CreateDirectory(previewDirectory);
            foreach (var extension in AudioExtensions)
            {
                var published = Path.Combine(previewDirectory, recording.Id + extension);
                if (File.Exists(published) && new FileInfo(published).Length > 0)
                    return "TEMP/" + Path.GetFileName(published);
            }
            workingDirectory = Path.Combine(previewDirectory, ".preview-" + Guid.NewGuid().ToString("N"));
            Directory.CreateDirectory(workingDirectory);
            var outputPrefix = Path.Combine(workingDirectory, recording.Id.ToString());
            var startInfo = new ProcessStartInfo
            {
                FileName = Path.Combine(AppContext.BaseDirectory, "Tools", "preview-downloader"),
                WorkingDirectory = workingDirectory,
                Arguments = configuration["Music:PreviewDownloader:Arguments"] ?? "-x --audio-format mp3 --no-playlist -o"
            };
            startInfo.Arguments += " " + QuoteArgument(outputPrefix) + " --max-filesize 128M --no-playlist " + QuoteArgument(
                (configuration["Music:PreviewDownloader:SearchArguments"] ?? "ytsearch1:") +
                recording.Title + " " + recording.ArtistName + " audio");
            await processRunner.RunAsync(startInfo, executionCancellation.Token);
            var candidates = Directory.EnumerateFiles(workingDirectory)
                .Where(path => AudioExtensions.Contains(Path.GetExtension(path), StringComparer.OrdinalIgnoreCase)).ToArray();
            if (candidates.Length != 1)
                throw new InvalidDataException("Preview output must contain exactly one audio file.");
            var candidate = candidates[0];
            var fileInformation = new FileInfo(candidate);
            if (fileInformation.Length <= 0 || fileInformation.Length > MaximumPreviewBytes || fileInformation.LinkTarget != null)
                throw new InvalidDataException("Preview output is empty, oversized or a symbolic link.");
            var validation = new ProcessStartInfo { FileName = "ffprobe" };
            foreach (var argument in new[] { "-v", "error", "-select_streams", "a:0", "-show_entries", "stream=codec_type", "-of", "default=noprint_wrappers=1:nokey=1", candidate })
                validation.ArgumentList.Add(argument);
            var mediaType = await processRunner.RunAsync(validation, executionCancellation.Token);
            if (!mediaType.Split('\n', StringSplitOptions.TrimEntries | StringSplitOptions.RemoveEmptyEntries).Contains("audio"))
                throw new InvalidDataException("Preview output does not contain an audio stream.");
            executionCancellation.Token.ThrowIfCancellationRequested();
            var finalName = recording.Id + Path.GetExtension(candidate).ToLowerInvariant();
            File.Move(candidate, Path.Combine(previewDirectory, finalName), overwrite: true);
            return "TEMP/" + finalName;
        }
        finally
        {
            try
            {
                if (workingDirectory != null) Directory.Delete(workingDirectory, recursive: true);
            }
            finally { processSlots.Release(); }
        }
    }

    private int ReadPositiveInteger(string key, int fallback)
    {
        var value = configuration[key];
        if (value == null) return fallback;
        if (!int.TryParse(value, NumberStyles.None, CultureInfo.InvariantCulture, out var number) || number <= 0)
            throw new InvalidOperationException($"{key} must be a positive integer.");
        return number;
    }

    private static string QuoteArgument(string value) => "\"" + value.Replace("\\", "\\\\").Replace("\"", "\\\"") + "\"";

    private sealed class SharedPreview
    {
        public CancellationTokenSource Cancellation { get; } = new();
        public Task<string> Work { get; set; } = null!;
        public int Waiters { get; set; }
    }
}

public sealed class PreviewCapacityException() : Exception("Preview capacity is exhausted. Retry later.");
