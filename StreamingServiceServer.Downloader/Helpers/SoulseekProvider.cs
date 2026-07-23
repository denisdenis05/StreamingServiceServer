using System.Globalization;
using System.Text;
using System.Text.RegularExpressions;
using FuzzySharp;
using StreamingServiceServer.Business.Helpers;
using StreamingServiceServer.Data.Models;

namespace StreamingServiceDownloader.Helpers;

/// <summary>
/// Soulseek download provider using Slskd daemon.
/// Implements format-cascading search (FLAC → WAV → MP3 → Any audio),
/// multi-tier search queries (Artist+Title, Artist+CoreTitle, Title-Only, CoreTitle-Only with diacritics/symbol normalization),
/// peer free-slot & transfer speed prioritization,
/// audio bitrate/quality scoring and fake FLAC detection,
/// directory-based candidate grouping with track completeness scoring,
/// strict artist verification for title-only queries,
/// search rate limiting (min 6s delay), and file size/lock validation (500KB - 200MB).
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

    private struct SearchQueryOption
    {
        public string Query { get; set; }
        public bool ContainsArtist { get; set; }
    }

    private class CandidateDirectory
    {
        public string Username { get; set; } = string.Empty;
        public string DirectoryPath { get; set; } = string.Empty;
        public List<SlskdFileDownloadRequest> Files { get; set; } = new();
        public int Score { get; set; }
        public int FileCount => Files.Count;
        public long UploadSpeed { get; set; }
        public bool HasFreeSlots { get; set; }
    }

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
        var queries = GenerateSearchQueries(release);

        foreach (var queryOption in queries)
        {
            if (string.IsNullOrWhiteSpace(queryOption.Query)) continue;

            Console.WriteLine($"[Soulseek] Initiating search query (ContainsArtist={queryOption.ContainsArtist}): '{queryOption.Query}'");

            // 1. Throttle search requests to protect against Soulseek search bans
            await ThrottleSearchAsync(cancellationToken);

            // 2. Start search on Soulseek network
            var searchId = await _slskdHelper.SearchAsync(queryOption.Query, cancellationToken);
            if (searchId == null) continue;

            try
            {
                // 3. Wait for search responses (Slskd polling)
                var searchResult = await _slskdHelper.WaitForSearchResultsAsync(searchId.Value, 12, cancellationToken);
                if (searchResult?.Responses == null || searchResult.Responses.Count == 0)
                {
                    Console.WriteLine($"[Soulseek] No search responses for query '{queryOption.Query}', trying next tier...");
                    continue;
                }

                Console.WriteLine($"[Soulseek] Received {searchResult.Responses.Count} responses for query '{queryOption.Query}'.");

                // 4. Try format cascade order (FLAC -> WAV -> MP3 -> ANY)
                foreach (var format in formatOrder)
                {
                    if (!FormatExtensions.TryGetValue(format, out var allowedExtensions))
                        continue;

                    var bestMatch = FindBestDirectoryForFormat(release, queryOption.ContainsArtist, searchResult.Responses, allowedExtensions);
                    if (bestMatch != null)
                    {
                        Console.WriteLine($"[Soulseek] Found {format} match from user '{bestMatch.Value.username}' in folder '{bestMatch.Value.directoryPath}' ({bestMatch.Value.files.Count} valid tracks). Enqueuing...");

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

                Console.WriteLine($"[Soulseek] No format tier matched for query '{queryOption.Query}'.");
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

    public async Task CleanUpAsync(string sourceIdentifier, bool deleteFiles)
    {
        var parts = sourceIdentifier.Split(':');
        if (parts.Length < 3) return;

        var username = parts[2];
        await _slskdHelper.RemoveCompletedTransfersAsync(username);
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

    // ─── Multi-Tier Search Queries & Directory Candidate Evaluation ───

    private List<SearchQueryOption> GenerateSearchQueries(ReleaseToDownload release)
    {
        var queries = new List<SearchQueryOption>();

        var rawArtist = release.Artist;
        var rawTitle = release.Title;

        var cleanArtist = CleanSearchQuery(rawArtist);
        var fullTitle = CleanSearchQuery(rawTitle);
        var coreTitle = GetCoreTitle(rawTitle);

        var normalizedArtist = CleanSearchQuery(RemoveDiacriticsAndNormalizeSymbols(rawArtist));
        var normalizedFullTitle = CleanSearchQuery(RemoveDiacriticsAndNormalizeSymbols(rawTitle));
        var normalizedCoreTitle = CleanSearchQuery(RemoveDiacriticsAndNormalizeSymbols(coreTitle));

        // Tier 1: Artist + Full Title (e.g. Kendrick Lamar To Pimp A Butterfly)
        if (!string.IsNullOrWhiteSpace(cleanArtist) && !string.IsNullOrWhiteSpace(fullTitle))
            queries.Add(new SearchQueryOption { Query = $"{cleanArtist} {fullTitle}", ContainsArtist = true });

        // Tier 1b: Diacritic/Symbol Normalized Artist + Full Title (e.g. Motley Crue / Bjork)
        if (!string.IsNullOrWhiteSpace(normalizedArtist) && !string.IsNullOrWhiteSpace(normalizedFullTitle))
            queries.Add(new SearchQueryOption { Query = $"{normalizedArtist} {normalizedFullTitle}", ContainsArtist = true });

        // Tier 2: Artist + Core Title (strips Deluxe/Remastered/Bonus info)
        if (!string.IsNullOrWhiteSpace(cleanArtist) && !string.IsNullOrWhiteSpace(coreTitle))
            queries.Add(new SearchQueryOption { Query = $"{cleanArtist} {coreTitle}", ContainsArtist = true });

        // Tier 2b: Normalized Artist + Core Title
        if (!string.IsNullOrWhiteSpace(normalizedArtist) && !string.IsNullOrWhiteSpace(normalizedCoreTitle))
            queries.Add(new SearchQueryOption { Query = $"{normalizedArtist} {normalizedCoreTitle}", ContainsArtist = true });

        // Tier 3: Full Title Only (Omits Artist to find folders like Music/To Pimp A Butterfly/01.flac)
        if (!string.IsNullOrWhiteSpace(fullTitle))
            queries.Add(new SearchQueryOption { Query = fullTitle, ContainsArtist = false });

        // Tier 3b: Normalized Full Title Only
        if (!string.IsNullOrWhiteSpace(normalizedFullTitle))
            queries.Add(new SearchQueryOption { Query = normalizedFullTitle, ContainsArtist = false });

        // Tier 4: Core Title Only
        if (!string.IsNullOrWhiteSpace(coreTitle))
            queries.Add(new SearchQueryOption { Query = coreTitle, ContainsArtist = false });

        // Tier 4b: Normalized Core Title Only
        if (!string.IsNullOrWhiteSpace(normalizedCoreTitle))
            queries.Add(new SearchQueryOption { Query = normalizedCoreTitle, ContainsArtist = false });

        // Deduplicate queries
        var seen = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
        var result = new List<SearchQueryOption>();
        foreach (var q in queries)
        {
            if (!string.IsNullOrWhiteSpace(q.Query) && seen.Add(q.Query))
            {
                result.Add(q);
            }
        }
        return result;
    }

    private (string username, string directoryPath, List<SlskdFileDownloadRequest> files)? FindBestDirectoryForFormat(
        ReleaseToDownload release,
        bool queryContainsArtist,
        List<SlskdSearchResponse> responses,
        HashSet<string> allowedExtensions)
    {
        var candidates = new List<CandidateDirectory>();

        var normalizedArtist = CleanSearchQuery(RemoveDiacriticsAndNormalizeSymbols(release.Artist)).ToLowerInvariant();
        var normalizedAlbum = CleanSearchQuery(RemoveDiacriticsAndNormalizeSymbols(release.Title)).ToLowerInvariant();
        var normalizedCoreAlbum = GetCoreTitle(RemoveDiacriticsAndNormalizeSymbols(release.Title)).ToLowerInvariant();
        bool isTargetVariousArtists = normalizedArtist.Contains("various") || normalizedArtist == "va";

        var artistTokens = Regex.Split(normalizedArtist, @"\W+").Where(t => t.Length >= 3).ToList();

        foreach (var response in responses)
        {
            if (response.Username == null || response.Files == null || response.Files.Count == 0)
                continue;

            // Group response files by shared Directory path
            var dirGroups = response.Files
                .GroupBy(f => GetDirectoryPath(f.Filename ?? ""))
                .Where(g => !string.IsNullOrWhiteSpace(g.Key));

            foreach (var group in dirGroups)
            {
                var dirPath = group.Key;

                // Filter valid audio files in this specific directory
                var validAudioFiles = group
                    .Where(f => f.Filename != null &&
                               !f.IsLocked &&
                               f.Size >= MinAudioFileSizeBytes &&
                               f.Size <= MaxAudioFileSizeBytes &&
                               allowedExtensions.Contains(GetExtension(f.Filename)))
                    .Select(f => new SlskdFileDownloadRequest { Filename = f.Filename!, Size = f.Size })
                    .ToList();

                if (validAudioFiles.Count == 0) continue;

                var fullPathSearchString = CleanSearchQuery(RemoveDiacriticsAndNormalizeSymbols(dirPath + " " + string.Join(" ", validAudioFiles.Select(f => f.Filename)))).ToLowerInvariant();

                // Exclude compilation folders (VA / Various Artists) if searching for a specific artist
                if (!isTargetVariousArtists && (fullPathSearchString.Contains(@"\va ") || fullPathSearchString.Contains("va - ") || fullPathSearchString.Contains("various artists")))
                {
                    continue;
                }

                // Calculate Artist Matching Score
                int artistScore = Fuzz.PartialRatio(normalizedArtist, fullPathSearchString);
                bool artistTokenInPath = artistTokens.Count > 0 && artistTokens.Any(token => fullPathSearchString.Contains(token));
                bool artistMatched = artistScore >= 60 || artistTokenInPath;

                // Calculate Album Matching Score
                int albumScore = Fuzz.PartialRatio(normalizedAlbum, fullPathSearchString);
                int coreAlbumScore = !string.IsNullOrWhiteSpace(normalizedCoreAlbum)
                    ? Fuzz.PartialRatio(normalizedCoreAlbum, fullPathSearchString)
                    : 0;
                int maxAlbumScore = Math.Max(albumScore, coreAlbumScore);

                if (maxAlbumScore < 45) continue;

                // Strict Artist Verification for Title-Only search queries
                if (!queryContainsArtist && !isTargetVariousArtists)
                {
                    if (!artistMatched && artistScore < 50)
                    {
                        // Skip candidate if artist cannot be verified in path/filenames for title-only queries
                        continue;
                    }
                }

                int combinedScore = (int)(artistScore * 0.35 + maxAlbumScore * 0.65);
                if (artistMatched) combinedScore += 15;

                // Audio Quality & Bitrate Scoring across files in directory
                int avgQualityScore = (int)group.Average(f => CalculateAudioQualityScore(f, GetExtension(f.Filename ?? "")));
                combinedScore += avgQualityScore;

                // Peer Free Upload Slots & Speed Weighting
                if (response.FreeUploadSlots > 0)
                {
                    combinedScore += 30; // Immediate download start bonus!
                }
                else
                {
                    combinedScore -= 20; // Queued download penalty
                }

                if (response.UploadSpeed > 1024 * 1024) // > 1 MB/s speed
                {
                    combinedScore += 10;
                }

                // Track completeness bonus for multi-track album directories
                if (validAudioFiles.Count >= 4) combinedScore += 15;
                else if (validAudioFiles.Count >= 2) combinedScore += 5;

                // Bonus for directories containing cover art or logs
                bool hasMetadataFiles = group.Any(f => f.Filename != null &&
                    (f.Filename.EndsWith(".jpg", StringComparison.OrdinalIgnoreCase) ||
                     f.Filename.EndsWith(".png", StringComparison.OrdinalIgnoreCase) ||
                     f.Filename.EndsWith(".jpeg", StringComparison.OrdinalIgnoreCase) ||
                     f.Filename.EndsWith(".cue", StringComparison.OrdinalIgnoreCase) ||
                     f.Filename.EndsWith(".log", StringComparison.OrdinalIgnoreCase)));
                if (hasMetadataFiles) combinedScore += 5;

                candidates.Add(new CandidateDirectory
                {
                    Username = response.Username,
                    DirectoryPath = dirPath,
                    Files = validAudioFiles,
                    Score = combinedScore,
                    UploadSpeed = response.UploadSpeed,
                    HasFreeSlots = response.FreeUploadSlots > 0
                });
            }
        }

        if (candidates.Count == 0) return null;

        var bestCandidate = candidates
            .Where(c => c.Score >= 50)
            .OrderByDescending(c => c.Score)
            .ThenByDescending(c => c.HasFreeSlots)
            .ThenByDescending(c => c.FileCount)
            .ThenByDescending(c => c.UploadSpeed)
            .FirstOrDefault();

        if (bestCandidate != null)
        {
            return (bestCandidate.Username, bestCandidate.DirectoryPath, bestCandidate.Files);
        }

        return null;
    }

    private static int CalculateAudioQualityScore(SlskdFile file, string extension)
    {
        int score = 0;
        var extLower = extension.ToLowerInvariant();

        bool isLossless = extLower == ".flac" || extLower == ".wav" || extLower == ".alac";

        if (isLossless)
        {
            // Fake FLAC check: If a FLAC file is under 5MB, it's likely a short/corrupt file or fake transcode
            if (file.Size < 5 * 1024 * 1024)
            {
                score -= 15;
            }
            else
            {
                score += 15;
            }

            // Hi-Res Audiophile Detection
            if (file.BitDepth >= 24 || file.SampleRate >= 88200)
            {
                score += 10; // Hi-Res bonus
            }
        }
        else if (extLower == ".mp3" || extLower == ".m4a" || extLower == ".aac" || extLower == ".ogg" || extLower == ".opus")
        {
            if (file.BitRate.HasValue)
            {
                if (file.BitRate >= 315) score += 20;       // 320 kbps MP3
                else if (file.BitRate >= 250) score += 10;  // V0 / 256 kbps MP3
                else if (file.BitRate < 190) score -= 20;   // Low quality < 192 kbps
            }
        }

        return score;
    }

    private static string RemoveDiacriticsAndNormalizeSymbols(string text)
    {
        if (string.IsNullOrWhiteSpace(text)) return "";

        // Replace common symbol representations
        text = text.Replace("&", " and ")
                   .Replace("+", " and ")
                   .Replace("$", "s");

        // Remove diacritics / accents
        var normalizedString = text.Normalize(NormalizationForm.FormD);
        var stringBuilder = new StringBuilder(capacity: normalizedString.Length);

        foreach (var c in normalizedString)
        {
            var unicodeCategory = CharUnicodeInfo.GetUnicodeCategory(c);
            if (unicodeCategory != UnicodeCategory.NonSpacingMark)
            {
                stringBuilder.Append(c);
            }
        }

        return stringBuilder.ToString().Normalize(NormalizationForm.FormC);
    }

    private static string CleanSearchQuery(string query)
    {
        if (string.IsNullOrWhiteSpace(query)) return "";
        var cleaned = Regex.Replace(query, @"[\[\({].*?[\]\)}]", " ");
        cleaned = Regex.Replace(cleaned, @"[-_'\.]", " ");
        cleaned = Regex.Replace(cleaned, @"\s+", " ").Trim();
        return cleaned;
    }

    private static string GetCoreTitle(string title)
    {
        if (string.IsNullOrWhiteSpace(title)) return "";
        var cleaned = Regex.Replace(title, @"[\(\[\{](?:deluxe|remaster|reissue|anniversary|edition|bonus|explicit|expanded|version|vol\.?\s*\d+|feat\.?|ft\.?).*?[\)\]\}]", "", RegexOptions.IgnoreCase);
        cleaned = Regex.Replace(cleaned, @"\b(?:deluxe|remastered|anniversary edition|expanded edition|special edition)\b", "", RegexOptions.IgnoreCase);
        return CleanSearchQuery(cleaned);
    }

    private static string GetDirectoryPath(string filename)
    {
        if (string.IsNullOrWhiteSpace(filename)) return "";
        var lastSlash = Math.Max(filename.LastIndexOf('/'), filename.LastIndexOf('\\'));
        if (lastSlash < 0) return filename;
        return filename.Substring(0, lastSlash);
    }

    private static string GetExtension(string filename)
    {
        var lastDot = filename.LastIndexOf('.');
        if (lastDot < 0 || lastDot == filename.Length - 1) return "";
        return filename.Substring(lastDot);
    }
}
