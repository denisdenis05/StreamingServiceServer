# Missing cover diagnosis and recovery

Investigated on 2026-10-17 using the albums visible in the user's screenshot.

## Database access

The development connection was read privately from appsettings.Development.json and passed to psql through process environment variables. Every connection set default_transaction_read_only=on and every query ran inside BEGIN READ ONLY. A SHOW transaction_read_only query returned on. Only SELECT/SHOW statements were sent; no migrations, updates, deletes or application startup ran against this database. Credentials were not printed or saved in this report.

The inspected database has legacy Cover/Optimized/Local fields but no CoverAssetJobs table. The previously prepared background-worker migrations therefore still need deployment through the user's normal workflow. This investigation did not apply them.

## Findings

| Image | Stored artwork state and HTTP checks | Cause |
| --- | --- | --- |
| The Unknown Collection, Vol. 1 | Local 500px URL returns 200, JPEG, 500x500 | Mobile fresh-file publication failed before display. |
| Balkan Brass Band | Local 500px URL returns 200, JPEG, 500x500 | Same mobile failure. |
| SAYA, working comparison | Local 500px URL returns 200, JPEG, 500x498 | Already cached content could still appear. |
| I Am, Earth, Wind & Fire | Local/optimized fields empty; release artwork returns 404; release-group thumbnail returns 200 JPEG | Specific edition has no artwork; group artwork is available. |
| Goodbye Horses, Q Lazzarus | Local/optimized fields empty; release artwork returns 404; release-group thumbnail returns 200 JPEG | Same edition-versus-group gap. |
| Studiu Manea / Steaua De Mare | Local/optimized fields empty; release and release-group thumbnails return 404 | No artwork found from either supported archive address. |

The optimized remote thumbnail addresses for Unknown Collection, Balkan Brass Band and SAYA also returned valid 200 JPEG responses. Their missing pictures were not explained by a broken database URL or unsupported JPEG header. The actual emulator cache request failed with EUNSPECIFIED: File does not exist.

## Mobile fix

The stricter cleanup introduced in the preceding change accepted only ENOENT for an absent destination. Installed Android RNFS can instead reject unlink with EUNSPECIFIED and File does not exist. Moving a newly downloaded file first tried removing its nonexistent destination, so a fresh image failed while an existing cached image could work.

Cleanup now checks destination existence before unlinking. If removal races another cleanup, an unspecified error is tolerated only after confirming that the file is absent. Existing permission errors still fail rather than being disguised as a successful deletion. No error-message string matching is used.

Two dedicated regressions cover first publication and concurrent removal. After the fix, actual emulator cache requests for Unknown Collection and Balkan Brass Band returned validated file:// addresses. These probes fetched static image files and wrote only the app's cache, not database records.

## Server refetch fix

- Only a confirmed 404/410 enters metadata recovery. Timeouts, throttling and server failures keep their ordinary retry policy.
- For an official Cover Art Archive release image, refetch MusicBrainz release metadata with release-groups included, resolve its current group ID, and try the corresponding group image at the same requested size.
- All three sizes share a bounded release-group lookup cache. Metadata requests are paced, have a total deadline and a response-size bound. This resolver has its own pacing; it is not a distributed limit across all server replicas or other provider clients.
- Successful fallback passes through the existing validation/rendering pipeline and creates the normal source mapping. Repeated requests for the original release URL reuse the recovered content instead of repeating its 404.
- Unrelated hosts and group URLs do not recursively trigger metadata lookups. Missing group metadata or missing group artwork remain missing; the code does not substitute unrelated artwork.

This changes source code for the background downloader. It did not rewrite the inspected database or run the background service. Deploy the server code and the previously required migrations for existing albums to be backfilled automatically.

## Checks

- Mobile cache suite: 73 passed; playback suite: 319 passed; full TypeScript passed; Android production JavaScript bundle passed.
- Server non-PostgreSQL suite: 64 passed, including nine new archive fallback cases. Tests use fake HTTP handlers and temporary files; no test database was accessed this turn.
- Server Release publish passed. Existing nullable and EF relational-version warnings remain.
- Live HTTP checks and emulator probes support the specific findings above. They do not establish permanent remote availability or deployed server-worker execution.
