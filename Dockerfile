FROM mcr.microsoft.com/dotnet/sdk:9.0-bookworm-slim AS build
WORKDIR /source

COPY StreamingServiceServer.API/StreamingServiceServer.API.csproj StreamingServiceServer.API/
COPY StreamingServiceServer.Business/StreamingServiceServer.Business.csproj StreamingServiceServer.Business/
COPY StreamingServiceServer.Data/StreamingServiceServer.Data.csproj StreamingServiceServer.Data/
COPY StreamingServiceServer.Downloader/StreamingServiceServer.Downloader.csproj StreamingServiceServer.Downloader/
COPY StreamingServiceServer.PreviewDownloader/StreamingServiceServer.PreviewDownloader.csproj StreamingServiceServer.PreviewDownloader/
COPY StreamingServiceServer.Background/StreamingServiceServer.Background.csproj StreamingServiceServer.Background/
COPY StreamingServiceServer.Migrations/StreamingServiceServer.Migrations.csproj StreamingServiceServer.Migrations/
RUN dotnet restore StreamingServiceServer.API/StreamingServiceServer.API.csproj
RUN dotnet restore StreamingServiceServer.Migrations/StreamingServiceServer.Migrations.csproj

COPY . .
RUN dotnet publish StreamingServiceServer.API/StreamingServiceServer.API.csproj --configuration Release --no-restore --output /publish /p:UseAppHost=false
RUN dotnet publish StreamingServiceServer.Migrations/StreamingServiceServer.Migrations.csproj --configuration Release --no-restore --output /migrations /p:UseAppHost=false

FROM mcr.microsoft.com/dotnet/aspnet:9.0-bookworm-slim AS migrations
WORKDIR /app
COPY --from=build /migrations .
USER $APP_UID
ENTRYPOINT ["dotnet", "StreamingServiceServer.Migrations.dll"]

FROM mcr.microsoft.com/dotnet/aspnet:9.0-bookworm-slim AS runtime
ARG YT_DLP_VERSION=2026.8.19
RUN apt-get update \
    && apt-get install --yes --no-install-recommends ca-certificates curl ffmpeg python3-venv \
    && python3 -m venv /opt/preview-downloader \
    && /opt/preview-downloader/bin/pip install --no-cache-dir "yt-dlp[default]==${YT_DLP_VERSION}" \
    && rm -rf /var/lib/apt/lists/*

WORKDIR /app
COPY --from=build /publish .
COPY deployment/settings.json /app/appsettings.json
RUN mkdir -p /app/Tools /data/music /data/covers /data/downloads \
    && ln -s /opt/preview-downloader/bin/yt-dlp /app/Tools/preview-downloader \
    && chown -R app:app /data

ENV ASPNETCORE_ENVIRONMENT=Production \
    ASPNETCORE_HTTP_PORTS=8080 \
    XDG_CACHE_HOME=/tmp/streaming-service-cache
USER $APP_UID
EXPOSE 8080
ENTRYPOINT ["dotnet", "StreamingServiceServer.API.dll"]
