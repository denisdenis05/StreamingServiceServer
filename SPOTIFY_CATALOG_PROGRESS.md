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
