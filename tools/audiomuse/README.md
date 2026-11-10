# Local AudioMuse analysis bridge

A small HTTP service around the existing AudioMuse-AI MusiCNN analyzer. StreamingService submits one accessible audio asset at a time. ASP.NET owns the durable work queue and stored results. No Navidrome, MusicBrainz mirror, ListenBrainz dump or AudioMuse PostgreSQL instance is required.

This is a StreamingService adapter, not an endpoint provided by stock AudioMuse. It pins [AudioMuse-AI v3.7.1](https://github.com/NeptuneHub/AudioMuse-AI/releases/tag/v3.7.1), commit `ac65c469d288a45092db77cc76c1e347a9e14f48`. It calls the upstream `tasks.analysis.song.analyze_track` implementation without rewriting audio analysis.

## Setup

From this directory:

```bash
python3 download_models.py ./models
docker build -t streaming-audiomuse-bridge .
```

The first command explicitly downloads only the two official MusiCNN ONNX assets. It checks their SHA256 against `manifest.json`, captured from the official GitHub release asset metadata. Already valid files are reused. The selected model files total 3,173,194 bytes; the Python runtime and image need additional storage. No model download, container build or actual inference was performed as part of implementation verification.

Set `AUDIOMUSE_BRIDGE_TOKEN` in your deployment environment, then run:

```bash
docker run --rm --name streaming-audiomuse \
  --publish 127.0.0.1:8786:8786 \
  --env AUDIOMUSE_BRIDGE_TOKEN \
  --mount type=bind,src="$PWD/models",dst=/models,readonly \
  streaming-audiomuse-bridge
```

The token must also be configured privately in the ASP.NET service. It is required even for health checks. No token is shipped. When ASP.NET is in another container, use a private container network and the container hostname instead of its loopback address. Do not publish this analysis API to the internet.

The image runs as an unprivileged user, uses only CPU ONNX Runtime and does not install CLAP, lyrics transcription, neural fingerprinting, remote AI clients or another music server. The source pin and direct package versions are fixed. Python transitive packages and the base image are not a complete reproducible dependency lock; generate and review a platform-specific hash lock before a reproducible production build.

## Optional deployment with the ASP.NET server

The repository includes `compose.audio-analysis.yaml` as an optional override. Generate the two model files with the explicit download command above, set `AUDIOMUSE_MODELS_HOST_PATH` to that existing directory, and provide `AUDIOMUSE_BRIDGE_TOKEN` through the deployment environment. Do not put the token in a tracked file.

From the server repository root, with the ordinary server deployment variables already supplied:

```bash
docker compose -f compose.yaml -f compose.audio-analysis.yaml --profile audio-analysis up -d --build
```

In the administrator's Settings page, use `http://audiomuse:8786` and the same token, then enable background processing. Enabling the Compose profile starts the service; the administrator toggle separately controls whether StreamingService processes its queue. Stopping processing does not require stopping the container.

The analyzer has no published host port. It joins an internal network shared only with the server. The server keeps its existing default network for other dependencies. The analyzer receives audio bytes from StreamingService; it has no access to the library directory or database. At runtime its internal network prevents internet access. Building the image and explicitly downloading models still require internet access.

The container runs as user `10001:10001` with a read-only root filesystem, a read-only existing model directory, no added capabilities and a 1 GiB temporary filesystem. Ensure that this user can read and traverse the model directory. Do not change ownership of the music library for the analyzer. Temporary decoding files and caches live in `/tmp` and disappear when the container is replaced.

Defaults allow 2 CPU cores, 8 GiB memory, 256 processes/threads and a 536,936,448-byte multipart request. That request limit covers the ASP.NET 512 MiB audio limit plus 64 KiB of form overhead. Override `AUDIOMUSE_CPU_LIMIT`, `AUDIOMUSE_MEMORY_LIMIT`, `AUDIOMUSE_MAX_UPLOAD_BYTES` and `AUDIOMUSE_READ_TIMEOUT_SECONDS` when appropriate. A long or highly compressed file can still exceed decoding memory limits; a container restart leaves durable retry/recovery to the server. These defaults are deployment limits, not measured performance requirements.

No analyzer healthcheck is configured in Compose because health requires the private token. The admin connection test checks authenticated readiness. Analysis submissions also reject unavailable engines. A missing or invalid model causes `ready: false` and safe retries rather than accepting empty analysis.

## HTTP contract

Every request requires `Authorization: Bearer <token>`. No cookies, account identifiers, artist names, titles, file paths or source URLs are needed.

`GET /health` returns `protocolVersion`, `engineName`, `engineVersion`, `profile`, `ready` and `busy`. Readiness is false when source/model import, checksums or model loading fail. Missing or incorrect manifest files cannot enable analysis.

`POST /v1/analyze` uses multipart form data with exactly these fields:

| Field | Value |
| --- | --- |
| `audio` | Audio bytes; filename is ignored |
| `request_id` | Attempt UUID |
| `profile` | `musicnn-v1` |

A successful response contains:

- `protocolVersion: 1`, `featureSchemaVersion: 1`.
- `requestId`, `audioSha256`, `profile`.
- `engineName: AudioMuse-AI`, `engineVersion: 3.7.1`.
- `models`: embedding and prediction names with the actual validated SHA256.
- `embedding`: exactly 200 finite numbers.
- `features`: `tempo`, `energy`, `key`, `scale`, `durationSeconds`, and the full `moods` label-to-score dictionary.

The labels include genre, mood, decade and vocal descriptors. Scores are model outputs, not calibrated probabilities. Tempo and key estimates can be imperfect. No cloud service is involved in inference. Decoded duration is measured by the upstream decoder and can be shorter than the asset on corrupt/truncated files; the consuming service must apply its completeness policy.

Errors expose only a fixed category: 400 invalid request, 401 unauthorized, 404 route missing, 413 upload too large, 429 worker busy, 500 analysis failed, 503 engine unavailable. Stock `/api/analysis/start` and `/external/get_score` routes are not exposed here.

## Resource and lifecycle behavior

One analysis is admitted at a time. A second request receives 429. Uploaded content is bounded, kept in memory during multipart parsing, then written under an opaque temporary directory as `audio.bin`. Temporary audio is removed on success and failure. Names supplied in multipart headers are not used as paths or logged.

| Environment variable | Default |
| --- | --- |
| `AUDIOMUSE_BRIDGE_TOKEN` | Required |
| `AUDIOMUSE_BIND` | `127.0.0.1`; Docker sets `0.0.0.0` inside its private namespace |
| `AUDIOMUSE_PORT` | `8786` |
| `AUDIOMUSE_MODEL_DIRECTORY` | `/models` |
| `AUDIOMUSE_MODEL_MANIFEST` | `/models/manifest.json` |
| `AUDIOMUSE_MAX_UPLOAD_BYTES` | `536936448`, including multipart overhead |
| `AUDIOMUSE_READ_TIMEOUT_SECONDS` | `120` |

The upload bound is not a memory bound for decoded audio. Set deployment memory/CPU limits and a suitable upload size. `AUDIOMUSE_READ_TIMEOUT_SECONDS` bounds socket reads; it does not terminate native inference. A disconnected caller does not kill an in-flight analyzer. The ASP.NET queue must avoid overlapping retries, reject stale completions and retry busy/unavailable work with backoff. Stopping or killing the container aborts in-flight analysis; durable queue recovery belongs to ASP.NET. Identical bytes/models/profile produce the same analysis request identity but no bridge-side result cache or job database is maintained.

## Upstream adaptation and licenses

`prepare_upstream.py` verifies the Git commit and original SHA256 of both touched source files before adapting them:

1. Remove the final import-time `_apply_db_overrides()` call from `config.py`. The standalone bridge uses its environment and manifest and never hydrates AudioMuse database settings.
2. Make `_plugin_onnx_providers()` return an empty list. This keeps plugin discovery from booting unrelated AudioMuse components. CPU ONNX inference remains unchanged.

No persistence methods are invoked. The retained upstream database module is imported by its song module, so lightweight Flask and PostgreSQL Python packages remain dependencies, but no PostgreSQL service is required.

Upstream code is AGPL-3.0-only. The image retains the full pinned source, its LICENSE, and Git metadata under `/opt/audiomuse`. Distribute these adapter sources, requirements and `prepare_upstream.py` with a deployed modified service and satisfy the source-availability requirements for network users. The two published model artifacts are separate assets; their applicable weight terms must be reviewed separately from the repository software license. The release provides model bytes and checksums but no artifact-specific license declaration was established by this review. Do not infer model redistribution rights from AGPL alone.

The bridge suppresses upstream Python logging because native analyzer exceptions can contain audio metadata. HTTP access logging and exception text responses are disabled. Runtime errors return fixed categories. Keep operator diagnostics separate from public responses.

## Verification

```bash
python3 -m unittest discover -s tests -v
```

Tests exercise the HTTP contract with a fake analyzer, authentication, bounded uploads, busy admission, malformed multipart data, temporary-file cleanup, safe errors, finite numeric result validation, pinned manifest integrity and upstream adaptation boundaries. They do not establish real model speed, recommendation quality, decoder compatibility or end-to-end deployment readiness.
