# Server image response and ownership fixes

## Implemented

- Playlist recording pages select full, small and thumbnail artwork through the same local/optimized/source policy as album responses. Selection runs after database pagination; unmatched playlist rows keep their own source cover.
- Cover jobs capture all three input addresses. A change to a smaller input creates a new source revision and fences an older download's publication.
- Candidate downloads prefer full, small and thumbnail in that order, skip duplicate addresses and try the next candidate after an image/network failure. Caller cancellation propagates immediately. The renderer continues to limit output dimensions without claiming to upscale smaller inputs.
- Source mutation clears the old generated fields before responses can prefer them indefinitely. Explicit new generated fields assigned in the same metadata operation are retained. Removing all source inputs clears generated fields and removes the entity's queued job in the same save.
- Inherited release artwork is propagated only to recordings whose complete source tuple matches the release's current or captured previous tuple, or whose source inputs are entirely empty. A shared old local filename no longer grants permission to overwrite a recording's custom source.
- Pending release changes preserve the first inherited source revision until publication. This handles multiple source changes while the original recordings still reference the earlier source.
- Release publication completes equivalent recording jobs in the same guarded transaction. Their original/source asset identities are recorded for shared verification and retention.
- Malformed/incomplete manifests are rejected before accessing variant fields. The maintenance service uses this parser and clears broken derivatives without adding filesystem probes to API responses.
- Derivative clearing accepts expected job version and local address guards. Maintenance cannot clear artwork after a newer metadata save changes the owning job version.
- A job suspends when every failed candidate is explicitly classified permanent. Transient candidates keep bounded exponential retry scheduling, honoring a longer provider Retry-After. A new source revision resets suspension.

## Regression coverage

`StreamingServiceServer.CoverTests/CoverSourceLogicTests.cs` covers candidate order/deduplication, thumbnail-only eligibility, secondary input revision changes, incomplete manifests, immediate stale derivative removal, source deletion, smaller fallback, playlist tiers and unmatched rows, custom recording preservation, inherited source replacement, missing-file fallback and delayed stale completion.

Final focused execution: 30 tests passed (16 source/response regressions, three failure/deadline regressions and 11 existing worker integration checks), using the disposable `/tmp` Unix socket fixture.

Command:

```sh
COVER_TEST_POSTGRES='Host=/tmp;Port=55439;Database=postgres' dotnet test StreamingServiceServer.CoverTests --no-restore --filter 'FullyQualifiedName~CoverJobFailureTests|FullyQualifiedName~CoverSourceLogicTests|FullyQualifiedName~CoverJobIntegrationTests' --nologo
```

The whole-job timeout covers candidate fallback and publication together. Timeout retries are saved using the application shutdown token, so an expired job deadline does not prevent its lease from being released.

Existing EF package-version conflict and nullable warnings remain outside this chapter. Final project-wide regression testing and migration script generation are owned by the parent and performance agent respectively.

No production database, Nginx configuration or live NAS behavior was exercised. Parent agent owns the final combined verification and commits. Local appsettings and IDE files were not changed.

## Metadata refresh and recording job ownership

The durable metadata refresh service updates inherited recording sources with one PostgreSQL data-modifying CTE. Its `RETURNING` identifiers select exactly the associated existing recording jobs for a source revision/version reset inside the same transaction. An older downloader cannot mark the replacement source complete, and custom recording sources/job versions are left alone. Prior asset manifests remain retained until replacement succeeds.

Three additional PostgreSQL regressions cover a recording download completing after metadata refresh, an independently selected custom recording, and a concurrent admin source update while a child row is locked.

The refresh locks the matching release row before changing its inherited recordings. The lock test waits for actual PostgreSQL lock states, confirms the admin update waits, then releases the child lock and verifies the later admin source remains selected.

All seven metadata refresh tests passed:

```sh
COVER_TEST_POSTGRES='Host=/tmp;Port=55439;Database=postgres' dotnet test StreamingServiceServer.CoverTests --no-restore --filter 'FullyQualifiedName~CoverMetadataRefreshTests' --nologo
```

## Integrated result

The final combined server suite passed 91 tests after transfer cleanup and ownership cross-review. See [IMAGE_TRANSFER_REPAIR_PROGRESS.md](IMAGE_TRANSFER_REPAIR_PROGRESS.md) for the shared test/build/deployment state. Earlier per-track counts above record the checks at those implementation points.
