# Soulseek source switching

## Cause

The dashboard source-list and source-switch endpoints used a scoped SlskdHelper without applying the saved provider settings. Requests therefore used localhost and empty credentials. Background-worker configuration belongs to a different service scope and cannot configure dashboard requests.

Other gaps included marking every directory belonging to the current username as current, deleting transfer entries across that username, tracking progress across unrelated directories, accepting partial enqueues, retaining import identity during a source switch, and allowing stale UI responses and concurrent clicks.

## Implemented

- Both dashboard endpoints configure the helper from the enabled Soulseek provider record. Missing configuration and upstream failures produce actionable responses.
- Current-source matching checks both username and directory. Other directories from the same username remain selectable.
- The switch checks the client import identifier, validates every requested file and size against the saved search, and obtains the album import lock. A database row lock protects the pending update.
- The selected files are submitted as one batch. Existing healthy transfers count toward the selection; all selected files must be visible in slskd before the pending source is replaced. A failed verification keeps the previous pending record.
- Successful switching creates a new ImportIdentifier so the old import journal cannot be reused. Import and stall-cleanup workers share the album lock and check source identity before destructive work.
- New Soulseek downloads store a versioned source identity containing the search, username, directory and exact selected files in the existing SourceName column. Legacy identities remain readable; the pending SavePath scopes their transfer lookup and cleanup.
- Progress and cleanup are restricted to the selected files or the legacy album directory. Completion requires every selected file, rather than every transfer belonging to a username.
- Previous transfers are cleaned up after the new source is committed. Cleanup errors are logged and returned as a visible warning. Files retained by the new selection are excluded from cleanup.
- Queue progress uses one transfer snapshot per dashboard request. Queue and album views show a readable source identifier instead of the encoded manifest.
- Changing import identity resets progress and stall timing while preserving the album's cumulative retry budget.
- The modal rejects repeated submissions, sends the expected import identifier, ignores stale results and displays upstream errors. Updated assets share a new cache revision.

## Verification

API Release publication, JavaScript syntax and Git whitespace checks passed. No test suites were added or run for this change. No live source switches, transfer cancellation, production database changes or deployment were performed. Local settings and IDE changes are excluded.

## Deployment and remaining limits

Deploy the API and static assets together. No database migration is needed. New source identities require this worker version; an older worker cannot interpret them.

slskd and PostgreSQL do not share a transaction. If enqueueing succeeds only partly or the database commit fails, newly submitted transfers may remain in slskd while the old pending source is retained. Retrying checks existing transfers before submitting missing files. Cleanup errors after a successful commit are reported instead of claiming cancellation succeeded. Missing or expired search results require requeueing the album; this change does not silently create replacement searches.

The unrelated album-wide requeue paths and recording-redownload cleanup still need a separate review. They were not replaced by this source-switch repair.
