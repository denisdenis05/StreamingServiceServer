using System.Globalization;
using System.Text;
using System.Text.RegularExpressions;
using FuzzySharp;
using StreamingServiceDownloader.Models;
using StreamingServiceDownloader.Models.Slskd;
using StreamingServiceDownloader.Models.Soulseek;
using StreamingServiceServer.Business.Helpers;
using StreamingServiceServer.Data.Models;

namespace StreamingServiceDownloader.Helpers;

public class SoulseekProvider : IDownloadProvider
{
    private readonly SlskdHelper _slskdHelper;
    private string[] _artistAliases = [];
    private int _expectedTrackCount;
    private readonly StreamingServiceServer.Business.Services.MusicSearch.IExternalMusicSearchService? _metadataSource;

    private static readonly SemaphoreSlim SearchRateLimiter = new(1, 1);
    private static DateTime _lastSearchTime = DateTime.MinValue;
    private static readonly TimeSpan MinInterSearchDelay = TimeSpan.FromSeconds(6);

    private const long MinAudioFileSizeBytes = 500 * 1024;        
    private const long MaxAudioFileSizeBytes = 200 * 1024 * 1024; 

    private static readonly Dictionary<string, HashSet<string>> FormatExtensions = new(StringComparer.OrdinalIgnoreCase)
    {
        ["FLAC"] = new(StringComparer.OrdinalIgnoreCase) { ".flac" },
        ["WAV"]  = new(StringComparer.OrdinalIgnoreCase) { ".wav" },
        ["MP3"]  = new(StringComparer.OrdinalIgnoreCase) { ".mp3" },
        ["ANY"]  = new(StringComparer.OrdinalIgnoreCase) { ".flac", ".wav", ".mp3", ".m4a", ".aac", ".ogg", ".alac", ".wma", ".opus" }
    };

    public string ProviderName => "Soulseek";

    public SoulseekProvider(SlskdHelper slskdHelper, StreamingServiceServer.Business.Services.MusicSearch.IExternalMusicSearchService? metadataSource = null)
    {
        _slskdHelper = slskdHelper;
        _metadataSource = metadataSource;
    }

    public void ApplyConfig(DownloadProviderConfig config)
    {
        _slskdHelper.Configure(config.ApiUrl, config.ApiKey, config.Username, config.Password);
    }

    public async Task<DownloadSearchResult?> SearchAndEnqueueAsync(
        ReleaseToDownload release, string[] formatOrder, CancellationToken cancellationToken)
    {
        var expected = _metadataSource is null ? []
            : await _metadataSource.SearchAlbumRecordingsByIdAsync(release.Id).WaitAsync(cancellationToken);
        var expectedTracks = expected.Count;
        _expectedTrackCount = expectedTracks;
        _artistAliases = expected.SelectMany(recording => recording.Releases)
            .SelectMany(album => album.ArtistCredit ?? [])
            .SelectMany(credit => new[] { credit.Name, credit.Artist?.Name })
            .Where(name => !string.IsNullOrWhiteSpace(name)).Select(name => name!).Distinct(StringComparer.OrdinalIgnoreCase).ToArray();
        if (_metadataSource is not null && expectedTracks == 0)
            throw new HttpRequestException("Album metadata is unavailable; source selection will be retried.");
        var queries = GenerateSearchQueries(release);

        foreach (var queryOption in queries)
        {
            if (string.IsNullOrWhiteSpace(queryOption.Query)) continue;

            Console.WriteLine($"[Soulseek] Initiating search query (ContainsArtist={queryOption.ContainsArtist}): '{queryOption.Query}'");


            await ThrottleSearchAsync(cancellationToken);


            var searchId = await _slskdHelper.SearchAsync(queryOption.Query, cancellationToken);
            if (searchId == null) continue;

            bool downloadEnqueued = false;
            try
            {

                var searchResult = await _slskdHelper.WaitForSearchResultsAsync(searchId.Value, 90, cancellationToken);
                if (searchResult?.Responses == null || searchResult.Responses.Count == 0)
                {
                    Console.WriteLine($"[Soulseek] No search responses for query '{queryOption.Query}', trying next tier...");
                    continue;
                }

                Console.WriteLine($"[Soulseek] Received {searchResult.Responses.Count} responses for query '{queryOption.Query}'.");


                foreach (var format in formatOrder)
                {
                    if (!FormatExtensions.TryGetValue(format, out var allowedExtensions))
                        continue;

                    var candidates = FindCandidateDirectoriesForFormat(release, queryOption.ContainsArtist, searchResult.Responses, allowedExtensions);
                    foreach (var candidate in candidates)
                    {
                        Console.WriteLine($"[Soulseek] Trying {format} candidate user='{candidate.Username}' dir='{candidate.DirectoryPath}' score={candidate.Score} freeSlots={candidate.HasFreeSlots} ({candidate.Files.Count} tracks)...");

                        var selectedSource = new SlskdSourceIdentity(searchId.Value, candidate.Username,
                            candidate.DirectoryPath, candidate.Files);
                        try
                        {
                            await _slskdHelper.StartAlbumDownloadAsync(selectedSource, cancellationToken);
                            downloadEnqueued = true;
                            return new DownloadSearchResult
                            {
                                Success = true,
                                SourceIdentifier = selectedSource.Encode(),
                                SavePath = candidate.DirectoryPath
                            };
                        }
                        catch (Exception failure) when (failure is HttpRequestException or TaskCanceledException && !cancellationToken.IsCancellationRequested)
                        {
                            Console.WriteLine($"[Soulseek] Enqueue could not be verified for '{candidate.Username}': {failure.Message}");
                        }
                    }
                }

                Console.WriteLine($"[Soulseek] No format tier matched for query '{queryOption.Query}'.");
            }
            finally
            {
                if (!downloadEnqueued)
                    await _slskdHelper.DeleteSearchAsync(searchId.Value, cancellationToken);
            }
        }

        Console.WriteLine($"[Soulseek] All queries exhausted for '{release.Artist} - {release.Title}'. No suitable download found.");
        return null;
    }

    public async Task<DownloadProgressInfo?> CheckProgressAsync(
        string sourceIdentifier, CancellationToken cancellationToken)
    {
        var source = SlskdSourceIdentity.Parse(sourceIdentifier);
        if (source is null) return null;
        var downloads = await _slskdHelper.GetDownloadsAsync(cancellationToken);
        if (downloads is null) return null;
        return ReadProgress(source, downloads);
    }

    public static DownloadProgressInfo ReadProgress(SlskdSourceIdentity source, IReadOnlyList<SlskdTransferUser> downloads)
    {
        var user = downloads.FirstOrDefault(candidate => candidate.Username == source.Username);
        var matches = (user?.Directories ?? []).SelectMany(directory => (directory.Files ?? [])
            .Where(file => source.Matches(directory, file)).Select(file => (Directory: directory, File: file))).ToArray();
        if (matches.Length == 0)
            return new DownloadProgressInfo { Progress = 0, SavePath = source.DirectoryPath, State = "NoTransfersFound" };

        var savePath = matches.Select(match => match.Directory.Directory).FirstOrDefault(directory => !string.IsNullOrWhiteSpace(directory))
            ?? source.DirectoryPath;
        var expectedMatches = source.Files.Count > 0
            ? source.Files.Select(expected => matches.Where(match =>
                (source with { Files = new[] { expected } }).Matches(match.Directory, match.File))
                .Select(match => match.File).ToArray()).ToArray()
            : matches.Select(match => new[] { match.File }).ToArray();
        var allComplete = expectedMatches.All(files => files.Any(IsCompletedTransfer));
        var allFailed = expectedMatches.All(files => files.Length > 0 && files.All(SlskdSourceIdentity.IsFailed));
        var progress = expectedMatches.Average(files => files.Where(file => !SlskdSourceIdentity.IsFailed(file))
            .Select(file => Math.Clamp(file.PercentComplete ?? 0, 0, 100)).DefaultIfEmpty(0).Max()) / 100;
        return new DownloadProgressInfo
        {
            Progress = allComplete ? 1 : Math.Min(progress, 0.999), SavePath = savePath,
            State = allComplete ? "Completed" : allFailed ? "Failed" : "Downloading"
        };
    }

    private static bool IsCompletedTransfer(SlskdTransferItem file) =>
        file.State?.Contains("Completed", StringComparison.OrdinalIgnoreCase) == true && !SlskdSourceIdentity.IsFailed(file);

    public async Task CleanUpAsync(string sourceIdentifier, bool deleteFiles)
    {
        var source = SlskdSourceIdentity.Parse(sourceIdentifier);
        if (source is null || string.IsNullOrWhiteSpace(source.DirectoryPath))
            throw new InvalidOperationException("Cannot clean up Soulseek transfers without an album directory.");
        await _slskdHelper.RemoveSelectedTransfersAsync(source, CancellationToken.None);
    }

    public async Task<bool> TryClearCompletedTransfersAsync(string sourceIdentifier)
    {
        try
        {
            await CleanUpAsync(sourceIdentifier, true);
            return true;
        }
        catch (Exception failure) when (failure is HttpRequestException or TaskCanceledException or System.Text.Json.JsonException)
        {
            return false;
        }
    }

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


        if (!string.IsNullOrWhiteSpace(cleanArtist) && !string.IsNullOrWhiteSpace(fullTitle))
            queries.Add(new SearchQueryOption { Query = $"{cleanArtist} {fullTitle}", ContainsArtist = true });


        if (!string.IsNullOrWhiteSpace(normalizedArtist) && !string.IsNullOrWhiteSpace(normalizedFullTitle))
            queries.Add(new SearchQueryOption { Query = $"{normalizedArtist} {normalizedFullTitle}", ContainsArtist = true });


        if (!string.IsNullOrWhiteSpace(cleanArtist) && !string.IsNullOrWhiteSpace(coreTitle))
            queries.Add(new SearchQueryOption { Query = $"{cleanArtist} {coreTitle}", ContainsArtist = true });


        if (!string.IsNullOrWhiteSpace(normalizedArtist) && !string.IsNullOrWhiteSpace(normalizedCoreTitle))
            queries.Add(new SearchQueryOption { Query = $"{normalizedArtist} {normalizedCoreTitle}", ContainsArtist = true });


        if (!string.IsNullOrWhiteSpace(fullTitle))
            queries.Add(new SearchQueryOption { Query = fullTitle, ContainsArtist = false });


        if (!string.IsNullOrWhiteSpace(normalizedFullTitle))
            queries.Add(new SearchQueryOption { Query = normalizedFullTitle, ContainsArtist = false });


        if (!string.IsNullOrWhiteSpace(coreTitle))
            queries.Add(new SearchQueryOption { Query = coreTitle, ContainsArtist = false });


        if (!string.IsNullOrWhiteSpace(normalizedCoreTitle))
            queries.Add(new SearchQueryOption { Query = normalizedCoreTitle, ContainsArtist = false });


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

    private List<CandidateDirectory> FindCandidateDirectoriesForFormat(
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

        Console.WriteLine($"[Soulseek][Match] Target: artist='{normalizedArtist}' album='{normalizedAlbum}' core='{normalizedCoreAlbum}' artistTokens=[{string.Join(",", artistTokens)}] responses={responses.Count}");

        int totalDirs = 0;
        int skippedNoAudio = 0;
        int skippedVa = 0;
        int skippedAlbumScore = 0;
        int skippedArtistScore = 0;

        foreach (var response in responses)
        {
            if (response.Username == null || response.Files == null || response.Files.Count == 0)
                continue;

            var dirGroups = response.Files
                .GroupBy(f => GetDirectoryPath(f.Filename ?? ""))
                .Where(g => !string.IsNullOrWhiteSpace(g.Key));

            foreach (var group in dirGroups)
            {
                var dirPath = group.Key;
                totalDirs++;

                var allFilesInGroup = group.ToList();
                var validAudioFiles = allFilesInGroup
                    .Where(f => f.Filename != null &&
                               !f.IsLocked &&
                               f.Size >= MinAudioFileSizeBytes &&
                               f.Size <= MaxAudioFileSizeBytes &&
                               allowedExtensions.Contains(GetExtension(f.Filename)))
                    .Select(f => new SlskdFileDownloadRequest { Filename = f.Filename!, Size = f.Size })
                    .ToList();

                if (validAudioFiles.Count < _expectedTrackCount) continue;
                if (validAudioFiles.Count == 0)
                {
                    var rejectedFiles = allFilesInGroup.Where(f => f.Filename != null).Take(3)
                        .Select(f => $"{Path.GetFileName(f.Filename ?? "")}(ext={GetExtension(f.Filename ?? "")},size={f.Size},locked={f.IsLocked})");
                    Console.WriteLine($"[Soulseek][Match] SKIP no-audio: '{dirPath}' totalFiles={allFilesInGroup.Count} sample=[{string.Join(", ", rejectedFiles)}]");
                    skippedNoAudio++;
                    continue;
                }

                var fullPathSearchString = CleanSearchQuery(RemoveDiacriticsAndNormalizeSymbols(dirPath + " " + string.Join(" ", validAudioFiles.Select(f => f.Filename)))).ToLowerInvariant();

                if (!isTargetVariousArtists && (fullPathSearchString.Contains(@"\va ") || fullPathSearchString.Contains("va - ") || fullPathSearchString.Contains("various artists")))
                {
                    Console.WriteLine($"[Soulseek][Match] SKIP va-filter: '{dirPath}'");
                    skippedVa++;
                    continue;
                }

                if (!(isTargetVariousArtists
                    ? AudioMatchPolicy.ContainsName(dirPath, release.Title) || AudioMatchPolicy.ContainsName(dirPath, normalizedCoreAlbum)
                    : AudioMatchPolicy.MatchesAlbumPath(release.Artist, release.Title, dirPath)
                        || _artistAliases.Any(alias => AudioMatchPolicy.MatchesAlbumPath(alias, release.Title, dirPath))))
                {
                    skippedArtistScore++;
                    continue;
                }

                int artistScore = Math.Max(Fuzz.TokenSetRatio(normalizedArtist, fullPathSearchString), Fuzz.PartialRatio(normalizedArtist, fullPathSearchString));
                bool artistTokenInPath = artistTokens.Count > 0 && artistTokens.Any(token => fullPathSearchString.Contains(token));
                bool artistMatched = artistScore >= 60 || artistTokenInPath;

                int albumScore = Math.Max(Fuzz.TokenSetRatio(normalizedAlbum, fullPathSearchString), Fuzz.PartialRatio(normalizedAlbum, fullPathSearchString));
                int coreAlbumScore = !string.IsNullOrWhiteSpace(normalizedCoreAlbum)
                    ? Math.Max(Fuzz.TokenSetRatio(normalizedCoreAlbum, fullPathSearchString), Fuzz.PartialRatio(normalizedCoreAlbum, fullPathSearchString))
                    : 0;
                int maxAlbumScore = Math.Max(albumScore, coreAlbumScore);

                Console.WriteLine($"[Soulseek][Match] DIR '{dirPath}' user='{response.Username}' audioFiles={validAudioFiles.Count} artistScore={artistScore} artistMatched={artistMatched} albumScore={maxAlbumScore} pathString='{fullPathSearchString.Substring(0, Math.Min(120, fullPathSearchString.Length))}'");

                if (maxAlbumScore < 45)
                {
                    Console.WriteLine($"[Soulseek][Match] SKIP album-score<45: '{dirPath}' albumScore={maxAlbumScore}");
                    skippedAlbumScore++;
                    continue;
                }

                int combinedScore = (int)(artistScore * 0.35 + maxAlbumScore * 0.65);
                if (artistMatched) combinedScore += 15;

                int avgQualityScore = (int)group.Average(f => CalculateAudioQualityScore(f, GetExtension(f.Filename ?? "")));
                combinedScore += avgQualityScore;

                if (response.FreeUploadSlots > 0)
                    combinedScore += 30;
                else
                    combinedScore -= 20;

                if (response.UploadSpeed > 1024 * 1024)
                    combinedScore += 10;

                if (validAudioFiles.Count >= 4) combinedScore += 15;
                else if (validAudioFiles.Count >= 2) combinedScore += 5;

                bool hasMetadataFiles = group.Any(f => f.Filename != null &&
                    (f.Filename.EndsWith(".jpg", StringComparison.OrdinalIgnoreCase) ||
                     f.Filename.EndsWith(".png", StringComparison.OrdinalIgnoreCase) ||
                     f.Filename.EndsWith(".jpeg", StringComparison.OrdinalIgnoreCase) ||
                     f.Filename.EndsWith(".cue", StringComparison.OrdinalIgnoreCase) ||
                     f.Filename.EndsWith(".log", StringComparison.OrdinalIgnoreCase)));
                if (hasMetadataFiles) combinedScore += 5;

                Console.WriteLine($"[Soulseek][Match] CANDIDATE '{dirPath}' user='{response.Username}' combinedScore={combinedScore} freeSlots={response.FreeUploadSlots > 0}");

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

        Console.WriteLine($"[Soulseek][Match] Summary: totalDirs={totalDirs} skippedNoAudio={skippedNoAudio} skippedVa={skippedVa} skippedAlbumScore={skippedAlbumScore} skippedArtistScore={skippedArtistScore} candidates={candidates.Count}");

        var ranked = candidates
            .Where(c => c.Score >= 50)
            .OrderByDescending(c => c.Score)
            .ThenByDescending(c => c.HasFreeSlots)
            .ThenByDescending(c => c.FileCount)
            .ThenByDescending(c => c.UploadSpeed)
            .ToList();

        if (ranked.Count == 0)
        {
            Console.WriteLine($"[Soulseek][Match] No candidate passed score>=50 threshold. Best scores: {string.Join(", ", candidates.OrderByDescending(c => c.Score).Take(5).Select(c => $"'{c.DirectoryPath}'={c.Score}"))}");
            return ranked;
        }

        Console.WriteLine($"[Soulseek][Match] {ranked.Count} candidate(s) ranked. Best: '{ranked[0].DirectoryPath}' user='{ranked[0].Username}' score={ranked[0].Score}");
        return ranked;
    }

    private static int CalculateAudioQualityScore(SlskdFile file, string extension)
    {
        int score = 0;
        var extLower = extension.ToLowerInvariant();

        bool isLossless = extLower == ".flac" || extLower == ".wav" || extLower == ".alac";

        if (isLossless)
        {

            if (file.Size < 5 * 1024 * 1024)
            {
                score -= 15;
            }
            else
            {
                score += 15;
            }


            if (file.BitDepth >= 24 || file.SampleRate >= 88200)
            {
                score += 10; 
            }
        }
        else if (extLower == ".mp3" || extLower == ".m4a" || extLower == ".aac" || extLower == ".ogg" || extLower == ".opus")
        {
            if (file.BitRate.HasValue)
            {
                if (file.BitRate >= 315) score += 20;       
                else if (file.BitRate >= 250) score += 10;  
                else if (file.BitRate < 190) score -= 20;   
            }
        }

        return score;
    }

    private static string RemoveDiacriticsAndNormalizeSymbols(string text)
    {
        if (string.IsNullOrWhiteSpace(text)) return "";

        text = StringSanitizers.NormalizeSpecialCharacters(text);

        text = text.Replace("&", " and ")
                   .Replace("+", " and ")
                   .Replace("$", "s");

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
        cleaned = Regex.Replace(cleaned, @"[-_'/\\.]", " ");
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
