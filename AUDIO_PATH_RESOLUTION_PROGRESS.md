# Shared audio paths

## Cause

The downloader and streaming service used `MusicLocationHelper.GetRelativeFolderPathForRecording`, which replaces spaces with percent signs and uppercases artist and album names. Replace/add audio separately called `SanitizeFolderName`, removing punctuation such as ampersands. The album detail endpoint only checked the downloader folder, so existing uploads could be marked missing.

The reported recording `475a9da0-130c-4a9f-8f9e-ca976e0c1705` belongs to album `0ea6b557-4ea5-42b3-9af8-ed70de9ee9f8`. A read-only database query confirmed the artist as `Ivan & The Parazol`. The existing readable MP3 is under `IVAN%THE%PARAZOL/HA%MEGNYITOD%A%SZIVET`, while the downloader destination uses `IVAN%&%THE%PARAZOL/HA%MEGNYITOD%A%SZIVET`.

## Changes

- Replace/add audio now uses the same shared destination helper as the downloader.
- Added shared file enumeration that checks the downloader folder first, then the older sanitized upload folder. Existing files stay at their current locations. Files present at both paths prefer the downloader folder.
- Streaming, album file status, missing-track removal, recording deletion and album analysis now use this enumeration.
- Dashboard file checks prefer `Music:Path`, matching the downloader and streaming configuration.
- Destination names use invariant uppercase and reject rooted or parent-traversing paths.

## Verification and scope

Release publication of the API succeeded. No test suites were added or run for this change. The supplied NAS file was inspected without modification. No database records, media files or production configuration were changed.

Album-wide delete/requeue operations retain their previous folder cleanup behavior. Consolidating those destructive operations and the remaining upload validation/import behavior is separate work; this repair aligns upload destinations and existing-file discovery.
