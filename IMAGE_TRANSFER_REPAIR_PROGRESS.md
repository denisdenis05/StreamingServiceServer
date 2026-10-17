# Image transfer and cleanup repairs

Implemented on 2026-10-17 alongside the two server logic/performance agents. See [logic progress](SERVER_IMAGE_LOGIC_PROGRESS.md), [performance progress](SERVER_IMAGE_PERFORMANCE_PROGRESS.md) and the original [review](IMAGE_CACHE_REVIEW.md).

## Server transfer ownership

- Shared source downloads have their own bounded lifetime. Canceling a waiter does not cancel other consumers. Abandoned task failures are observed and completed operations leave the sharing dictionaries.
- Each source operation and import has a total deadline; each body read has an idle deadline. Job processing also has an outer deadline across fallback candidates. The lease exceeds the job deadline.
- HTTP failures carry permanent/transient classification and Retry-After. Expired mappings retain ETag/Last-Modified and can reuse originals after a 304 response. Missing or corrupt originals suppress conditional requests so they can actually be recovered.
- Rendering is shared by content identity within one server process. A private snapshot keeps the input alive if a consumer cancels. Cached valid variants return without another input snapshot or decode. The renderer produces all three variants with one FFmpeg invocation.
- Originals become permanent only after decoding succeeds. Failed manifest publication removes newly installed files; existing assets are preserved. Temporary cleanup releases every active-path marker even if deletion fails, and logs the deferred cleanup.

## Server cleanup and admission

- Cleanup recognizes import, derivative, original-copy and JSON temporary patterns. Temporaries use a separate one-hour default grace, rather than one-year asset retention, and active local writers are protected.
- Production cleanup advances a directory cursor by at most CleanupBatchSize files per call. Referenced identities are checked through indexed job fields; manifests are not loaded for the entire catalog.
- Recently reused/published identities receive a lease-length grace while their database references are handed off. Cleanup rechecks protection and timestamps after asynchronous reference checks.
- Imports have bounded running and waiting admission. Storage reserves conservative working capacity before reading, tracks finalized-file changes and checks the filesystem free-space floor. The first byte snapshot is streamed once, excludes temporary working files and retries after a failed scan.
- Storage limits are enforced per server instance. Independent external writers and multiple server replicas do not share a reservation ledger; free-space checks are not a distributed quota. Kernel stalls on a hard NFS mount cannot be forcibly canceled by a .NET token.

Defaults: OperationTimeout two minutes, BodyIdleTimeout fifteen seconds, MaximumPendingImports 32, TemporaryFileRetention one hour, CleanupBatchSize 100, MaximumStoredBytes 10 GiB and MinimumFreeBytes 256 MiB. Reservations are conservative and may reject a large new operation before the finalized-file budget is completely occupied. Existing files are retained according to their publication/retention policy; no automatic destructive quota purge was introduced.

## Tests and builds

- Full server suite: 91 tests passed, including disposable PostgreSQL transactions/migrations and real FFmpeg checks. No production database was accessed.
- Transfer-specific tests cover first-waiter cancellation, body/whole-operation deadlines, failure classification, conditional requests, failed decode/publication cleanup, all temporary patterns, bounded cleanup, admission recovery, malformed manifests and publication grace.
- Storage-specific tests cover failed initial scan recovery and preventing mutation accounting before the initial snapshot.
- Release publish passed to `/tmp/streaming-cover-hardened-publish`. Existing nullable and relational-version warnings remain.

## Deployment and remaining checks

Apply [deployment/harden-cover-asset-scheduling.sql](deployment/harden-cover-asset-scheduling.sql) after the previous AddCoverAssetJobs migration before starting this server. It contains the four additive scheduling, indexing, integrity and backfill migrations. For databases predating AddCoverAssetJobs, apply that migration first.

Measure real NAS latency, process RSS, disk growth and provider behavior after deployment. Render sharing, storage reservations and provider pacing are per instance; a multi-replica deployment still needs a distributed render/admission/provider policy if duplicate cross-process work is unacceptable. No production migration, deployment or Nginx testing was performed. Local appsettings and IDE changes were preserved.
