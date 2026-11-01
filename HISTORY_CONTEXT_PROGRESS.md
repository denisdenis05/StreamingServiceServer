# Playback collection history

Implemented 2026-11-01. The existing track listen, scrobble accounting and metadata
remain intact. Collection history now records the collection that actually supplied
the playback queue, rather than deriving an album from a track's metadata.

## Contract and validation

`POST /lastfm/start-playback` accepts optional `sourceAlbumId` and `playlistId`.
Exactly one can identify the playback source. Both absent means unknown/direct
track, including older app versions. A valid album source requires the recording
to belong to that exact catalog release. A playlist source requires the playlist
to belong to the authenticated account and contain that recording identifier.
Playlist entries without a catalog recording are supported through their existing
playlist membership. The model has no public visibility/access flag, so this
chapter deliberately does not accept foreign playlist identifiers as history.

Conflicting, empty, missing, foreign or nonmember source identifiers are discarded;
the track listen is still saved. This prevents invalid context from poisoning
collection recents or breaking playback reporting. Metadata alone never establishes
album context. Server failures retain the exception cause but the new start-session
wrapper message contains no account, song, or upstream response details. No live
Last.fm or Spotify call was made by the feature tests.

`Listen.SourceAlbumId` is nullable. Existing `Listen.PlaylistId` stores explicit
playlist context. The migration adds the album column and account/collection/time
indexes; it does not infer or backfill historical albums. Historical valid playlist
attribution remains usable. The generated EF migration, designer and model snapshot
use EF's required schema names and generated source conventions. They are generated
framework output rather than hand-authored application logic.

## Feature tests

`StreamingServiceServer.CoverTests/PlaybackCollectionContextTests.cs`:

| Behavior | Test |
| --- | --- |
| Unknown and conflicting source identifiers never infer album metadata or query the database | UnknownAndConflictingContextsRequireNoDatabaseAndNeverInferAlbum |
| Empty identifiers never query the database | EmptyCollectionIdentifiersAreDiscardedWithoutDatabaseAccess |
| Nullable migration has no data backfill or default | MigrationAddsNullableAlbumContextWithoutBackfillingHistory |
| Explicit matching album persists alongside unchanged track metadata | AlbumPlaybackPersistsOnlyExplicitMatchingAlbumAndKeepsTrackMetadata |
| Owned playlist containing an external recording persists as playlist source | OwnedPlaylistPlaybackPersistsPlaylistEvenWithoutCatalogRecording |
| Foreign, nonmember, missing and conflicting context is discarded while track listens remain | InvalidForeignAndConflictingContextsPreserveTrackListenWithoutCollectionAttribution |
| Real migration upgrade, rollback and upgrade retains old unknown track history | ApplyingMigrationKeepsLegacyListenUnknownAndRetainsTrackHistory |

## Validation

`COVER_TEST_POSTGRES='Host=/tmp;Port=55439;Username=denis;Database=postgres' dotnet test StreamingServiceServer.CoverTests/StreamingServiceServer.CoverTests.csproj --no-restore --filter FullyQualifiedName~PlaybackCollectionContextTests --logger 'console;verbosity=normal'`

Passed nine tests, zero failures or skips. Tests used isolated fixture databases in
a disposable local PostgreSQL cluster. No production database, settings, deployment
or device was changed. `git diff --check` passed. Existing project warnings remain.

The mobile chapter must capture queue provenance before async reporting, carry it
through future track transitions, and keep history HTTP off the audio control path.
The recommendation chapter consumes explicit source identifiers only. Deployment
requires applying this server migration before sending requests from the new app.
