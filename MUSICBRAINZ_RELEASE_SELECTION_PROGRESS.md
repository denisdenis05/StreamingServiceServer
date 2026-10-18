# MusicBrainz release selection

## Implemented

- Queue album and add album now show a release card per matching release group.
- Every release card displays its track count in the top right, summed across its media when no total is supplied. Missing upstream counts display a dash rather than a fabricated zero.
- Cards include release type, date, country, format, disambiguation and exact release identifier.
- A plain `See more` control at the bottom right expands other releases in the group. `See less` collapses them without discarding the data.
- Selecting any expanded edition submits its own release identifier.
- Added authenticated admin endpoints `GET /api/Dashboard/mb-search/groups?title=...&artist=...` and `GET /api/Dashboard/mb-search/groups/{groupId}/releases?offset=0`.

## Requests and caching

Initial search uses one release-search request for up to 100 results, grouped into up to eight cards. The representative prefers a title matching its release group, then the upstream score. No per-card lookup is needed for track counts. The old album search method used elsewhere is unchanged.

First expansion browses the group's releases in pages of up to 100. Additional pages are requested only when the upstream response indicates more releases. Requests for the same search or group share a pending promise in the browser. The client retains up to 16 searches and 64 expanded groups; currently displayed groups retain their rendered releases even if a cache entry is evicted. Reopening a loaded group makes no HTTP call.

The server caches search results and release pages for five minutes with a 256-entry limit and serializes cache misses to avoid duplicate upstream requests. Failed requests are not cached. Cache keys include the upstream base URL. New requests use the existing provider rate limiter and accept cancellation. Old search responses cannot replace a newer search in the same modal.

## Verification and deployment

API Release publication and JavaScript syntax checks passed. No test suites were added or run for this change. Read-only upstream checks confirmed that the requested Changes album and the similarly named single belong to different release groups. Subsequent live release-search checks returned upstream HTTP 503, so the complete deployed search/expansion flow still needs a browser check when MusicBrainz is available.

No production database changes, migrations, media modifications or deployment were performed. Deploy the API and versioned static assets together. Local appsettings and IDE changes are excluded.
