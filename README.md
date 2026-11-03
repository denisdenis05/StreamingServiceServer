# StreamingServiceServer

StreamingServiceServer is the ASP.NET Core backend for the StreamingService music library. It stores library metadata and user collections, exposes authenticated APIs, resolves protected media, and runs background import, cover, and preview work against PostgreSQL and configured external download services.

[Deploy with Docker Compose](#deploy-with-docker-compose) · [Build and test](#build-and-test) · [Project layout](#project-layout) · [Operations](#operations-and-limits)

## Deploy with Docker Compose

The supported container deployment uses .NET 9, an existing PostgreSQL database, external downloader services where configured, and host directories for music, covers, and downloads. For a fresh checkout without a local `.env`, copy the tracked example and fill in values locally:

```sh
cp .env.example .env
chmod 600 .env
```

Set the required database, JWT issuer/audience/signing key, public origin, MusicBrainz identification, host paths, and downloader settings as needed. The directories for music, covers, and downloads must already exist and be accessible to the container user. `chmod 600` protects the local file on Unix-like systems; keep real values out of tracked files and command output.

Validate the Compose configuration without printing its resolved environment, then build and start the services:

```sh
docker compose config --quiet
docker compose up --build -d
```

Compose maps host port 3003 to container port 8080. It does not create PostgreSQL or downloader services. The `migrations` service applies pending Entity Framework migrations to the same configured database before the API starts. The database must retain its EF migration history and grant the rights required by pending migrations. Use `docker compose up` to deploy a new image or migration; `docker compose restart` does not build an image or apply a new migration. See [DEPLOYMENT.md](DEPLOYMENT.md) for environment mapping, filesystem permissions, migration behavior, and proxy setup.

## API and media behavior

The API provides authentication and protected library, playlist, saved album, liked-recording, metadata, recommendation, history, and Last.fm operations. Controller attributes define the routes; see [`StreamingServiceServer.API/Controllers`](StreamingServiceServer.API/Controllers) for the current route contracts.

Spotify catalog lookup and search are optional, read-only projections at `GET spotify/catalog/recordings/{identifier}` and `GET spotify/catalog/recordings/search`. They use the existing app Bearer authorization and local catalog data. The server does not store Spotify credentials or ask Spotify to change a queue. The library consumes these projections through an app-authenticated adapter. Current metadata projection changes are tracked in [SPOTIFY_CATALOG_PROGRESS.md](SPOTIFY_CATALOG_PROGRESS.md); source changes are not evidence that a deployment has been updated.

Protected audio delivery expects an external Nginx proxy configured for the stream controller's `X-Accel-Redirect` response. Kestrel alone does not return the protected audio body. TLS termination, internal file mapping, range support, and filesystem access belong to the external proxy. The Compose stack does not start Nginx. The browser dashboard has a separate authenticated session-cookie flow for exact stream requests; see [BROWSER_AUDIO_PROGRESS.md](BROWSER_AUDIO_PROGRESS.md).

## Project layout

| Project | Responsibility |
| --- | --- |
| `StreamingServiceServer.API` | ASP.NET Core routes, authentication, dashboard, and media endpoints |
| `StreamingServiceServer.Business` | Library and domain services |
| `StreamingServiceServer.Data` | Entity Framework models, persistence, and migrations support |
| `StreamingServiceServer.Background` | Hosted background work |
| `StreamingServiceServer.Downloader` | Configured download-provider integration |
| `StreamingServiceServer.PreviewDownloader` | Preview lookup and download integration |
| `StreamingServiceServer.Migrations` | Deployment migration runner |
| `StreamingServiceServer.CoverTests` | API, database, migration, cover, catalog, and other server tests |

## Build and test

Install the .NET 9 SDK. These commands build from the solution root:

```sh
dotnet restore StreamingServiceServer.sln
dotnet build StreamingServiceServer.sln
dotnet test StreamingServiceServer.CoverTests/StreamingServiceServer.CoverTests.csproj
```

PostgreSQL tests marked with `PostgresFact` are skipped unless `COVER_TEST_POSTGRES` identifies a disposable local PostgreSQL database. Other database fixtures can have their own requirements; inspect the chosen suite before running it. Use an isolated test database and never point a test run at production data. A clean checkout may require local settings for launching the API; the commands above do not imply a working localhost instance or provision external services.

## Operations and limits

- Container paths `/data/music`, `/data/covers`, and `/data/downloads` map to host directories. The API container needs the intended read/write access, and external Nginx needs read/traverse access to protected music files.
- Configure the external proxy as trusted to the API and terminate TLS there. Follow the example and exact forwarded-header assumptions in [DEPLOYMENT.md](DEPLOYMENT.md).
- Preview availability depends on the configured external downloader and remote source behavior. The server does not promise that every remote recording is available or in a browser-supported format.
- Source builds and tests do not establish a deployed migration, working production proxy, or successful audio delivery through the host filesystem.

## Documentation

- [DEPLOYMENT.md](DEPLOYMENT.md): Compose environment, migration runner, directories, and external Nginx
- [COVER_ASSET_PROGRESS.md](COVER_ASSET_PROGRESS.md): cover storage and asset processing
- [BROWSER_AUDIO_PROGRESS.md](BROWSER_AUDIO_PROGRESS.md): dashboard player, authenticated stream session, and remaining deployment checks
- [LIKED_SONGS_PLAYLIST_PROGRESS.md](LIKED_SONGS_PLAYLIST_PROGRESS.md): playlist-backed liked recordings
- [HOME_RECOMMENDATIONS_PROGRESS.md](HOME_RECOMMENDATIONS_PROGRESS.md): Home recommendation APIs
- [HISTORY_CONTEXT_PROGRESS.md](HISTORY_CONTEXT_PROGRESS.md): collection context in listening history
- [SPOTIFY_CATALOG_PROGRESS.md](SPOTIFY_CATALOG_PROGRESS.md): read-only recording catalog projection

Mobile, Player, and SpotifyJam are separate sibling repositories. Their application behavior and local package integrations are documented in their respective root READMEs.
