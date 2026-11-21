# Selectable Liked Songs playlist

Implemented on 2026-11-01 under the workspace streaming-service-quality rules.

The account's existing playlist `IsSystem` flag is the persisted Liked Songs
designation. No schema migration or copied recording rows are needed. Existing
accounts keep their designation and likes. Selecting another owned playlist
atomically switches the flag and makes its existing recordings liked everywhere
that uses the library heart query. Playlist additions, removals, imports and heart
toggles all use the same recording membership. Playlist recording identifiers
already have a composite primary key, so duplicate occurrences cannot exist.

Changing or clearing the designation preserves all playlists and their contents.
Clearing returns a null selection and no liked recordings. The next heart creates
a new default playlist; previously selected playlists are retained. This is not a
restore operation for an old default. Deleting the selected playlist also leaves
no designation. Selected playlists remain renameable. A selected playlist uses
the existing reverse ordering for liked songs. Legacy multiple designations are
read consistently by creation time and identifier; saving a selection normalizes
them to one. Selection and heart toggling use serializable transactions; concurrent
writes may abort with a PostgreSQL serialization conflict and must be retried by
the caller. No automatic retries are added to the existing service lifetime.

## Authenticated API

Existing Library Bearer User/Admin authorization applies.

- `GET /Library/liked-songs-playlist`: 200 `{ "playlistId": "guid" }` or null.
- `PUT /Library/liked-songs-playlist`: body and 200 response use the same shape.
  Missing, deleted or another account's playlist returns 404 before mutation.
- `GET /Library/likes/{recordingId}`: 200 `{ "isLiked": true|false }` from a
  bounded recording membership query.
- Existing `POST /Library/likes/{recordingId}` now returns the same authoritative
  membership shape after toggling, preserving clients that ignore its body.
- Empty identifiers return 400; malformed identifiers use ASP.NET Core's existing
  automatic Guid binding validation. Invalid account claims return 401. ASP.NET
  Core owns route/model binding and framework identifier spelling at this boundary.

## Feature test map

`StreamingServiceServer.CoverTests/LikedSongsPlaylistTests.cs` contains:

| Behavior | Dedicated test |
| --- | --- |
| Selection preserves contents and names | SelectionChangesDesignationAndPreservesRecordingsAndNames |
| Clear preserves playlists | ClearingSelectionPreservesEveryPlaylist |
| Invalid selection cannot mutate current flag | MissingSelectionFailsBeforeChangingExistingDesignation |
| Repeated save normalizes duplicate legacy flags | RepeatedSelectionNormalizesLegacyDuplicateDesignations |
| Empty identifier rejected before database access | EmptyIdentifierFailsWithoutDatabaseAccess |
| Account and endpoint identifier validation | ControllerRejectsMissingAccountAndEmptyPlaylistWithoutCallingService |
| Persisted selection, bounded and full heart queries, page hearts, toggle and playlist edits | SelectionPersistsAndAllHeartQueriesAndTogglesUseItsContents |
| Ownership, deleted IDs, rename, clear/default creation, deletion | ForeignAndDeletedPlaylistsCannotReplaceSelectionAndClearingCreatesDefaultOnNextLike |
| Concurrent selection preserves one designation | ConcurrentSelectionsCannotLeaveTwoDesignatedPlaylists |

Validation: `COVER_TEST_POSTGRES='Host=/tmp;Port=55439;Username=denis;Database=postgres' dotnet test StreamingServiceServer.CoverTests/StreamingServiceServer.CoverTests.csproj --filter 'FullyQualifiedName~LikedSongsPlaylistTests|FullyQualifiedName~SearchQueryOptimizationTests' --no-restore`
passed 17 tests, with zero failures or skips. This includes nine dedicated feature
tests and eight existing query regression tests. PostgreSQL used a disposable
cluster and isolated databases on a `/tmp` socket, then was stopped. Existing
assembly/nullability warnings remain. Dependency restore resolved the host's
missing ASP.NET targeting reference pack before this run.

`git diff --check` passed. No production database, deployment or live mobile UI
was exercised. User-owned appsettings changes and `.idea` were preserved and
excluded from staging. Mobile settings selection and live cache refresh belong to
the companion mobile chapter.

## Addition ordering repair — 2026-11-21

This chapter replaces the earlier unconditional reversal for selected playlists.
Designation changes no longer reverse undated imports. Ordinary playlists retain
saved position order. Liked songs sort known addition dates newest first, followed
by undated tracks in saved order. Equal known dates use descending saved position
so the last addition in a batch comes first. Recording identifiers break duplicate
legacy position ties consistently before pagination. Responses number displayed
rows from 1 across page boundaries without changing stored positions.

The nullable `PlaylistRecordings.KnownAddedAt` column distinguishes known song
addition history from import processing time. Native creation, direct additions,
fuzzy additions and hearts record the injected clock's UTC time. Import accepts
optional `addedAt` dates; the dashboard also forwards Spotify-style `added_at`.
Dates with offsets are stored in UTC. Import without dates retains its input
order across batches, including an explicitly reversed input. Replacing a track
preserves both timestamps, saved position and original contributor.

Migration `20261121101751_AddKnownPlaylistAdditionTime` must be applied before
running the updated server. It leaves existing rows' known dates null: old imports
and native likes cannot be distinguished reliably from their processing timestamps.
All legacy rows therefore use saved order as their baseline, including previously
designated playlists. Original addition dates discarded by past imports cannot
be recovered. In mixed dated/undated collections, known dates come before the
undated baseline. Selecting or clearing a designation never rewrites this history.
Ordinary playlist responses also use consecutive display positions rather than
exposing potentially sparse stored positions.

### Dedicated feature tests

| Functionality | Test evidence |
| --- | --- |
| Undated order, dates versus saved positions, ordinary order, equal-date ties, duplicate-position stability, pagination and empty collections | `PlaylistRecordingOrderingTests` — 9 deterministic unit cases |
| Imports across batches, repeated selection and clearing preserve positions | `UndatedImportKeepsBatchOrderThroughSelectionRepeatedSelectionAndClearing` |
| Persisted dates, newest-first SQL ordering, continuous page numbering and hearts | `DatedImportDisplaysNewestFirstWithContinuousPositionsAcrossPages` |
| New hearts, removal/re-addition, equal clock times and clearing | `NewLikesAndReaddedSongsAppearFirstAndClearingRestoresSavedOrder` |
| Replacement keeps history and position | `ReplacingAnImportedRecordingPreservesItsAdditionHistoryAndPosition` |
| Creation, direct and fuzzy addition paths use the injected clock | `EveryNativeAdditionPathRecordsKnownAdditionTime` |
| Upgrade from the preceding schema preserves legacy rows with unknown dates | `MigrationLeavesLegacyAdditionHistoryUnknownAndRowsReadable` |
| Dashboard forwards both date field spellings, leaves missing dates unknown and preserves dates through reversal | `tools/playlist-import-order.test.cjs` — 4 unit cases |

### Verification

`COVER_TEST_POSTGRES='Host=/tmp/liked-order-postgres;Port=55449;Username=denis;Database=postgres' dotnet test StreamingServiceServer.CoverTests/StreamingServiceServer.CoverTests.csproj --no-restore --filter 'FullyQualifiedName~PlaylistRecordingOrderingTests|FullyQualifiedName~PlaylistAdditionHistoryTests|FullyQualifiedName~LikedSongsPlaylistTests|FullyQualifiedName~SearchQueryOptimizationTests' --verbosity normal`
passed 32 tests with zero failures or skips. This includes 9 ordering unit cases,
6 new PostgreSQL feature cases, 9 existing selection cases and 8 query regressions.
The first run exposed test fixture precision and missing release-cover problems;
the fixture now reloads persisted timestamps and supplies the required cover.

`node --test tools/playlist-import-order.test.cjs tools/manual-download-source.test.cjs tools/browser-player.test.cjs tools/dashboard-polling.test.cjs tools/lyrics-queue.test.cjs`
passed 61 tests with zero failures or skips. Existing polling fixtures emitted
missing lyrics-client diagnostic messages while their assertions passed.

`dotnet ef migrations has-pending-model-changes --project StreamingServiceServer.Data --startup-project StreamingServiceServer.Data --no-build`
reported no pending model changes. `git diff --check` passed.

EF-generated migration/designer/snapshot files retain EF's generated schema
literals and directives; authored ordering, import and test code adds no comments.
Migration test SQL uses the actual schema identifiers required by PostgreSQL.

No production database, deployment, mobile package or physical-device scenario
was exercised. Existing appsettings, recommendation documentation, `.idea` and
source-database design edits remain outside this chapter. Publishing requires
GitHub authentication: the initial HTTPS dry-run push failed because no username
or credential helper was available, and the temporary GitHub CLI has no login.
After authentication, push this branch and create a PR targeting `master`.
