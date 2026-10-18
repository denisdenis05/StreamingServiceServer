# Audio import safety

Updated 2026-10-18. Addresses F01–F03 from `../OPTIMIZATION_FOLLOWUP_ANALYSIS.md`, within O09.

## Implemented

All three pending-download completion paths now use `AudioAlbumImport`: normal provider completion, disk recovery after disappearing transfers, and torrent fallback. Existing import journals resume before querying provider progress, so cleanup retries do not depend on transfers still being listed. An unavailable non-torrent provider is retained rather than being sent to torrent cleanup.

Each pending download has an `ImportIdentifier`. Its journal identity includes album, provider, source and import identifiers. A later download with the same source name cannot reuse the old completed journal. Source/current-import checks fence publication and cleanup; transactional queue removal checks that same identity before removing the pending and queued rows.

The journal snapshots expected recordings and source directory, then records each completed publication. A partial album retains its source and queue. Restart verifies receipts by destination identity/length/modification time, skips healthy completed tracks and attempts only missing work. Empty/duplicate expected identities and malformed journals fail safely. Compilation albums use the album artist for storage, matching streaming lookup. Before cleanup, saved catalog identifiers and destination folders must match the admitted snapshot.

Files are copied to unique private staging names inside their destination directory. The staged length, unchanged source size/time, audio stream and source/staged SHA-256 checksums are checked before an atomic rename. Existing destination audio stays available until replacement succeeds. Cancellation and ordinary failures clean the operation's own staging file and preserve the source. Destination traversal and symbolic-link directories are rejected.

Only after all expected tracks are published, metadata is saved and cleanup readiness is checkpointed are committed source files removed. Source removal rechecks the published destination and source checksum; changed sources are retained. Provider cleanup follows, with its own completed checkpoint before database queue removal. Cleanup/database failures resume without rematching or copying healthy audio. Unmatched source files are not deleted by the file publisher; provider-wide cleanup retains its existing behavior after full album completion.

Journals are atomically replaced, flushed files under `<Music:Path>/.audio-import-state`. A held per-album file handle with exclusive sharing prevents cooperating import operations from running together on the tested filesystem. Source names are hashed for journal filenames. Journals/zero-length lock files remain for diagnosis; their retention policy is separate follow-up work. No database connection is stored in a journal.

Matching builds an attempt-local metadata inventory, reading each distinct candidate path once. Filename/tag matching then uses those results rather than reopening files inside every recording/candidate comparison. Cancellation is checked before metadata reads and between recordings. This inventory is not a process-global metadata cache.

## Deployment

Apply additive migration `20261018190000_IdentifyAudioImports` before running this server revision. It adds a non-null UUID import identifier to `PendingDownloads`; existing rows receive distinct values. The model default supports new database inserts, and application-created pending downloads get a fresh identifier too. The preceding cover migrations remain prerequisites for this branch.

The music mount must be writable for staging and journals. Deploy during a normal worker stop/restart so the old destructive import loop is not still running alongside the replacement. No production migration, container restart or deployment was performed here. Local settings and IDE changes were excluded.

## Tests

`AudioImportSafetyTests` adds 24 cases, including two disposable PostgreSQL cases:

- Partial/unmatched tracks and one failed publication keep the album incomplete; restart imports only missing tracks.
- Cleanup, metadata, queue and journal write failures preserve the appropriate checkpoint/source and retry stage.
- Missing published files are repaired before source cleanup.
- Staging remains invisible until completion; cancellation, truncation and invalid audio cannot replace the old destination.
- Real WAV validation, corrupt input, changed-source cleanup and unrelated source retention.
- Per-album exclusive lock, stale pending source, new import generation, root containment and symbolic-link rejection.
- Migration backfills distinct identities; stale queue removal cannot delete a replacement import, even with the same source name.
- Compilation artist paths, changed catalog/destination rejection and once-per-candidate metadata reads/cancellation.

Final commands/results:

- `COVER_TEST_POSTGRES='Host=/tmp;Port=55439;Database=postgres' dotnet test StreamingServiceServer.CoverTests --no-restore --nologo`: **179 passed**, zero failures/skips.
- `dotnet publish StreamingServiceServer.API --configuration Release --no-restore --output /tmp/audio-import-publish`: passed. Existing nullable and EF dependency-version warnings remain.
- `node --test tools/browser-player.test.cjs tools/dashboard-polling.test.cjs tools/cover-progress.test.cjs`: **44 passed**.
- `git diff --check`: passed.

Tests use generated local files, a real local TagLib metadata parser and disposable `/tmp` PostgreSQL databases. No production settings, database, provider or NAS was accessed. Filesystem tests establish publication ordering and process-restart checkpoints, not power-loss behavior or NFS throughput.

## Remaining limits

- O09 still needs bounded database/candidate pages, provider/import lane scheduling, visible job failure/retry state and a broader durable job design. This chapter preserves the existing serial worker rather than increasing its concurrency.
- Provider and metadata interfaces still lack complete transport cancellation. Cancellation is checked around those operations; an in-flight save is awaited before its database context is released.
- A hard-mounted NAS can block a kernel filesystem call beyond managed cancellation. Exclusive sharing and rename/flush guarantees must be validated on the actual NAS before claiming distributed writer or sudden-power-loss guarantees.
- Receipts use cheap file identity checks on resume; they are not a periodic full-audio corruption scrub. Interrupted staging files and completed journal retention need bounded maintenance.
- Changed expected catalog metadata fails safely and retains source rather than silently revising the admitted track set. A reviewed replan/reset flow remains to be added.
- This cannot recover files already deleted by the old partial-completion behavior. Existing incomplete albums need source recovery/redownload.
