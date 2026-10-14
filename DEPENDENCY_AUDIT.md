# Server dependency audit

Audit date: 2026-10-14.

## Result

NuGet reported **no known vulnerabilities** in the direct or transitive packages resolved for the six projects in `StreamingServiceServer.sln`. Restore and the vulnerability query both completed with exit code `0`. Restore produced no vulnerability warnings, and the JSON vulnerability report contained all six project paths with no vulnerable package entries or errors.

This is a check against the advisories available from NuGet.org at the time of the audit. It does not establish that every dependency is free of vulnerabilities.

## Commands

Run from the server repository:

```bash
dotnet restore StreamingServiceServer.sln -p:NuGetAudit=true -p:NuGetAuditMode=all --source https://api.nuget.org/v3/index.json --verbosity minimal
dotnet list StreamingServiceServer.sln package --vulnerable --include-transitive --format json --no-restore --source https://api.nuget.org/v3/index.json
dotnet list StreamingServiceServer.sln package --include-transitive --no-restore --format json
```

The SDK used was .NET `10.0.112`; every project targets `net9.0`. The advisory source was `https://api.nuget.org/v3/index.json`. The inventory command also completed with exit code `0` and no reported problems.

`dotnet --info` reported SDKs `8.0.131`, `9.0.121`, and `10.0.112`. The CLI host was `10.0.12` on Linux x64. Installed `Microsoft.NETCore.App` and `Microsoft.AspNetCore.App` runtimes were `8.0.31`, `9.0.20`, and `10.0.12` for each runtime family. These local version observations are not a runtime vulnerability audit or evidence of the versions deployed on the server.

Initial attempts inside the workspace sandbox failed because the .NET CLI and NuGet needed to write their user initialization and download caches. The commands were rerun with approved escalation. The failed attempts were not counted as successful audit results.

## Coverage

The resolved inventory contains **129 unique package/version pairs** across the solution. Counts below include overlapping dependencies across projects; they must not be added to obtain a unique package count.

| Project | Direct packages | Transitive packages | Reported vulnerable packages |
| --- | ---: | ---: | ---: |
| API | 5 | 81 | 0 |
| Data | 4 | 58 | 0 |
| Business | 4 | 42 | 0 |
| Downloader | 6 | 44 | 0 |
| PreviewDownloader | 0 | 46 | 0 |
| Background | 2 | 36 | 0 |

Only NuGet package dependencies were audited. The commands do not assess the deployed .NET shared runtime, operating system, reverse proxy, database installation, or the bundled `StreamingServiceServer.PreviewDownloader/Tools/preview-downloader` executable. They also do not assess application code or configuration security. No other dependency ecosystem manifests were found in the server repository.

## Changes and follow-up

No dependency versions or tracked application files were changed. Restore refreshed generated project assets and the NuGet cache. Existing changes to the API appsettings files and the untracked `.idea/` directory were left untouched.

Repeat the commands after dependency changes and before deployment. There are no package advisory fixes indicated by this scan. A deployment dependency scan is still needed to cover the components listed outside NuGet coverage.
