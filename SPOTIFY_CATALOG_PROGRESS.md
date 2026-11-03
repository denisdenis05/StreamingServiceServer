# Current commit authorization — 2026-11-03

The user authorized commits at the current time without backdating. Earlier
no-commit notes below are historical. Canonical recording-credit projection and
search passed all 16 catalog tests, including three isolated PostgreSQL cases.
This chapter remains undeployed. User-owned appsettings and .idea changes are
excluded from the commit and preserved.

# Spotify recording catalog

Implemented on 2026-10-21 under the workspace streaming-service-quality rules.

`RecordingCatalogQuery` provides a read-only Entity Framework projection of the
existing MusicBrainz-derived recording catalog. Lookup preserves the recording
identifier; bounded search uses title and artist evidence with deterministic
ordering. Projection reads title, duration and available artist credits without
loading cover metadata or tracking entities. Multiple credits are preserved;
release artist fallback is explicitly labeled for conservative matching.

The authenticated routes are `GET spotify/catalog/recordings/{identifier}` and
`GET spotify/catalog/recordings/search?title=...&author=...&limit=...`. They use
the existing Bearer User/Admin authorization and do not contact Spotify, store
Spotify credentials, alter catalog data, or queue downloads. The TypeScript
library consumes these endpoints through an application-authenticated adapter.

Validation: the `SpotifyRecordingCatalogTests` filter passed all 12 cases,
including two real disposable PostgreSQL cases. Those tests cover projection,
artist fallback, copies, invalid inputs/duration, cancellation, SQL translation,
authorization/routes, actual lookup/search, result bounds and no tracked entities.
The disposable PostgreSQL 18.6 cluster used a `/tmp` Unix socket and was stopped
after testing. No schema migration or application database change was needed.

The broader non-PostgreSQL suite produced 148 passes, two skips and one failure
in untouched `AudioImportSafetyTests.ImportDestinationCannotEscapeMusicRootOrTraverseDirectoryLinks`:
an expected InvalidDataException differs from the InvalidOperationException thrown
by unchanged MusicLocationHelper. This work does not establish a green full server
suite. Existing assembly/nullability warnings remain.

User-owned appsettings changes and the untracked .idea directory were preserved
and excluded from this chapter's staging. Query result bounds are verified;
database scan cost and latency were not benchmarked.

## Canonical recording artists — 2026-11-03

The Spotify catalog projection now prefers the canonical linked Artist.Name for
recording credits. A release-specific credited alias no longer hides that name.
Credited text remains the fallback when the canonical artist is missing or blank;
release-only fallback still retains its explicit provenance. Search accepts both
credited text and the linked canonical artist, preserving lookups by either form.
No schema migration, metadata writes, cover loading or app display changes were
introduced. This is a generic metadata correction, not a hardcoded artist alias.

All 16 SpotifyRecordingCatalogTests pass, including three actual disposable
PostgreSQL cases and new canonical/credited alias projection/search and canonical deduplication regressions.
Command: dotnet test StreamingServiceServer.CoverTests/StreamingServiceServer.CoverTests.csproj
--filter FullyQualifiedName~SpotifyRecordingCatalogTests --no-restore with
COVER_TEST_POSTGRES set to an isolated /tmp Unix socket. PostgreSQL was stopped
after the run. Log: /tmp/jam-canonical-server-postgres-tests.log. No production
server deployment or live catalog response is established by these tests.
The user's no-commit override applies; changes remain uncommitted. Preexisting
appsettings and .idea changes were preserved and were not used for this test.
