# Browser audio controls

Updated 2026-10-18.

## Implemented

- Album track listings have a play/pause icon beside retry/delete. Known missing files have a disabled play button.
- Playlist manager has a final play column. Unavailable/unmatched entries are disabled. An indexed recording whose file has disappeared reports an unavailable status when requested.
- One bottom-centered floating player survives dashboard navigation. It shows track/artist, loading/buffering/playing/paused/finished/error status, elapsed/duration labels, seek position, play/pause and close. Selecting another track releases the old audio source. Track controls reflect the shared player state.
- The cream, olive and serif styles use the dashboard's existing theme variables, with a responsive narrow layout and keyboard labels/focus states.

Audio uses a native browser media element and `GET /Stream?id=<recording>&allowPreview=false`. The existing X-Accel-Redirect and external protected Nginx file-serving path are preserved. Range and seek requests are handled by the browser/stream route rather than fetching an entire track into a JavaScript Blob. Browser requests never put access tokens in URLs. Mobile callers keep the existing default preview behavior.

## Authentication

Before playing, the dashboard sends an authenticated `POST /Stream/browser-session`. The endpoint copies its validated Bearer credential into an HttpOnly, SameSite=Strict, session cookie scoped to `/Stream`. The cookie has Secure set when ASP.NET sees an HTTPS request. The existing JWT middleware still validates signature, issuer, audience and expiry; no separate credential issuer or anonymous stream path is introduced.

Cookie extraction is allowed only for exact `GET /Stream`, and only when an Authorization header is absent. Other API paths and session creation continue to require the regular Bearer header. Explicit invalid/expired Bearer credentials do not fall back to the cookie. Authentication changes stop audio and queue cookie deletion after any pending creation request, preventing an old response from starting playback. The deletion endpoint can clear expired sessions too. The cookie does not extend the original access token's lifetime.

## Verification

- `node --test tools/browser-player.test.cjs tools/dashboard-polling.test.cjs tools/cover-progress.test.cjs`: 44 passed, including 15 new browser-player/controller and actual table-rendering cases.
- Browser API cases: 13 passed, including actual JWT handler acceptance/rejection of valid, expired and incorrectly signed cookie tokens; exact route/method/header restrictions; cookie flags/clearing; missing audio without preview calls.
- `COVER_TEST_POSTGRES='Host=/tmp;Port=55439;Database=postgres' dotnet test StreamingServiceServer.CoverTests --no-restore --nologo`: 155 passed, zero failures/skips. Database tests use a disposable local Unix-socket cluster and generated test databases.
- `dotnet publish StreamingServiceServer.API --configuration Release --no-restore --output /tmp/browser-player-publish`: passed. Existing nullable and dependency-version warnings remain.
- JavaScript syntax and `git diff --check`: passed.
- Chromium visual check used actual dashboard/render/player assets with local sample responses and a WAV fixture. It verified placement/theme and disabled rows, not the deployed Nginx path, production files or codec compatibility.

Dedicated browser tests cover safe button markup, time labels, authentication-before-streaming, toggling, replacement and stale events, superseded requests, close/logout during authorization, disabled/anonymous selections, seeking, buffering/end/replay, recoverable errors and delegated row clicks.

## Acceptance still needed

Deploy the server/static assets together, sign into the dashboard and play an existing local recording through the normal external Nginx origin. Check seek/pause/resume, missing files and both album and playlist rows. Supported audio formats depend on the browser; this feature does not transcode unsupported files. An autoplay restriction displays a prompt to press play.

No Nginx configuration, production database, local application settings or mobile/player code was changed for this chapter. No push or deployment is included.
