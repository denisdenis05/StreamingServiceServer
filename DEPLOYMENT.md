# Production Compose deployment

## Start

From the server repository:

```sh
cp .env.example .env
chmod 600 .env
```

Fill the empty values in `.env`, then start the stack:

```sh
docker compose config --quiet
docker compose up --build -d
docker compose ps
```

The service is available at `http://localhost:3003`. Nginx owns host port 3003 and proxies to the API on internal port 8080. The API publishes no host port. The Dockerfile publishes the API with `dotnet publish --configuration Release` and runs with both .NET environment names set to Production. Swagger remains disabled under the existing production behavior.

The stack expects an existing PostgreSQL database with the application's schema and existing download-provider services. It does not provision a database, apply migrations, or start torrent/Soulseek services. Apply the project's existing EF migrations before using a new database.

## Required environment values

| Variable | Purpose |
| --- | --- |
| DATABASE_CONNECTION_STRING | PostgreSQL connection string reachable from inside the server container. Container localhost is not the Docker host. |
| JWT_SECRET_KEY | Private signing key, at least 32 bytes for the existing HMAC-SHA256 authentication. |
| JWT_ISSUER, JWT_AUDIENCE | Values expected by your clients and issued tokens. |
| PUBLIC_BASE_URL | Public server origin used to produce cover URLs. Include the scheme and omit a trailing slash. |
| MUSICBRAINZ_USER_AGENT | Application identification and contact information for the external MusicBrainz service. |
| MUSIC_HOST_PATH | Absolute host directory containing the music library. |
| COVERS_HOST_PATH | Absolute host directory containing cover images. |
| DOWNLOADS_HOST_PATH | Absolute host directory shared with the external downloader. |

All three directories must already exist. Bind mounts deliberately fail if a directory is missing. Both containers default to UID/GID `1654:1654`; the server needs read/write access and Nginx needs read access to the music files. Set `SERVER_USER` to the owner UID/GID of existing directories if necessary. File ownership on the host is preserved.

To generate a signing key locally:

```sh
openssl rand -base64 64
```

Store its output only in your local environment or `.env`. Use single quotes around `.env` values containing dollar signs, spaces, semicolons or hash characters so Compose treats credentials as intended. Do not put real values into `.env.example`.

## Optional configuration

The `.env.example` lists optional Last.fm and download-provider values without supplying credentials. Configure `LASTFM_API_KEY`, `LASTFM_API_SECRET` and `LASTFM_REDIRECT` to use Last.fm. Configure `TORRENT_URL`, `TORRENT_USER`, `TORRENT_PASSWORD` and `DOWNLOAD_SOURCE_*` to use the corresponding provider. Compose passes empty values for omitted credentials, rather than supplying default passwords.

Provider settings from configuration seed the application's provider records when its database is empty. Existing database provider records are managed by the application's provider configuration; changing `.env` does not overwrite them.

Additional optional variables:

| Variable | Default / meaning |
| --- | --- |
| SERVER_BIND_ADDRESS | `0.0.0.0`; use `127.0.0.1` when a TLS reverse proxy on the host is the public entry point. |
| SERVER_USER | `1654:1654`; host filesystem UID/GID used by both containers. |
| TORRENT_MAX_DOWNLOADS | `3`. |
| MUSICBRAINZ_BASE_URL | Public MusicBrainz API endpoint. |
| ALBUM_COVER_BASE_URL | Public Cover Art Archive release endpoint. |
| PREVIEW_DOWNLOADER_ARGUMENTS | `-x --audio-format mp3 --no-playlist -o`. |
| PREVIEW_DOWNLOADER_SEARCH_ARGUMENTS | `ytsearch1:`. |
| LOG_LEVEL | `Information`. |
| ASPNETCORE_LOG_LEVEL | `Warning`. |

The image includes FFmpeg, Python and yt-dlp 2026.8.19 at the executable path expected by PreviewDownloaderService. `YT_DLP_VERSION` is a Docker build argument for deliberately updating that tool; credentials are never build arguments. External extractor support depends on yt-dlp and the remote site. Sites requiring additional JavaScript runtimes need those runtimes installed in a derived image.

## How configuration reaches appsettings

The container's `/app/appsettings.json` comes from `deployment/settings.json`, a template containing only empty deployment fields and container defaults. Your existing `StreamingServiceServer.API/appsettings.json` and `appsettings.Development.json` remain untouched and are excluded from the Docker build context. They are not copied into any image layer.

`WebApplication.CreateBuilder` already loads environment variables after JSON configuration. Compose maps readable deployment variable names to .NET keys, for example:

| Compose input | Container variable | IConfiguration key |
| --- | --- | --- |
| DATABASE_CONNECTION_STRING | ConnectionStrings__DefaultConnection | ConnectionStrings:DefaultConnection |
| JWT_SECRET_KEY | Jwt__SecretKey | Jwt:SecretKey |
| LASTFM_API_SECRET | LastFm__ApiSecret | LastFm:ApiSecret |
| TORRENT_PASSWORD | Music__DownloadSource__Torrent__Password | Music:DownloadSource:Torrent:Password |

The double underscore denotes a configuration section. Environment values override JSON; the JSON file itself is not rewritten. `${VARIABLE}` strings inside JSON would not perform this substitution, so the template does not use them.

Compose reads `.env` for interpolation automatically. Exported shell variables take precedence over that file. An alternate file can be used with `docker compose --env-file /path/to/private.env up --build -d`. Extra application settings can be added to the service's `environment` mapping using their .NET double-underscore names, or supplied in a local Compose override with `env_file`. Required Compose inputs must still be supplied for interpolation.

`.env`, `.env.*` except `.env.example`, and `secrets/` are Git-ignored. Docker also excludes local settings, environment files, signing keys, IDE data and build outputs. No real settings or credentials are needed to build the image. Avoid sharing the output of `docker compose config` or container inspection, since resolved runtime environment values may be displayed; use `docker compose config --quiet` for validation.

## Streaming and HTTPS

The current stream controller returns `X-Accel-Redirect`, so directly exposing Kestrel would return no audio body. Nginx handles that header and serves the shared music directory with byte-range support. Its `/protected/` locations are internal and cannot be requested directly. The second internal location handles the absolute music path currently returned by the preview fallback. Only music files are mounted into the proxy, read-only; the rest of the host filesystem is not exposed through the alias.

Port 3003 is HTTP. For a public production endpoint, terminate TLS at your existing reverse proxy and route it to port 3003. On the same host, bind this stack to `127.0.0.1`. The TLS proxy must set `X-Forwarded-Proto: https`; the included Nginx forwards that HTTPS indication and replaces the forwarded client-address header. The API accepts forwarded headers only through this unexposed container entry point. Do not expose the API container separately.

Production JWT diagnostic logging no longer enables personally identifiable information, and JWT HTTPS metadata is required outside Development. These changes do not modify the existing CORS policy or downloader HTTP behavior.

## Operations

```sh
docker compose logs --follow --tail 100
docker compose up --build -d
docker compose down
```

Bind-mounted media, covers and downloads survive container replacement. Health checks verify the static application endpoint; they do not establish database readiness or external-provider availability. Log rotation is configured for both containers.

After changing `.env`, run `docker compose up -d` to recreate affected containers. `docker compose restart` does not reload their environment. No production configuration, database mutation or running deployment is performed merely by building the image.

## References

- [ASP.NET Core configuration precedence and environment variables](https://learn.microsoft.com/en-us/aspnet/core/fundamentals/configuration/?view=aspnetcore-9.0)
- [Compose environment interpolation and precedence](https://docs.docker.com/compose/how-tos/environment-variables/variable-interpolation/)
- [yt-dlp installation and runtime dependencies](https://github.com/yt-dlp/yt-dlp#dependencies)

## Build record

On 2026-10-15, docker compose config --quiet passed using temporary nonsecret placeholder inputs. docker build and docker compose build both completed the actual Release publication and runtime image build. Image metadata reports runtime UID 1654 and internal port 8080. The included proxy configuration passed nginx -t as UID/GID 1654:1654 with a read-only root filesystem and writable /tmp.

This host does not have the Docker buildx plugin, so its Docker/Compose installation used the classic builder successfully. No app test suites were added or run for this deployment change. No containers were started against the real database or providers, and no live deployment was performed. Supply your private environment and mount directories before docker compose up.

The user's existing edits to both appsettings files and IDE directory were left untouched and excluded from this commit.
