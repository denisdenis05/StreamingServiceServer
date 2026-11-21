using Microsoft.EntityFrameworkCore;
using Npgsql;
using StreamingServiceServer.Business.Helpers;
using StreamingServiceServer.Business.Models.Library;
using StreamingServiceServer.Business.Models.MusicSearch;
using StreamingServiceServer.Business.Services.Images;
using StreamingServiceServer.Business.Services.MusicSearch;
using StreamingServiceServer.Data;
using StreamingServiceServer.Data.Models;
using Microsoft.Extensions.Configuration;

namespace StreamingServiceServer.Business.Services.Recommendations;

public sealed class AudioRecommendations(StreamingDbContext database, IAudioRecommendationFeatures features,
    RecommendationRanking ranking, IRecommendationCursorCodec cursors, TimeProvider clock,
    IImageService images, ILibraryService library, IConfiguration configuration, IRecommendationEntropy entropy) : IAudioRecommendations
{
    private const string InvalidRequest = "Choose a supported recommendation mode and valid seed, page size or playlist.";
    private const string InvalidSession = "This radio session expired or changed. Start it again.";
    private const string MissingPlaylist = "Playlist not found.";
    private const string MusicPath = "Music:Path";
    private const string TemporaryFolder = "TEMP";
    private const string Explanation = "Similar sound with a varied selection of artists and albums.";
    private const string FallbackExplanation = "A varied selection from available music.";
    private const int MaximumNameLength = 160;
    public async Task<RecommendationOverview> OverviewAsync(CancellationToken cancellationToken) =>
        new((await features.GetAsync(cancellationToken)).Features.Count, RecommendationPlaylistCatalog.Cards);

    public async Task<RecommendationPage> GenerateAsync(Guid userId, RecommendationRequest request, CancellationToken cancellationToken)
    {
        Validate(request);
        var snapshot = await features.GetAsync(cancellationToken);
        var session = request.ContinuationToken is null ? null : cursors.Decode(request.ContinuationToken);
        if (session is not null && (session.UserId != userId || session.ExpiresAt <= clock.GetUtcNow()
            || session.PolicyVersion != RecommendationPolicy.PolicyVersion)) throw new RecommendationInputException(InvalidSession, RecommendationPolicy.ExpiredSessionCode);
        var activeRequest = session?.Request ?? request with { ContinuationToken = null };
        var exclusions = (session?.Excluded ?? []).Concat(request.Exclude ?? []).Distinct().ToHashSet();
        if (exclusions.Count > RecommendationPolicy.MaximumExclusions) throw new RecommendationInputException(InvalidSession, RecommendationPolicy.ExpiredSessionCode);
        var randomSeed = session?.RandomSeed ?? request.RandomSeed ?? entropy.Create();
        var available = snapshot.Features.ToList();
        if (activeRequest.Mode is RecommendationPolicy.SongRadio or RecommendationPolicy.ArtistRadio && activeRequest.RecordingId is { } seedId && available.All(candidate => candidate.RecordingId != seedId))
            await AddMetadataFallbackAsync(available, [seedId], cancellationToken);
        if (activeRequest.PlaylistId is { } playlistId)
        {
            var playlistIds = await OwnedPlaylistRecordingsAsync(userId, playlistId, cancellationToken);
            if (activeRequest.Mode == RecommendationPolicy.SmartShuffle)
            {
                available = available.Where(candidate => playlistIds.Contains(candidate.RecordingId)).ToList();
                await AddMetadataFallbackAsync(available, playlistIds, cancellationToken);
            }
        }
        if (available.Count == 0 && activeRequest.Mode == RecommendationPolicy.SmartShuffle)
            await AddMetadataFallbackAsync(available, await database.Recordings.AsNoTracking().Select(recording => recording.Id).ToArrayAsync(cancellationToken), cancellationToken);
        var seeds = session is null ? await ResolveSeedsAsync(userId, activeRequest, available, randomSeed, cancellationToken)
            : available.Where(candidate => session.Seeds.Contains(candidate.RecordingId)).ToArray();
        if (activeRequest.Mode == RecommendationPolicy.SmartShuffle && seeds.FirstOrDefault() is { } shuffleAnchor)
            available = available.Select(candidate => candidate.Cohort == shuffleAnchor.Cohort ? candidate
                : candidate with { Cohort = shuffleAnchor.Cohort, Vector = new float[RecommendationPolicy.EmbeddingDimensions], Descriptors = new Dictionary<string, double>() }).ToList();
        if (session is null && activeRequest.Mode != RecommendationPolicy.SmartShuffle)
            exclusions.UnionWith(await RecentAsync(userId, cancellationToken));
        var anchor = session is null && activeRequest.Mode is RecommendationPolicy.SongRadio or RecommendationPolicy.SmartShuffle ? seeds.FirstOrDefault() : null;
        if (anchor is not null) exclusions.Remove(anchor.RecordingId);
        var excludedHashes = snapshot.Features.Where(candidate => exclusions.Contains(candidate.RecordingId)).Select(candidate => candidate.ContentHash).ToHashSet();
        var candidates = available.Where(candidate => !exclusions.Contains(candidate.RecordingId) && !excludedHashes.Contains(candidate.ContentHash)).ToList();
        if (anchor is not null) candidates.RemoveAll(candidate => candidate.ContentHash == anchor.ContentHash);
        var ranked = ranking.Select(candidates, seeds, activeRequest.Mode, randomSeed, RecommendationPolicy.MaximumPageSize * 2, anchor is null ? [] : [anchor]);
        var ordered = anchor is null ? ranked.ToArray() : new[] { anchor }.Concat(ranked).ToArray();
        var checkedCandidates = ordered.Take(RecommendationPolicy.MaximumPageSize * 2).ToArray();
        var playable = await ResponsesAsync(checkedCandidates.Select(candidate => candidate.RecordingId).ToArray(), cancellationToken, playableOnly: true);
        var recordings = playable.Take(request.Count).ToList();
        var liked = await library.GetLikedRecordingIdsAsync(userId, recordings.Select(recording => recording.Id).ToArray(), cancellationToken);
        foreach (var recording in recordings) recording.IsLiked = liked.Contains(recording.Id);
        var playableIds = playable.Select(recording => recording.Id).ToHashSet();
        exclusions.UnionWith(checkedCandidates.Where(candidate => !playableIds.Contains(candidate.RecordingId)).Select(candidate => candidate.RecordingId));
        exclusions.UnionWith(recordings.Select(recording => recording.Id));
        var emittedHashes = available.Where(candidate => exclusions.Contains(candidate.RecordingId)).Select(candidate => candidate.ContentHash).ToHashSet();
        var remaining = available.Any(candidate => !exclusions.Contains(candidate.RecordingId) && !emittedHashes.Contains(candidate.ContentHash)
            && (seeds.Length == 0 || candidate.Cohort == seeds[0].Cohort));
        var continuation = remaining && recordings.Count > 0 && exclusions.Count < RecommendationPolicy.MaximumExclusions ? cursors.Encode(new RecommendationSession(userId,
            activeRequest with { Exclude = null, ContinuationToken = null }, randomSeed, seeds.Select(seed => seed.RecordingId).ToArray(),
            exclusions.ToArray(), session?.ExpiresAt ?? clock.GetUtcNow() + RecommendationPolicy.SessionLifetime, RecommendationPolicy.PolicyVersion)) : null;
        return new RecommendationPage(recordings, continuation, seeds.Any(seed => seed.Vector.Any(value => value != 0)) ? Explanation : FallbackExplanation, snapshot.Generation, activeRequest.Mode);
    }

    public async Task<DailyMixResponse> DailyAsync(Guid userId, Guid? expectedVersion, CancellationToken cancellationToken)
    {
        var day = DateOnly.FromDateTime(clock.GetUtcNow().UtcDateTime);
        var previous = await database.RecommendationDailyMixes.AsNoTracking().SingleOrDefaultAsync(mix => mix.UserId == userId && mix.Day == day, cancellationToken);
        if (expectedVersion is null && previous is not null)
            return new DailyMixResponse(previous.Version, day, await WithLikesAsync(userId, await ResponsesAsync(previous.RecordingIds, cancellationToken), cancellationToken), previous.SavedPlaylistId);
        if (expectedVersion is not null && previous?.Version != expectedVersion) throw new RecommendationConflictException();
        var seed = entropy.Create();
        var snapshot = await features.GetAsync(cancellationToken);
        var seeds = await TasteSeedsAsync(userId, snapshot.Features, cancellationToken);
        var recent = await RecentAsync(userId, cancellationToken);
        const int PreviousDays = 7;
        var previousSelections = await database.RecommendationDailyMixes.AsNoTracking().Where(mix => mix.UserId == userId && mix.Day < day && mix.Day >= day.AddDays(-PreviousDays))
            .Select(mix => mix.RecordingIds).ToArrayAsync(cancellationToken);
        var previouslySelected = previousSelections.SelectMany(identifiers => identifiers).ToHashSet();
        var candidates = snapshot.Features.Where(candidate => !recent.Contains(candidate.RecordingId) && !previouslySelected.Contains(candidate.RecordingId)).ToArray();
        if (candidates.Length < RecommendationPolicy.DailySize)
            candidates = snapshot.Features.Where(candidate => !recent.Contains(candidate.RecordingId)).ToArray();
        var ordered = ranking.Select(candidates, seeds, RecommendationPolicy.Daily, seed, RecommendationPolicy.DailySize);
        var playable = await ResponsesAsync(ordered.Select(candidate => candidate.RecordingId).ToArray(), cancellationToken, playableOnly: true);
        await WithLikesAsync(userId, playable, cancellationToken);
        var recordingIds = playable.Select(recording => recording.Id).ToArray();
        if (recordingIds.Length == 0 && previous is null) return new DailyMixResponse(seed, day, playable, null);
        if (previous is null)
        {
            var created = new RecommendationDailyMix { UserId = userId, Day = day, Version = seed, RecordingIds = recordingIds, CreatedAt = clock.GetUtcNow() };
            database.RecommendationDailyMixes.Add(created);
            try { await database.SaveChangesAsync(cancellationToken); }
            catch (DbUpdateException failure) when (failure.InnerException is PostgresException { SqlState: PostgresErrorCodes.UniqueViolation })
            {
                database.Entry(created).State = EntityState.Detached;
                return await DailyAsync(userId, null, cancellationToken);
            }
        }
        else
        {
            var changed = await database.RecommendationDailyMixes.Where(mix => mix.UserId == userId && mix.Day == day && mix.Version == expectedVersion)
                .ExecuteUpdateAsync(update => update.SetProperty(mix => mix.Version, seed).SetProperty(mix => mix.RecordingIds, recordingIds)
                    .SetProperty(mix => mix.SavedPlaylistId, (Guid?)null).SetProperty(mix => mix.CreatedAt, clock.GetUtcNow()), cancellationToken);
            if (changed == 0) throw new RecommendationConflictException();
        }
        return new DailyMixResponse(seed, day, playable, null);
    }

    public async Task<PlaylistResponse> SaveAsync(Guid userId, SaveRecommendationPlaylist request, CancellationToken cancellationToken)
    {
        if (string.IsNullOrWhiteSpace(request.Name) || request.Name.Length > MaximumNameLength || request.RecordingIds is null
            || request.RecordingIds.Length == 0 || request.RecordingIds.Length > RecommendationPolicy.MaximumPageSize
            || request.RecordingIds.Any(identifier => identifier == Guid.Empty)) throw new RecommendationInputException(InvalidRequest);
        await using var transaction = await database.Database.BeginTransactionAsync(cancellationToken);
        RecommendationDailyMix? daily = null;
        if (request.DailyVersion is { } version)
        {
            var day = DateOnly.FromDateTime(clock.GetUtcNow().UtcDateTime);
            var locked = await database.RecommendationDailyMixes.Where(mix => mix.UserId == userId && mix.Day == day && mix.Version == version)
                .ExecuteUpdateAsync(update => update.SetProperty(mix => mix.Version, version), cancellationToken);
            if (locked == 0) throw new RecommendationConflictException();
            daily = await database.RecommendationDailyMixes.AsNoTracking().SingleAsync(mix => mix.UserId == userId && mix.Day == day, cancellationToken);
            if (daily.SavedPlaylistId is { } savedId)
            {
                var saved = await database.Playlists.AsNoTracking().SingleOrDefaultAsync(playlist => playlist.Id == savedId && playlist.OwnerId == userId, cancellationToken);
                if (saved is not null) return new PlaylistResponse { Id = saved.Id, Name = saved.Name, Cover = saved.Cover, RecordingCount = daily.RecordingIds.Length };
            }
        }
        var identifiers = daily?.RecordingIds ?? request.RecordingIds.Distinct().ToArray();
        if (await database.Recordings.AsNoTracking().CountAsync(recording => identifiers.Contains(recording.Id), cancellationToken) != identifiers.Length)
            throw new RecommendationInputException("Some tracks are no longer available. Refresh this selection before saving.");
        var created = await library.CreatePlaylistAsync(userId, new CreatePlaylistRequest { Name = request.Name.Trim(), RecordingIds = identifiers.ToList() }, cancellationToken);
        if (daily is not null)
            await database.RecommendationDailyMixes.Where(mix => mix.UserId == userId && mix.Day == daily.Day && mix.Version == daily.Version)
                .ExecuteUpdateAsync(update => update.SetProperty(mix => mix.SavedPlaylistId, (Guid?)created.Id), cancellationToken);
        await transaction.CommitAsync(cancellationToken);
        return new PlaylistResponse { Id = created.Id, Name = created.Name, Cover = created.Cover, CreatedAt = created.CreatedAt, RecordingCount = identifiers.Length };
    }

    private async Task<AudioRecommendationFeature[]> ResolveSeedsAsync(Guid userId, RecommendationRequest request,
        List<AudioRecommendationFeature> available, Guid randomSeed, CancellationToken cancellationToken)
    {
        if (request.Mode == RecommendationPolicy.SongRadio)
        {
            if (request.RecordingId is null) throw new RecommendationInputException(InvalidRequest);
            return available.Where(candidate => candidate.RecordingId == request.RecordingId).ToArray();
        }
        if (request.Mode == RecommendationPolicy.ArtistRadio)
        {
            var artistId = request.ArtistId ?? available.FirstOrDefault(candidate => candidate.RecordingId == request.RecordingId)?.ArtistId;
            if (artistId is null) throw new RecommendationInputException(InvalidRequest);
            return available.Where(candidate => candidate.ArtistId == artistId).GroupBy(candidate => candidate.ReleaseId)
                .OrderBy(group => group.Key).Select(group => group.OrderBy(candidate => RecommendationRanking.Jitter(randomSeed, candidate.RecordingId)).First())
                .Take(RecommendationPolicy.MaximumSeeds).ToArray();
        }
        if (request.Mode == RecommendationPolicy.PlaylistRadio)
        {
            if (request.PlaylistId is not { } playlistId) throw new RecommendationInputException(InvalidRequest);
            var identifiers = await OwnedPlaylistRecordingsAsync(userId, playlistId, cancellationToken);
            return available.Where(candidate => identifiers.Contains(candidate.RecordingId)).GroupBy(candidate => candidate.ArtistId)
                .Select(group => group.First()).Take(RecommendationPolicy.MaximumSeeds).ToArray();
        }
        if (request.Mode == RecommendationPolicy.SmartShuffle)
        {
            var analyzed = available.Where(candidate => candidate.Vector.Any(value => value != 0)).ToArray();
            return (analyzed.Length > 0 ? analyzed : available.ToArray()).OrderBy(candidate => RecommendationRanking.Jitter(randomSeed, candidate.RecordingId)).Take(1).ToArray();
        }
        return await TasteSeedsAsync(userId, available, cancellationToken);
    }

    private async Task<AudioRecommendationFeature[]> TasteSeedsAsync(Guid userId, IReadOnlyList<AudioRecommendationFeature> available, CancellationToken cancellationToken)
    {
        var liked = await library.GetLikedRecordingIdsAsync(userId, cancellationToken);
        var listened = await database.Listens.AsNoTracking().Where(listen => listen.UserId == userId && !listen.HasAnomalies
            && listen.ValidatedPlayedSeconds >= RecommendationPolicy.QualifiedListenSeconds).OrderByDescending(listen => listen.StartTime)
            .Select(listen => listen.RecordingId).Distinct().Take(RecommendationPolicy.RecentLimit).ToArrayAsync(cancellationToken);
        return available.Where(candidate => liked.Contains(candidate.RecordingId) || listened.Contains(candidate.RecordingId))
            .OrderByDescending(candidate => liked.Contains(candidate.RecordingId)).ThenBy(candidate => candidate.RecordingId)
            .GroupBy(candidate => candidate.ArtistId).Select(group => group.First()).Take(RecommendationPolicy.MaximumSeeds).ToArray();
    }

    private async Task<HashSet<Guid>> RecentAsync(Guid userId, CancellationToken cancellationToken) =>
        (await database.Listens.AsNoTracking().Where(listen => listen.UserId == userId && listen.ValidatedPlayedSeconds >= RecommendationPolicy.QualifiedListenSeconds)
            .OrderByDescending(listen => listen.StartTime).Select(listen => listen.RecordingId).Take(RecommendationPolicy.RecentLimit).ToArrayAsync(cancellationToken)).ToHashSet();

    private async Task<Guid[]> OwnedPlaylistRecordingsAsync(Guid userId, Guid playlistId, CancellationToken cancellationToken)
    {
        if (!await database.Playlists.AnyAsync(playlist => playlist.Id == playlistId && playlist.OwnerId == userId, cancellationToken))
            throw new KeyNotFoundException(MissingPlaylist);
        return await database.PlaylistRecordings.Where(row => row.PlaylistId == playlistId).OrderBy(row => row.Order).ThenBy(row => row.RecordingId)
            .Select(row => row.RecordingId).ToArrayAsync(cancellationToken);
    }

    private async Task AddMetadataFallbackAsync(List<AudioRecommendationFeature> available, Guid[] identifiers, CancellationToken cancellationToken)
    {
        var present = available.Select(candidate => candidate.RecordingId).ToHashSet();
        var missing = identifiers.Where(identifier => !present.Contains(identifier)).ToArray();
        var rows = await database.Recordings.AsNoTracking().Where(recording => missing.Contains(recording.Id))
            .Select(recording => new { recording.Id, ReleaseId = recording.Release.Id, ArtistId = recording.Release.Artist.Id }).ToArrayAsync(cancellationToken);
        var cohort = available.GroupBy(candidate => candidate.Cohort).OrderByDescending(group => group.Count()).FirstOrDefault()?.Key ?? RecommendationPolicy.SmartShuffle;
        available.AddRange(rows.Select(row => new AudioRecommendationFeature(row.Id, row.ReleaseId, row.ArtistId, row.Id.ToString(), cohort,
            new float[RecommendationPolicy.EmbeddingDimensions], new Dictionary<string, double>(), 0, 0)));
    }

    private async Task<List<RecordingResponse>> WithLikesAsync(Guid userId, List<RecordingResponse> recordings, CancellationToken cancellationToken)
    {
        var liked = await library.GetLikedRecordingIdsAsync(userId, recordings.Select(recording => recording.Id).ToArray(), cancellationToken);
        foreach (var recording in recordings) recording.IsLiked = liked.Contains(recording.Id);
        return recordings;
    }

    private async Task<List<RecordingResponse>> ResponsesAsync(Guid[] identifiers, CancellationToken cancellationToken, bool playableOnly = false)
    {
        var recordings = await database.Recordings.AsNoTracking().Include(recording => recording.Release).ThenInclude(release => release.Artist)
            .Where(recording => identifiers.Contains(recording.Id)).ToDictionaryAsync(recording => recording.Id, cancellationToken);
        var result = new List<RecordingResponse>();
        foreach (var identifier in identifiers)
        {
            cancellationToken.ThrowIfCancellationRequested();
            if (!recordings.TryGetValue(identifier, out var recording)) continue;
            var response = recording.ToResponse(images);
            if (!playableOnly || IsPlayable(response)) result.Add(response);
        }
        return result;
    }

    private bool IsPlayable(RecordingResponse recording)
    {
        var root = configuration[MusicPath];
        if (string.IsNullOrWhiteSpace(root) || !Directory.Exists(root)) return false;
        try
        {
            return MusicLocationHelper.EnumerateRecordingFiles(root, recording).Any(path => Path.GetFileNameWithoutExtension(path) == recording.Id.ToString()
                && SupportedExtensions.Contains(Path.GetExtension(path))) || SupportedExtensions.Any(extension => File.Exists(Path.Combine(root, TemporaryFolder, recording.Id + extension)));
        }
        catch (Exception failure) when (failure is IOException or UnauthorizedAccessException or InvalidOperationException) { return false; }
    }

    private static readonly HashSet<string> SupportedExtensions = new(StringComparer.OrdinalIgnoreCase) { ".flac", ".mp3", ".opus", ".ogg", ".wav", ".m4a", ".aac" };
    private static void Validate(RecommendationRequest request)
    {
        if (!RecommendationPolicy.Modes.Contains(request.Mode) || request.Count <= 0 || request.Count > RecommendationPolicy.MaximumPageSize
            || request.Exclude?.Length > RecommendationPolicy.MaximumExclusions || request.Exclude?.Any(identifier => identifier == Guid.Empty) == true
            || request.RecordingId == Guid.Empty || request.PlaylistId == Guid.Empty || request.ArtistId == Guid.Empty
            || request.ContinuationToken?.Length > RecommendationPolicy.MaximumCursorCharacters) throw new RecommendationInputException(InvalidRequest);
    }
}
