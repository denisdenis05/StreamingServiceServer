# Durable audio import status

## Evidence and limits

For `mister misfit but aint missed a fit in months`, album
`182d16ac-dfc1-4d18-8066-07f061a1ca4a`, read-only production queries confirmed a
queued record, a Soulseek pending record, no failed marker and one catalog track.
The selected peer's one expected transfer was `Completed, Succeeded` at 100%.
The user's report that files have moved is consistent with unfinished import
finalization, but the server's filesystem and import journal were not accessible
here. The exact blocker is not yet confirmed, and the old logs are unavailable.

The normal importer publishes files before source/provider cleanup and queue
removal. It deliberately retains the queue if any later step fails. Previously,
those errors appeared only in worker logs; the Queue page kept showing
`downloading (100%)`. Existing journals retain checkpoints, but not every failure
message. The separate manual song job also does not reconcile album queue state;
this change adds diagnostics rather than changing that completion contract.

## Implemented

- Each import stores its current phase, expected/published counts, timestamp and
  last failure under `<Music:Path>/.audio-import-state/*.status.json`. Its identity
  uses the exact same provider, source and import generation as the import journal.
- Phases cover locating download files, reading metadata, publication, saving
  metadata, deleting source files, provider cleanup and database queue removal.
  Individual publication errors are also retained in the import journal.
- Failure messages include an underlying exception when present and are bounded.
  A retry that succeeds clears the error. Older source generations cannot inherit
  these messages.
- Existing journals without status files supply a coarse checkpoint: partial
  publication, metadata save, unfinished cleanup or pending queue removal. They
  do not invent an old error message or identify an unrecorded cleanup sub-step.
- The admin queue response includes this status. The Audio downloads table shows
  `awaiting import`, `importing`, `finalizing` or `import error`, with the actual
  count and failure message. User-controlled diagnostic text is escaped.
- A bad status file leaves its album visible with an unavailable-status message.
  Status-write failures are logged but do not prevent importing, and diagnostics
  do not create a missing music root. Source ownership and completion checks are
  unchanged. No database migration is needed.

## Verification

- `dotnet build StreamingServiceServer.API/StreamingServiceServer.API.csproj --no-restore --verbosity quiet`: passed; existing warnings remain.
- `node --check StreamingServiceServer.API/wwwroot/js/dashboard.js`: passed.
- `node --test tools/*.test.cjs`: 76 passed, including five new queue-status cases.
- With a disposable PostgreSQL instance at `/tmp/streaming-manual-source-postgres.W3IbD2`, `COVER_TEST_POSTGRES='Host=/tmp/streaming-manual-source-postgres.W3IbD2;Port=56439;Database=postgres;Username=denis' dotnet test StreamingServiceServer.CoverTests/StreamingServiceServer.CoverTests.csproj --no-restore --filter '(FullyQualifiedName~AudioImportSafetyTests&FullyQualifiedName!~ImportDestinationCannotEscapeMusicRootOrTraverseDirectoryLinks)|FullyQualifiedName~AudioImportDiagnosticsTests|FullyQualifiedName~ManualDownloadSourceTests|FullyQualifiedName~SoulseekDownloadRetryTests|FullyQualifiedName~SoulseekSongRetryTests' --verbosity quiet`: 82 passed, none skipped.
- The new server cases cover failures after files are published, restart without
  logs, successful recovery without copying healthy tracks again, missing source
  files before journal creation, partial publication errors, old journal fallback,
  source-generation isolation, bounded/root errors, corrupt status and progress
  storage failure. Two PostgreSQL cases verify the queue response and retention
  of pending/queued rows.
- `dotnet test StreamingServiceServer.CoverTests/StreamingServiceServer.CoverTests.csproj --no-build --no-restore --verbosity normal` without the PostgreSQL environment variable: 310 passed, 105 skipped, one existing failure in `AudioImportSafetyTests.ImportDestinationCannotEscapeMusicRootOrTraverseDirectoryLinks` due to the exception-type mismatch already reported in earlier chapters.
- `git diff --check`: passed.

No production queue entries or files were changed. Tests used disposable local
files/databases and simulated provider responses. Static asset versions were
advanced together. Runtime browser use and live import recovery were not tested.

## Delivery and next step

The change is on `fix/visible-audio-import-status`, created from freshly fetched
`origin/master` after PR #1 was merged. Deploy the API and static assets together,
then inspect this album's status after the next worker retry. A precise existing
failure can then be repaired without relying on retained console logs. This PR
does not deploy the service or mark an unverified album complete. Status sidecars
share the existing journal retention policy; automatic pruning remains deferred.
