# Cover asset repairs

Implemented locally on 2026-10-17. No commits or deployment. User settings in appsettings.json/appsettings.Development.json and .idea are untouched.

## Implemented

- One validated Cover Art Archive URL builder accepts root and legacy release-prefixed configuration. Known duplicated archive prefixes are repaired in returned URLs and when jobs publish. Unrelated hosts are not rewritten.
- Library, metadata and dashboard responses prefer locally generated covers through the same selection policy. Owned local paths are projected onto the current public origin; external artwork keeps its original address. Prior manifests retain original assets while replacement jobs run, allowing old-origin uploads to be restored without fetching the old host.
- `CoverAssetStore` downloads a bounded stream once per source, validates signatures and invokes FFmpeg decoding. It retains a checked original and generates full, small and thumbnail JPEGs. Default maximum dimensions are 1400, 500 and 250; images are not enlarged. Derivative identity includes original content and resize configuration, so changed uploads/settings receive new immutable URLs. Outputs and manifests publish through same-directory atomic renames.
- Cached source manifests avoid repeated HTTP requests, including when recordings share a release image. Missing/corrupt outputs are repaired from the retained original. Current versioned local URLs reuse their asset set without another download/re-encode. Source URLs are rechecked after one day by default.
- A SaveChanges interceptor enqueues CoverAssetJobs in the metadata transaction. The hosted worker polls every five seconds with bounded parallelism, database leases, optimistic version checks, persisted retries and failure state. Delayed work cannot overwrite a changed source. Jobs survive application recreation. Filtered backfill and completed-asset repair replace the two independent six-hour jobs; old hosted-service implementations were removed.
- Pending count, oldest enqueue time and failures are logged. Completed files are verified on a configurable repair interval. Missing jobs are backfilled in bounded batches rather than delaying the first processing pass for the whole catalog.
- Uploaded covers are decoded and resized before publishing. A conditional transaction rejects an upload if another cover update won during processing. Job publication also guards the original source and lease/version in its transaction. Release cover changes propagate to recordings sharing the earlier artwork.
- Unreferenced managed assets expire after one year by default; currently referenced assets/originals and user legacy filenames are retained. Source maps and asset checks use hashes, sizes and completion metadata, not remote basenames or existence alone.
- ASP.NET sets immutable caching only for versioned filenames and a short revalidating policy for legacy names. The external Nginx template matches this policy. No Nginx configuration tests were run, as instructed.

## Migration and deployment

Apply `20261016190024_AddCoverAssetJobs` before starting this API version. [deployment/cover-asset-jobs.sql](deployment/cover-asset-jobs.sql) is the idempotent script for the additive migration after AddTrackMatchColumns. It creates CoverAssetJobs and its due-work index and records migration history. Use your normal database migration tool against the intended production database; no production connection or database was used here.

The existing Docker runtime already installs FFmpeg, which is now also used for cover generation. `Cover:RendererExecutable` defaults to `ffmpeg`; a custom deployment needs that executable. `Cover:Path` must point at a writable mounted directory for the container user. The API and external Nginx must see the same generated files at their respective configured filesystem paths. Nothing here remounts NFS or manipulates containers on NAS failure: failed jobs retain retry state and retry when storage returns.

Default configuration lives in CoverAssetOptions and can be overridden through the existing ASP.NET environment convention (`Cover__ParallelJobs`, `Cover__JobPollInterval`, `Cover__RepairInterval`, `Cover__FullDimension`, `Cover__SmallDimension`, `Cover__ThumbnailDimension`, `Cover__RendererExecutable` and the size/retention/timeout fields). Domain and Cover:DomainExtension must describe the public image origin/path. Compose and the environment template now use the archive root; legacy release-prefixed external cover configuration is still supported.

Do not give legacy mutable filenames a year-long immutable policy. Old versioned URLs remain immutable when settings change; unreferenced versions are retained for the configured retention period.

## Tests

`StreamingServiceServer.CoverTests` is part of the solution. Default `dotnet test` skips the PostgreSQL category when COVER_TEST_POSTGRES is absent. For integration, use a disposable PostgreSQL cluster listening only on a /tmp Unix socket and set COVER_TEST_POSTGRES accordingly; the test fixture refuses other hosts, creates uniquely named databases and drops only those databases.

Executed:

- `COVER_TEST_POSTGRES='Host=/tmp;Port=55439;Database=postgres' dotnet test StreamingServiceServer.CoverTests --no-restore`: 38 passed, including eleven PostgreSQL cases, actual migration on a fresh database, job transaction rollback, persisted retries, competing workers, stale job completion, file repair and Library/metadata response selection.
- Dedicated policy/storage tests cover URL roots, safe prefix repair, local fallback, shared downloads, basename collisions, invalid/oversized inputs, damaged files, source reuse, renderer failure, changed upload/settings identities and immutable serving policy.
- Real FFmpeg/ffprobe tests decode an 800x600 source, check every generated tier and reject a corrupt image. Retention tests preserve referenced and legacy files.
- `dotnet publish StreamingServiceServer.API --configuration Release --no-restore --output /tmp/streaming-cover-publish`: passed. Existing nullable and EF relational-version warnings remain; this chapter does not claim a dependency-audit pass.
- Mobile `npm run test:asset-cache`: 58 passed; `npm run test:player-mobile`: 319 passed including the subsequent mobile refresh chapter; full TypeScript and both platform JavaScript bundles passed. See ../StreamingServiceMobile/CACHE_REPAIR_PROGRESS.md.

The PostgreSQL cluster is a test fixture, not production. No deployed NAS path, external Nginx alias, production image timing or physical-device image memory was verified. No production database migration was applied. Native playback code was not changed.

## Commit authorization

The user authorized committing these completed changes on 2026-10-17. Earlier no-commit statements record the implementation phase. No push, deployment or production migration is included. Local server appsettings files and IDE settings are excluded.

## Follow-up performance and retention review

[IMAGE_CACHE_REVIEW.md](IMAGE_CACHE_REVIEW.md) records the 2026-10-17 source review requested after the implementation commit. It identifies body-download deadlines, maintenance blocking new work, repeated per-recording hashing, startup manifest materialization, temporary-file leaks and incomplete disk/resource admission. It also traces mobile RNFS listener/native-job retention and iOS cancellation settlement. These findings remain open. This review changed documentation only and did not run tests or measure production performance.

## Transfer, logic and performance repairs

The follow-up findings were implemented in three coordinated tracks. [Transfer repairs](IMAGE_TRANSFER_REPAIR_PROGRESS.md), [logic repairs](SERVER_IMAGE_LOGIC_PROGRESS.md) and [performance repairs](SERVER_IMAGE_PERFORMANCE_PROGRESS.md) record the current state. Final integrated suite: 91 server tests passed. The new additive scheduling/integrity/backfill migrations are required before deployment; local settings and IDE changes remain excluded. Native/mobile acceptance and actual NAS performance are separate gates.

## Missing covers follow-up

[MISSING_COVER_DIAGNOSIS.md](MISSING_COVER_DIAGNOSIS.md) records the screenshot investigation, strictly read-only database queries, the Android missing-destination regression and the release-to-group archive fallback. The non-PostgreSQL server suite passed 64 tests, including nine new recovery cases; Release publish passed. No production records or schema were changed. The inspected database does not yet have the cover-job tables, so automatic recovery requires deployment of the previously prepared migrations and updated server.

## Browser queue progress

[COVER_PROGRESS_DASHBOARD.md](COVER_PROGRESS_DASHBOARD.md) records the library-wide cover percentage, per-album progress/status, search, filters and pagination added to the browser queue page. The full server suite passed 116 tests, the browser controller suite passed 12 tests, and Release publish succeeded. No production database changes or deployment were performed.
