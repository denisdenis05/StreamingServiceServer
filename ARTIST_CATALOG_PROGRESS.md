# Artist catalog

## Implemented on 2026-11-14

- Authenticated `GET /api/artists/resolve?recordingId=...&albumId=...` resolves the local recording's album artist first, then the selected album. Missing local entries use exact external recording or release IDs. Names are never used to guess artist identity.
- Authenticated `GET /api/artists/{artistId}/albums?offset=0` returns 24 indexed releases in title/ID order and a continuation offset. It uses untracked reads and does not write metadata or enqueue downloads.
- `catalogSource=true` requests MusicBrainz release groups for the exact artist ID. Each group exposes an actual release ID for the existing album/import flow. Pages are cached for 15 minutes, and external calls use the existing MusicBrainz request pacing and retries. Failed pages are not cached as successful empty results.
- Invalid identifiers and offsets are rejected. Provider HTTP failures and non-request timeouts return 503. Caller cancellation propagates.
- Registered the artist service, external provider and memory cache in application composition. No schema changes are needed.

The provider follows the documented [MusicBrainz release-group search artist-ID field](https://musicbrainz.org/doc/MusicBrainz_API/Search/ReleaseGroupSearch). Catalog browsing is separate from local results so a provider outage does not block an indexed artist's library albums. Only primary album artists are resolved for indexed recordings. External recording credits use the first credited artist when the recording is not indexed; a multi-artist picker is outside this chapter.

## Verification

`ArtistCatalogTests` covers service/cache registration, successful cache reuse, cancellation before work, failed-cache recovery and HTTP status, input/authentication boundaries, exact external artist queries, navigable release IDs, artwork mapping and continuation offsets. A disposable PostgreSQL test also covers artist isolation when names match, deterministic local pagination, recording/album resolution, no external work for indexed artists and no tracked entities.

- `dotnet build StreamingServiceServer.API --no-restore`: passed, existing repository warnings remain.
- With `COVER_TEST_POSTGRES` pointing at a disposable `/tmp` Unix-socket database: `dotnet test StreamingServiceServer.CoverTests --no-restore --filter 'FullyQualifiedName~ArtistCatalogTests|FullyQualifiedName~SearchQueryOptimizationTests'`: 14 passed, none skipped.
- `git diff --check`: passed.

No production database was used by these tests. No server deployment, live MusicBrainz verification, APK packaging or device verification was performed. Deploy this server source together with the mobile artist-page source before checking the flow on a device. Preserve the unrelated configuration and research-note changes already present in this working tree.
