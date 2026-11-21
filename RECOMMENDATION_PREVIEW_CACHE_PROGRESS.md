# Recommendation preview cache

Recommendation playlist previews are stored in PostgreSQL per account and normalized request, including the ordered tracks and generated playlist cover. Entries expire at the next 00:00 UTC, following the existing daily-mix clock. Hits do not extend expiry. The existing maintenance worker removes expired payloads.

Cold fetches use a PostgreSQL advisory lock for the account and recheck the cache after obtaining it. Concurrent requests and separate server instances therefore share the first completed generation. Failed or canceled generation is not cached. Work that crosses midnight is not published into the new day's cache.

Warm responses refresh heart membership and daily saved-playlist status without ranking tracks or rendering covers. Cached radio cursors are renewed so their existing six-hour session deadline does not invalidate a playlist cached for the whole day. Explicit preview refresh bypasses and replaces its cache entry; daily regeneration invalidates daily previews. The Home collection endpoint reads the static playlist catalog without loading analysis features just to obtain titles.

Cache tests exposed an existing duplicate-save race caused by a tracked daily mix retaining an old saved-playlist identifier. Saving now reads the locked row without tracking and updates its saved identifier directly.

## Validation

- Targeted tests run against a disposable PostgreSQL instance using `COVER_TEST_POSTGRES` and `dotnet test StreamingServiceServer.CoverTests --no-restore --filter 'FullyQualifiedName~RecommendationPreviewCacheTests|FullyQualifiedName~RecommendationPlaylistPreviewTests|FullyQualifiedName~PlaylistArtworkTests|FullyQualifiedName~AudioRecommendationLogicTests'`.
- Tests cover persistent reuse across scopes, account and seed isolation, exact midnight expiry, non-sliding expiry, concurrent cold fetches, explicit refresh, cancellation, midnight-crossing generation, current hearts and saved state, renewed cursors, cleanup, and migration application. Controller coverage verifies that repeated Home fetches do not regenerate tracks or artwork.
- `dotnet ef migrations has-pending-model-changes --project StreamingServiceServer.Data --startup-project StreamingServiceServer.Data --no-build` checks the generated model.
- Mobile recommendation screen tests and TypeScript checking pass. Explicit new-selection actions send `refresh=true`; ordinary opens use the daily cache. The standalone ARM64 APK was rebuilt with this behavior.

Apply `AddRecommendationPlaylistCache` before deploying the server change. No production migration or deployment was performed. Tests establish avoided generation and rendering; production response latency has not been benchmarked.
