# Completed Soulseek albums leaving the queue

## Reproduction and fix

The user rejected persistent import diagnostics. PR #2 was closed unmerged, and
this branch starts from `origin/master` without those changes. No new logging,
status files, periodic writes or database fields are introduced here.

A local regression reproduced a real queue defect: both tracks had been verified
and published in the library, metadata was saved and the original source files
were removed. A subsequent HTTP 503 from slskd's transfer-list endpoint threw
`Could not read slskd transfers; no transfers were removed`, preventing database
queue removal. The test failed before the fix and passes afterward.

The completion worker now uses best-effort Soulseek transfer-record cleanup for
normal completion, journal resume and disk recovery. HTTP failures, timeouts and
malformed transfer responses cannot prevent queue removal after the importer's
existing checks have succeeded. Source file deletion, verified destination checks,
metadata consistency, current-source ownership and transactional queue removal
remain mandatory. Torrent cleanup and explicit/stalled-source cleanup retain
their existing failure behavior. Invalid Soulseek source contracts still fail.

If housekeeping fails, completed transfer records may remain visible in slskd.
This does not require downloading, copying or retrying a completed album again.
No separate cleanup job or stored diagnostics are added.

## Evidence and limits

Earlier read-only checks confirmed that `mister misfit but aint missed a fit in
months` has a successful slskd transfer and remains queued/pending. Its existing
import journal is not accessible here, and the user declined a server link.
This fixes the reproduced housekeeping failure; it does not establish that this
was the exact cause for that production album. The separate manual-song path's
album reconciliation gap is not changed by this narrow fix.

## Tests

- New import regression: completed library files and removed sources leave the
  queue even when slskd history is unavailable. It failed against the old code.
- Partial albums do not reach best-effort cleanup or queue removal.
- Database queue-removal failures still resume without copying healthy audio again.
- Provider cases cover unavailable transfer lists, failed transfer deletion,
  malformed JSON, timeouts, already-cleared history and invalid source contracts.
- Existing metadata, publication, restart, queue-identity, cancellation, source
  switching and automatic/manual song retry protections remain covered.

Validation:

- `dotnet test StreamingServiceServer.CoverTests/StreamingServiceServer.CoverTests.csproj --no-restore --filter FullyQualifiedName~FullyImportedSoulseekAlbumLeavesQueueWhenTransferHistoryIsUnavailable --verbosity normal`: failed before the fix with the slskd HTTP exception; the same case passed afterward.
- With the disposable `/tmp` PostgreSQL instance, `COVER_TEST_POSTGRES='Host=/tmp/streaming-manual-source-postgres.W3IbD2;Port=56439;Database=postgres;Username=denis' dotnet test StreamingServiceServer.CoverTests/StreamingServiceServer.CoverTests.csproj --no-restore --filter '(FullyQualifiedName~AudioImportSafetyTests&FullyQualifiedName!~ImportDestinationCannotEscapeMusicRootOrTraverseDirectoryLinks)|FullyQualifiedName~SoulseekDownloadRetryTests|FullyQualifiedName~SoulseekSongRetryTests' --verbosity quiet`: 62 passed, none skipped. This builds the API and referenced projects; existing warnings remain.
- `dotnet test StreamingServiceServer.CoverTests/StreamingServiceServer.CoverTests.csproj --no-build --no-restore --verbosity normal` without the PostgreSQL environment variable: 308 passed, 103 skipped, one existing unrelated failure in `AudioImportSafetyTests.ImportDestinationCannotEscapeMusicRootOrTraverseDirectoryLinks` because the path validator throws a different exception type than the test expects.
- `git diff --check`: passed.

## Delivery

Branch: `fix/imported-album-queue-finalization`. Deploy the server after review,
then let the pending worker resume the existing import journal. No production
files, queue records, service settings or deployments were changed here. The
test PostgreSQL instance is stopped after validation. No database migration or
frontend asset update is required.
