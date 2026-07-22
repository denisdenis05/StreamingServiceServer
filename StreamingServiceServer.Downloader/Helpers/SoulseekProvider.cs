using System.Text.RegularExpressions;
using FuzzySharp;
using StreamingServiceServer.Business.Helpers;
using StreamingServiceServer.Data.Models;

namespace StreamingServiceDownloader.Helpers;

/// <summary>
/// Soulseek download provider using Slskd daemon.
/// Implements format-cascading search (FLAC → WAV → MP3 → Any audio),
/// search rate limiting (min 6s delay), fallback query handling,
/// two-pass candidate matching (strict artist folder match first),
/// and file size/lock validation (500KB - 200MB per file).
/// </summary>
public class SoulseekProvider : IDownloadProvider
{
    private readonly SlskdHelper _slskdHelper;
    
    private static readonly SemaphoreSlim SearchRateLimiter = new(1, 1);
    private static DateTime _lastSearchTime = DateTime.MinValue;
    private static readonly TimeSpan MinInterSearchDelay = TimeSpan.FromSeconds(6);

    private const long MinAudioFileSizeBytes = 500 * 1024;        // 500 KB min to avoid 0B/corrupt files
    private const long MaxAudioFileSizeBytes = 200 * 1024 * 1024; // 200 MB max per single audio track

    private static readonly Dictionary<string, HashSet<string>> FormatExtensions = new(StringComparer.OrdinalIgnoreCase)
    {
        ["FLAC"] = new(StringComparer.OrdinalIgnoreCase) { ".flac" },
        ["WAV"]  = new(StringComparer.OrdinalIgnoreCase) { ".wav" },
        ["MP3"]  = new(StringComparer.OrdinalIgnoreCase) { ".mp3" },
        ["ANY"]  = new(StringComparer.OrdinalIgnoreCase) { ".flac", ".wav", ".mp3", ".m4a", ".aac", ".ogg", ".alac", ".wma", ".opus" }
    };

    public string ProviderName => "Soulseek";

    public SoulseekProvider(SlskdHelper slskdHelper)
    {
        _slskdHelper = slskdHelper;
    }

    public void ApplyConfig(DownloadProviderConfig config)
    {
        _slskdHelper.Configure(config.ApiUrl, config.ApiKey, config.Username, config.Password);
    }

    public async Task<DownloadSearchResult?> SearchAndEnqueueAsync(
        ReleaseToDownload release, string[] formatOrder, CancellationToken cancellationToken)
    {
        // Define search queries: Primary ({Artist} {Title}), then Fallback ({Title}) if empty
        var queries = new List<string>
        {
            CleanSearchQuery($"{release.Artist} {release.Title}"),
            CleanSearchQuery(release.Title)
        }.Distinct(StringComparer.OrdinalIgnoreCase).ToList();

        foreach (var query in queries)
        {
            if (string.IsNullOrWhiteSpace(query)) continue;

            Console.WriteLine($"[Soulseek] Initiating search query: '{query}'");

            // 1. Throttle search requests to protect against Soulseek search bans
            await ThrottleSearchAsync(cancellationToken);

            // 2. Start search on Soulseek network
            var searchId = await _slskdHelper.SearchAsync(query, cancellationToken);
            if (searchId == null) continue;

            try
            {
                // 3. Wait for search responses
                var searchResult = await _slskdHelper.WaitForSearchResultsAsync(searchId.Value, 20, cancellationToken);
                if (searchResult?.Responses == null || searchResult.Responses.Count == 0)
                {
                    Console.WriteLine($"[Soulseek] No search responses for query '{query}', trying fallback...");
                    continue;
                }

                Console.WriteLine($"[Soulseek] Received {searchResult.Responses.Count} responses for query '{query}'.");

                // 4. Try format cascade order (FLAC -> WAV -> MP3 -> ANY)
                foreach (var format in formatOrder)
                {
                    if (!FormatExtensions.TryGetValue(format, out var allowedExtensions))
                        continue;

                    var bestMatch = FindBestUserForFormat(release, searchResult.Responses, allowedExtensions);
                    if (bestMatch != null)
                    {
                        Console.WriteLine($"[Soulseek] Found {format} match from user '{bestMatch.Value.username}' ({bestMatch.Value.files.Count} valid files). Enqueuing...");

                        var enqueued = await _slskdHelper.EnqueueDirectoryDownloadAsync(
                            bestMatch.Value.username, bestMatch.Value.files, cancellationToken);

                        if (enqueued > 0)
                        {
                            return new DownloadSearchResult
                            {
                                Success = true,
                                SourceIdentifier = $"slskd:{searchId.Value}:{bestMatch.Value.username}",
                                SavePath = null
                            };
                        }
                    }
                }

                Console.WriteLine($"[Soulseek] No format tier matched for query '{query}'.");
            }
            finally
            {
                await _slskdHelper.DeleteSearchAsync(searchId.Value, cancellationToken);
            }
        }

        Console.WriteLine($"[Soulseek] All queries exhausted for '{release.Artist} - {release.Title}'. No suitable download found.");
        return null;
    }

    public async Task<DownloadProgressInfo?> CheckProgressAsync(
        string sourceIdentifier, CancellationToken cancellationToken)
    {
        var parts = sourceIdentifier.Split(':');
        if (parts.Length < 3) return null;

        var username = parts[2];
        var downloads = await _slskdHelper.GetDownloadsAsync(cancellationToken);
        if (downloads == null) return null;

        var userTransfers = downloads.FirstOrDefault(d => 
            d.Username?.Equals(username, StringComparison.OrdinalIgnoreCase) == true);
        
        if (userTransfers?.Directories == null || userTransfers.Directories.Count == 0)
        {
            return new DownloadProgressInfo
            {
                Progress = 1.0,
                SavePath = "",
                State = "Completed"
            };
        }

        double totalProgress = 0;
        int fileCount = 0;
        string savePath = "";

        foreach (var dir in userTransfers.Directories)
        {
            if (dir.Files == null) continue;
            if (string.IsNullOrWhiteSpace(savePath) && !string.IsNullOrWhiteSpace(dir.Directory))
                savePath = dir.Directory;

            foreach (var file in dir.Files)
            {
                fileCount++;
                totalProgress += file.PercentComplete ?? 0;
            }
        }

        var avgProgress = fileCount > 0 ? totalProgress / fileCount / 100.0 : 0;
        var allComplete = fileCount > 0 && userTransfers.Directories
            .SelectMany(d => d.Files ?? new List<SlskdTransferItem>())
            .All(f => f.State?.Contains("Completed", StringComparison.OrdinalIgnoreCase) == true);

        return new DownloadProgressInfo
        {
            Progress = allComplete ? 1.0 : avgProgress,
            SavePath = savePath,
            State = allComplete ? "Completed" : "Downloading"
        };
    }

    public Task CleanUpAsync(string sourceIdentifier, bool deleteFiles)
    {
        return Task.CompletedTask;
    }

    // ─── Rate limiting & Search throttling ───

    private static async Task ThrottleSearchAsync(CancellationToken cancellationToken)
    {
        await SearchRateLimiter.WaitAsync(cancellationToken);
        try
        {
            var elapsed = DateTime.UtcNow - _lastSearchTime;
            if (elapsed < MinInterSearchDelay)
            {
                var waitTime = MinInterSearchDelay - elapsed;
                Console.WriteLine($"[Soulseek] Rate limit protection: waiting {waitTime.TotalSeconds:F1}s before next search...");
                await Task.Delay(waitTime, cancellationToken);
            }
            _lastSearchTime = DateTime.UtcNow;
        }
        finally
        {
            SearchRateLimiter.Release();
        }
    }

    // ─── Two-Pass File & Folder Matching with Size/Lock Validation ───

    private (string username, List<SlskdFileDownloadRequest> files)? FindBestUserForFormat(
        ReleaseToDownload release,
        List<SlskdSearchResponse> responses,
        HashSet<string> allowedExtensions)
    {
        var pass1Candidates = new List<(string username, List<SlskdFileDownloadRequest> files, int score, int fileCount, long uploadSpeed)>();
        var pass2Candidates = new List<(string username, List<SlskdFileDownloadRequest> files, int score, int fileCount, long uploadSpeed)>();

        var normalizedArtist = StringSanitizers.SanitizeTitle(release.Artist).ToLowerInvariant();
        var normalizedAlbum = StringSanitizers.SanitizeTitle(release.Title).ToLowerInvariant();
        bool isTargetVariousArtists = normalizedArtist.Contains("various") || normalizedArtist == "va";

        foreach (var response in responses)
        {
            if (response.Username == null || response.Files == null || response.Files.Count == 0)
                continue;

            // Filter response files: 500KB - 200MB size, not locked, allowed extension
            var validFiles = response.Files
                .Where(f => f.Filename != null &&
                           !f.IsLocked &&
                           f.Size >= MinAudioFileSizeBytes &&
                           f.Size <= MaxAudioFileSizeBytes &&
                           allowedExtensions.Contains(GetExtension(f.Filename)))
                .Select(f => new SlskdFileDownloadRequest { Filename = f.Filename!, Size = f.Size })
                .ToList();

            if (validFiles.Count == 0) continue;

            var joinedPaths = string.Join(" ", validFiles.Select(f => f.Filename)).ToLowerInvariant();

            // Exclude compilation folders (VA / Various Artists) if searching for a single artist
            if (!isTargetVariousArtists && (joinedPaths.Contains(@"\va ") || joinedPaths.Contains("va - ") || joinedPaths.Contains("various artists")))
            {
                continue;
            }

            // Check artist match in folder path or filenames (handles abbreviations like JPEG for JPEGMAFIA)
            int artistScore = Fuzz.PartialRatio(normalizedArtist, joinedPaths);
            var artistTokens = Regex.Split(normalizedArtist, @"\W+").Where(t => t.Length >= 3).ToList();
            bool artistTokenInPath = artistTokens.Any(token => joinedPaths.Contains(token));
            bool artistMatched = artistScore >= 60 || artistTokenInPath;

            int albumScore = Fuzz.PartialRatio(normalizedAlbum, joinedPaths);
            if (albumScore < 50) continue;

            int combinedScore = (int)(artistScore * 0.4 + albumScore * 0.6);

            if (artistMatched && albumScore >= 55)
            {
                // Pass 1 Candidate: Strict Artist Match in Folder/Filenames
                pass1Candidates.Add((response.Username, validFiles, Math.Max(combinedScore, 75), validFiles.Count, response.UploadSpeed));
            }
            else if (albumScore >= 75)
            {
                // Pass 2 Candidate: Fallback if no artist-specific folder exists
                pass2Candidates.Add((response.Username, validFiles, albumScore, validFiles.Count, response.UploadSpeed));
            }
        }

        // 1. Prefer Pass 1 candidates (Artist in folder path or filenames)
        if (pass1Candidates.Count > 0)
        {
            var bestPass1 = pass1Candidates
                .OrderByDescending(c => c.score)
                .ThenByDescending(c => c.fileCount)
                .ThenByDescending(c => c.uploadSpeed)
                .First();
            return (bestPass1.username, bestPass1.files);
        }

        // 2. Fall back to Pass 2 candidates (Album title fallback) only if zero artist-specific folders exist
        if (pass2Candidates.Count > 0)
        {
            var bestPass2 = pass2Candidates
                .OrderByDescending(c => c.score)
                .ThenByDescending(c => c.fileCount)
                .ThenByDescending(c => c.uploadSpeed)
                .First();
            return (bestPass2.username, bestPass2.files);
        }

        return null;
    }

    private static string CleanSearchQuery(string query)
    {
        if (string.IsNullOrWhiteSpace(query)) return "";
        var cleaned = Regex.Replace(query, @"[\[\({].*?[\]\)}]", " ");
        cleaned = Regex.Replace(cleaned, @"[-_'\.]", " ");
        cleaned = Regex.Replace(cleaned, @"\s+", " ").Trim();
        return cleaned;
    }

    private static string GetExtension(string filename)
    {
        var lastDot = filename.LastIndexOf('.');
        if (lastDot < 0 || lastDot == filename.Length - 1) return "";
        return filename.Substring(lastDot);
    }
}
