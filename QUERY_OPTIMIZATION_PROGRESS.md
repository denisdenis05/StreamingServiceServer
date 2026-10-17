# Search and likes query progress

Updated 2026-10-17. Implements O17 and the bounded likes part of O16 from `../REMAINING_OPTIMIZATION_REVIEW.md`.

## Implemented

- General search starts the three independent provider searches together. The playlist database read completes before the likes database read starts, so they do not overlap on the request's scoped `StreamingDbContext`.
- General search limits like candidates to the returned provider recordings. It retains the existing playlist visibility and best-match rules.
- Database search collects exact and fuzzy recording results before asking for likes. Its like query includes only those candidate identifiers. Search reads no longer retain entity graphs in the context tracker.
- Playlist pages query likes only for returned recording identifiers, after reading the page. Their image selection and system-playlist ordering remain unchanged.
- Added a candidate-identifier overload of `GetLikedRecordingIdsAsync`. Empty candidate sets return without querying. The existing unrestricted overload remains for other callers, but now projects only recording identifiers rather than loading a playlist and all recording entities.
- Likes retain the existing rule: the first system playlist belonging to the authenticated user supplies liked recording IDs. Ordinary playlists and other owners' system playlists do not contribute. This work does not introduce a new playlist authorization policy.
- Both metadata search methods accept cancellation and the API passes `HttpContext.RequestAborted`. Database reads receive the token. General search stops waiting for provider completion when canceled, without starting the later likes query.

## Feature tests

`COVER_TEST_POSTGRES='Host=/tmp;Port=55439;Database=postgres' dotnet test StreamingServiceServer.CoverTests --no-restore --filter FullyQualifiedName~SearchQueryOptimizationTests --nologo`

Result: **8 passed**, including 6 disposable PostgreSQL tests and 2 tests requiring no database provider.

| Functionality | Dedicated test |
| --- | --- |
| Empty like candidates | Returns empty without a configured database provider |
| Already canceled searches | Neither database nor provider work starts |
| Like projection/account isolation | Matching IDs only, no tracked playlist entities; unrestricted compatibility projection also checked |
| Paged playlist likes | Captured candidate array contains only page IDs; liked/unliked flags remain correct |
| Shared-context sequencing | Delayed playlist command while all providers start; no second context operation until released; bounded likes and full response checked |
| Active database cancellation | Delayed command cancels before likes starts |
| Exact and fuzzy search likes | PostgreSQL trigram search returns both tiers; captured likes array equals returned recording IDs |
| Provider-wait cancellation | Search returns cancellation before an unfinished provider completes and never queries likes |

The shared test fixture accepts only a `/tmp` Unix socket and creates/drops its own randomly named test databases. No production connection string, settings, database, provider or NAS was accessed.

## Remaining work

- O16 search predicates, combined fuzzy ranking and indexes remain unchanged. Adding index migrations needs representative `EXPLAIN` evidence first.
- General search provider interfaces still have no cancellation parameter. Caller cancellation stops waiting but cannot terminate underlying HTTP work; address that in O20's provider scheduler/interface work.
- Some other metadata methods still call the unrestricted likes projection. They no longer materialize playlist graphs, but their own candidate-scoping work is separate.
- Search result ordering, pagination bounds and playlist visibility policies retain existing behavior. They need separate product/security decisions where changes alter results or access.
- These tests establish query sequencing and bounded candidate transfer, not production latency or database-plan improvements.

No commit made by this implementation agent.
