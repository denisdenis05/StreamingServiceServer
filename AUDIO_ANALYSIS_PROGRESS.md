# Local audio analysis

## Chapter 1: durable storage

Implemented on 2026-11-10 in StreamingServiceServer.

Migration `20261110194920_AddAudioAnalysisProcessing` adds three tables and leaves recording/library data intact:

| Table | Purpose |
| --- | --- |
| AudioAnalysisSettings | Singleton processing switch, endpoint, protected token, profile and revision. Processing starts disabled. |
| AudioAnalysisJobs | One durable job per indexed recording, attempts, safe failure category, next attempt, source identity and expiring versioned lease. |
| RecordingAudioAnalyses | One current successful result per recording, source hash, analyzer/model lineage, full features, moods and 200-dimensional embedding. |

The current result is replaced only after successful validation. Failures retain the previous result. There is no growing response/job history and no copy of a MusicBrainz or ListenBrainz database. Source audio stays in the existing music library.

Settings revision and job version are concurrency tokens. Jobs have due-time and lease indexes. Recording foreign keys cascade when a recording is deleted. Features, embedding and model manifests are stored as JSONB; tempo, energy, key, scale and duration also have typed columns. This preserves data for later recommendation work. No similarity search endpoint or vector index is introduced in this chapter.

The six schema tests check migration scope, defaults, keys, bounds, JSONB columns, indexes and concurrency tokens. The integrated worker tests also apply the actual migration to a disposable PostgreSQL database and verify the disabled singleton and empty analysis tables.

Generated EF migration/designer/snapshot output retains framework naming, literals and generated comments. Authored code follows the local quality skill; generated files are the explicit boundary.
