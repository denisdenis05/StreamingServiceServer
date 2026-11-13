# Recommendations and playlist artwork

Implemented on 2026-11-13. Server and mobile changes belong to their separate repositories. No player-package or Jam-package API changes are required.

## Recommendation services

The authenticated `api/recommendations` API supports:

| Route | Behavior |
| --- | --- |
| GET overview | Available modes and analyzed-track coverage |
| POST generate | Song radio, artist radio, playlist continuation, library/playlist Smart shuffle, calmer, energetic and instrumental selections |
| GET daily | Stable per-account daily mix |
| POST daily/regenerate | Explicit regeneration with the expected mix version |
| POST playlists | Save an ordered selection as a normal editable playlist; daily saves are idempotent |

Every action requires a valid account identity and the existing User/Admin bearer authentication. Playlist seed and Smart shuffle scopes are checked against the owner. The indexed recording catalog remains shared, matching existing catalog access; this feature does not make private playlists public. Cursors are encrypted/authenticated by ASP.NET Data Protection, tied to the account, bounded in size and track exclusions, and expire after six hours. The client can restart an expired cursor once with its original seed and accumulated exclusions; repeated failure returns to the ordinary backoff/visible retry flow.

`AudioRecommendationFeatures` caches normalized 200-value vectors and model-specific descriptors. Startup warming happens in a background service. Two-minute refreshes fetch new/changed analysis rows and a small identifier set for deletions; full metadata refresh happens every thirty minutes. Concurrent refreshes share one load. Different feature/model cohorts are never compared as if they were compatible. Smart shuffle can retain tracks from other cohorts as metadata-only candidates. The pinned 3.7.1 outer descriptor sigmoid is reversed in memory; stored analysis is unchanged.

`RecommendationRanking` uses SIMD cosine similarity, multiple anchors, stable seed-dependent variation, content-hash deduplication and a diversity penalty. Radio/mix pages cap artist and album repetition, including a pinned initial seed. Smart shuffle can use every eligible playlist song even when all tracks belong to one album. An analyzed starting song is preferred when available; unanalyzed playlist entries remain metadata candidates. Ordinary native shuffle stays separate.

Artist radio selects representative tracks from several albums. Song radio keeps its original recording anchor across pages. Playlist continuation uses several artists in the owned playlist as anchors. Daily/mood seeds come from the user's designated liked playlist and qualified, non-anomalous listens. Daily selection reduces recent playback and previous-week daily overlap, relaxing previous-day exclusion when the library is too small. Descriptor/energy mixes are estimates, not categorical genre or mood guarantees. Existing feedback data does not support learning trustworthy dislikes from historical skips, so no negative taste inference was added.

Only physical supported music files are returned for new selections. Selection does not start preview downloads or audio inference. Sparse libraries may produce fewer tracks than the requested size. Daily results are persisted, including recording order and the saved-playlist reference. Regeneration clears that reference and creates a new version without editing the already saved playlist. The day boundary is UTC. Mix rows belong to users and are removed with their account; deleting a saved playlist clears its reference.

Migrations: `20261113165137_AddRecommendationDailyMixes` adds the daily mix table and keys/indexes; `20261113172933_AddPlaylistArtworkVersion` adds the playlist artwork concurrency token. EF migration/designer/snapshot comments and framework literals are generated output, outside authored-source rules.

## Local playlist images

`PlaylistArtworkService` takes covers from the first four ordered songs, using locally stored recording/release covers. It performs no remote image requests. Missing local covers among those songs are skipped and available tiles repeat when fewer than four are ready. If no usable local cover exists, no new image is generated. Empty playlists clear generated artwork. A custom cover is preserved.

`PlaylistCollageRenderer` uses the existing local FFmpeg executable and one admitted render at a time. The two-by-two square is at most 500 pixels and no larger than the smallest source's shorter side. The JPEG also cannot exceed the smallest source file in bytes: quality is reduced first, then dimensions. No tile is enlarged. If these limits cannot be met, no oversized image is published. Images are validated before decoding, metadata is stripped, operations have a total timeout and child processes are killed on cancellation.

Published filenames depend on the ordered recordings, local source versions and artwork policy version. Publication is atomic; the first four recordings and previous cover are checked again before updating the playlist. Playlist-entry saves bump the artwork version and clear generated covers in the same transaction. Publication compares that version, including edits that race with a render, so a stale render cannot overwrite a newer playlist. Generated assets are served through the existing image route. Old unreferenced collages are pruned in bounded batches after seven days; existing source assets are untouched. Filesystem free-space limits are checked before rendering. The existing cover-store quota is not a reservation shared with this separate renderer; concurrent cover downloads still require operational disk headroom.

Playlist creation preserves caller order. The original implementation reordered recording IDs through an unordered database query; this has been corrected. Listing library/playlists queues artwork work without blocking on rendering. A bounded, deduplicating background queue refreshes existing playlists. Newly created generated playlists try to produce their artwork immediately; an explicit owner-checked artwork endpoint refreshes it on playlist viewing. The queue is recovered through subsequent library listings after a server restart. Immutable cover addresses change when artwork changes, so image caches can retain the previous asset safely.

## Mobile integration

Home has a Recommendations section linked to a dedicated screen. Song menus include song and artist radio. Playlist menus include Smart shuffle and playlist radio. The screen exposes daily mixes, explicit regeneration, alternate selections, ordered track playback and save-as-playlist with an editable name. The generated cover is passed through to the saved playlist view.

The continuation controller uses the existing native runtime and queue-change lifecycle. It prefetches near the end, appends instead of replacing manual edits, excludes queued/seen recording IDs, and fences late HTTP and source-preparation completions. Ordinary play/playlist/clear/shuffle commands cancel the recommendation owner. Queue replacement and account changes cancel it. Native ordinary shuffle is disabled for a recommendation-ordered queue and can be explicitly re-enabled by the user. Starting radio from the song already playing preserves that playback and existing queued entries, adding new candidates after them.

The generic random-suggestion fallback is suppressed while a recommendation session owns the queue. Failures have bounded retries and a visible retry/stop control on the player. A serialized account/queue-bound local session snapshot restores continuation when the same native queue is recovered. Otherwise it is discarded. Generation and owner checks protect screen loads, saves and playback. All queue mutations use the existing Jam-aware lifecycle; successful appends still publish their receipts after their own native view changes.

## Verification

- Server focused and regression tests: **55 passed, zero skipped**, using isolated disposable PostgreSQL databases under `/tmp`. This includes daily migration application, account isolation, cohort/deduplication checks, initial seed ordering, cursor tampering/account fences and expiry, playlist ownership/scope, physical file eligibility, cache reuse/incremental refresh/deletion/invalid rows, stable daily generation, concurrent regeneration/save, immutable saved order, collage ordering/bounds, fewer-cover behavior, cache invalidation, artwork ownership and raced publication/invalidation.
- The collage integration tests execute actual local FFmpeg and inspect the resulting dimensions, byte size and quadrant pixels. Other artwork service tests use injected renderers.
- Mobile `npm run test:recommendations`: **86 controller/player tests and 15 screen tests passed**. Dedicated tests cover continuation races, manual edits during HTTP/source preparation, stopped initial loads, account changes, duplicate responses, bounded retries, sparse buffers, local restoration/write ordering, daily screen loading/regeneration/save and safe errors. Runtime tests cover the append generation guard and Jam queue receipts after commit.
- Mobile `npx tsc --noEmit --pretty false`: passed with no TypeScript errors. The focused recommendation and player configurations also compile.
- Server API compilation passes. Existing unrelated server warnings remain.
- EF pending-model check: no changes since the new migration.
- A real-database cache/ranking probe used `default_transaction_read_only=on` in connection startup. It loaded 11,978 compatible features, selected twenty candidates locally and confirmed a warm call reused the same snapshot. No personal histories or track metadata were printed. This was a feature-cache read test, not a live generated-playlist mutation or playback test.

Commands and logs are recorded in this session under `/tmp/recommendations-server-regression.log`, `/tmp/recommendations-mobile-all-tests.log`, `/tmp/recommendations-mobile-types.log`, `/tmp/recommendations-pending-model.log` and `/tmp/recommendation-cache-live.log`. Production processing, Spotify processes and native sessions were left running. No production migrations, writes, restarts or deployments were performed.

## Deployment and verification still required

Deploy the server migration/API and update the mobile app together. Keep the existing Data Protection key volume writable by the configured server user; cursors use that same persistent key ring. No AudioMuse model change or reprocessing is needed.

A new APK was not packaged for this chapter. Emulator/phone verification remains postponed: actual audio start/transition behavior, background/headless delivery, process-recovery on a real native session, Jam participant queue propagation and artwork appearance need device checks. The unit/integration tests above establish source behavior, not those device outcomes. Relevance weights and mood labels also need listening evaluation; no listener-satisfaction improvement is claimed.
