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
