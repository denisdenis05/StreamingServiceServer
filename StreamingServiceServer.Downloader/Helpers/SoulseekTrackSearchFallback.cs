using StreamingServiceDownloader.Models;
using StreamingServiceDownloader.Models.Slskd;
using StreamingServiceDownloader.Models.Soulseek;
using StreamingServiceServer.Business.Models.MusicSearch;
using StreamingServiceServer.Data.Models;

namespace StreamingServiceDownloader.Helpers;

public sealed class SoulseekTrackSearchFallback(
    ISoulseekAlbumSearchClient searchClient,
    Func<CancellationToken, Task> waitForSearchSlot,
    Func<List<SlskdSearchResponse>, string[], int, List<CandidateDirectory>> rankCandidates,
    Func<string, string> cleanSearchQuery)
{
    private const int MaximumSeedTracks = 3;
    private const int SeedTrackCount = 1;
    private const int AvailableUploadSlots = 1;
    private const int NoUploadSlots = 0;
    private const char NormalizedPathSeparator = '/';
    private const int NoTrackMatchScore = 0;

    public async Task<DownloadSearchResult?> SearchAndEnqueueAsync(ReleaseToDownload release,
        IReadOnlyList<RecordingDto> recordings, string[] formatOrder, CancellationToken cancellationToken)
    {
        var orderedRecordings = recordings.OrderBy(recording => recording.PositionInAlbum).ToArray();
        var searchedQueries = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
        foreach (var recording in orderedRecordings.Take(MaximumSeedTracks))
        {
            cancellationToken.ThrowIfCancellationRequested();
            var query = cleanSearchQuery(recording.Title ?? string.Empty);
            if (string.IsNullOrWhiteSpace(query) || !searchedQueries.Add(query)) continue;
            await waitForSearchSlot(cancellationToken);
            var searchIdentifier = await searchClient.SearchAsync(query, cancellationToken);
            if (searchIdentifier is null) continue;
            var downloadEnqueued = false;
            try
            {
                var search = await searchClient.WaitForSearchResultsAsync(searchIdentifier.Value,
                    ISoulseekAlbumSearchClient.SearchTimeoutSeconds, cancellationToken);
                var seeds = rankCandidates(search?.Responses ?? [], formatOrder, SeedTrackCount);
                var expandedDirectories = new HashSet<(string Username, string Directory)>();
                foreach (var seed in seeds)
                {
                    cancellationToken.ThrowIfCancellationRequested();
                    if (!expandedDirectories.Add((seed.Username, SlskdSourceIdentity.NormalizePath(seed.DirectoryPath)))) continue;
                    try
                    {
                        var files = await searchClient.GetDirectoryFilesAsync(seed.Username, seed.DirectoryPath, cancellationToken);
                        var response = new SlskdSearchResponse
                        {
                            Username = seed.Username, Files = files,
                            FreeUploadSlots = seed.HasFreeSlots ? AvailableUploadSlots : NoUploadSlots,
                            UploadSpeed = seed.UploadSpeed
                        };
                        var expandedCandidates = rankCandidates([response], formatOrder, orderedRecordings.Length);
                        foreach (var candidate in expandedCandidates)
                        {
                            var selectedFiles = SelectRequiredTracks(release, orderedRecordings, candidate.Files);
                            if (selectedFiles is null) continue;
                            var source = new SlskdSourceIdentity(searchIdentifier.Value, candidate.Username,
                                candidate.DirectoryPath, selectedFiles);
                            try
                            {
                                await searchClient.StartAlbumDownloadAsync(source, cancellationToken);
                            }
                            catch (Exception failure) when (failure is HttpRequestException or TaskCanceledException
                                && !cancellationToken.IsCancellationRequested)
                            {
                                Console.WriteLine($"[Soulseek] Track search enqueue failed for '{candidate.Username}': {failure.Message}");
                                continue;
                            }
                            downloadEnqueued = true;
                            return new DownloadSearchResult
                                { Success = true, SourceIdentifier = source.Encode(), SavePath = source.DirectoryPath };
                        }
                    }
                    catch (Exception failure) when (failure is HttpRequestException or TaskCanceledException or System.Text.Json.JsonException
                        && !cancellationToken.IsCancellationRequested)
                    {
                        Console.WriteLine($"[Soulseek] Track search source '{seed.Username}' unavailable: {failure.Message}");
                    }
                }
            }
            finally
            {
                if (!downloadEnqueued)
                    await searchClient.DeleteSearchAsync(searchIdentifier.Value, CancellationToken.None);
            }
        }
        return null;
    }

    private static List<SlskdFileDownloadRequest>? SelectRequiredTracks(ReleaseToDownload release,
        IReadOnlyList<RecordingDto> recordings, IReadOnlyList<SlskdFileDownloadRequest> files)
    {
        var availableFiles = files.DistinctBy(file => SlskdSourceIdentity.NormalizePath(file.Filename)).ToList();
        var selectedFiles = new List<SlskdFileDownloadRequest>();
        foreach (var recording in recordings)
        {
            var expected = new RecordingResponse { Title = recording.Title, ArtistName = release.Artist };
            var selected = availableFiles.Select(file => (File: file, Score: AudioMatchPolicy.TrackScore(expected,
                    SlskdSourceIdentity.NormalizePath(file.Filename).Split(NormalizedPathSeparator).Last(), null)))
                .Where(match => match.Score > NoTrackMatchScore).OrderByDescending(match => match.Score)
                .Select(match => match.File).FirstOrDefault();
            if (selected is null) return null;
            availableFiles.Remove(selected);
            selectedFiles.Add(selected);
        }
        return selectedFiles;
    }
}
