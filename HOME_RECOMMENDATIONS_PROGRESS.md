# Home recommendations and collection history

Implemented 2026-11-01 under the workspace streaming-service-quality skill.

## Behavior

Home sections use persisted account activity and library state. No random ordering,
friend graph, fabricated social claims, calendar randomness or external request is
used. Each query materializes at most ten section cards. Database subqueries match
artist evidence without loading the whole catalog or history into application
memory. Latest albums and artist albums sort by date added descending, then album
identifier ascending. Saved albums sort by saved date descending, then identifier.
Artist albums exclude already saved albums and use actual artist identifiers from
recordings listened to, recordings in the account's designated Liked Songs
playlist, and saved albums. Ordinary playlists do not imply liked artists. Legacy
multiple liked designations use creation date then identifier, matching the
library heart resolver.

Advertised sections, in order, are:

- `recent`: Recently played collections, only when a visible collection exists.
- `saved`: Your saved albums, only when an account has saved albums.
- `artists`: More from your artists, only when evidence yields candidate albums.
- `latest`: Recently added albums, only when catalog albums exist.

The mobile Home screen already has a recent collection grid and can suppress the
additional `recent` carousel. The section endpoint remains available for older
clients. New accounts see actual latest additions; an empty catalog yields no
empty sections. Album candidates are existing local release rows, not an external
catalog recommendation or a guarantee that every recording is downloaded.

Older `today` and `picks` section identifiers resolve to the same artist evidence,
with latest additions as the new-account fallback. `friends` returns an empty
list and is never advertised. Unknown section identifiers return empty lists.
The API keeps its existing response fields and adds `canEdit`, true only for an
owned playlist recent card. Album responses have `canEdit: false`.

## Recently played collections

The quick grid and `recent` section share the same source-aware query. An album
qualifies only through `Listen.SourceAlbumId`, captured from an explicit album
queue. A playlist qualifies through `Listen.PlaylistId`. Unknown/direct-track
contexts with neither identifier never imply the recording's album. Historical
rows with neither identifier remain unknown; there is no retrospective guess.
A recording played from a playlist marks that playlist, even if the recording
belongs to a local album. Defensive conflicting historical rows favor the
playlist rather than manufacturing a second recent album.

Grouping happens in PostgreSQL before the card limit, using the newest listen
start per collection. Identity includes both collection type and identifier.
Repeated songs or more than fifty plays of one album cannot hide older distinct
collections. Ties sort by type then identifier. Missing/deleted albums and
playlists are excluded before limiting. Playlists have no public/shared visibility
flag in the existing schema, so only the account's owned playlists are eligible.
The service clamps quick results to twelve and returns empty results for
nonpositive service counts. Existing HTTP query clamping remains in the controller.

This chapter depends on collection history commit `bb57ee3`, including its
nullable source-album migration and ingestion validation. Deploy the combined
server source and apply that migration before using the new recommendation query.
The companion mobile chapter must report explicit queue context for new album
history. Historical playlist identifiers continue to work.

## Feature test map

`StreamingServiceServer.CoverTests/HomeRecommendationsTests.cs`:

| Behavior | Test |
| --- | --- |
| Unknown/friends endpoints and nonpositive counts avoid database access | UnknownAndFriendsSectionsDoNotQueryOrInventRecommendations |
| Empty catalog advertises no empty sections | EmptyCatalogAndNewAccountProduceNoEmptySections |
| Legacy unknown context cannot become recent album | UnknownContextDoesNotMarkRecordingAlbumRecentlyPlayed |
| Explicit playlist versus album source, shared recent query and edit flag | PlaylistPlaybackMarksPlaylistAndExplicitAlbumPlaybackMarksAlbum |
| More than fifty repeated plays and type-aware identity | RepeatedPlaysCannotStarveOlderCollectionsAndTypeIsPartOfIdentity |
| Account isolation and missing/deleted/foreign collections | ForeignAndDeletedCollectionsAndOtherAccountsAreExcludedBeforeLimiting |
| Newest play and stable repeated requests | RecentCollectionsUseNewestPlayAndStableTypeAndIdentifierTies |
| New listener fallback and old section compatibility | NewListenerGetsStableLatestAlbumsWithoutFakePersonalOrFriendsSections |
| Saved time ordering and account isolation | SavedAlbumsUseSavedTimeAndAccountScope |
| Actual listened/liked/saved artist evidence and exclusions | ArtistRecommendationsUseListensLikesAndSavedEvidenceWithoutForeignOrUnrelatedAlbums |
| Ordinary and noncanonical liked playlists cannot supply evidence | OrdinaryAndLaterDuplicateLikedPlaylistsDoNotSupplyArtistEvidence |
| Bounded quick cards and latest-added ordering | RecentQuickClampsResultsAndLatestOrdersByAddedTime |

## Validation

`dotnet build StreamingServiceServer.Business/StreamingServiceServer.Business.csproj --no-restore -v:q`
passed with existing assembly/nullability warnings and no errors.

`COVER_TEST_POSTGRES='Host=/tmp;Port=55439;Username=denis;Database=postgres' dotnet test StreamingServiceServer.CoverTests/StreamingServiceServer.CoverTests.csproj --no-restore --filter FullyQualifiedName~HomeRecommendationsTests --logger 'console;verbosity=normal'`
passed all twelve tests: one database-free boundary test and eleven deterministic
PostgreSQL integration tests, with zero failures or skips. The shared existing
fixture creates and drops isolated databases on a disposable `/tmp` Unix-socket
PostgreSQL cluster. Actual PostgreSQL translation caught and fixed an EF Include
projection issue in the saved-album query. No production database, live account,
Spotify request, mobile device or deployment was used.

`git diff --check` passed. User-owned appsettings and `.idea` changes are excluded
from this chapter. Real mobile acceptance remains separate from these source and
query results.
