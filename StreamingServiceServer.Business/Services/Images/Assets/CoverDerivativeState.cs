using Microsoft.EntityFrameworkCore;
using StreamingServiceServer.Data;
using StreamingServiceServer.Data.Models;

namespace StreamingServiceServer.Business.Services.Images.Assets;

public static class CoverDerivativeState
{
    public static Task<int> ClearAsync(StreamingDbContext database, CoverEntityKind kind, Guid identifier,
        CancellationToken cancellationToken) => ClearAsync(database, kind, identifier, null, null, cancellationToken);
    public static Task<int> ClearAsync(StreamingDbContext database, CoverEntityKind kind, Guid identifier,
        Guid? expectedJobVersion, string? expectedLocalAddress, CancellationToken cancellationToken) => kind == CoverEntityKind.Release
        ? database.Releases.Where(release => release.Id == identifier
            && (expectedJobVersion == null || database.CoverAssetJobs.Any(job => job.EntityKind == kind
                && job.EntityIdentifier == identifier && job.Version == expectedJobVersion))
            && (expectedLocalAddress == null || release.LocalCover == expectedLocalAddress)).ExecuteUpdateAsync(update => update
            .SetProperty(release => release.LocalCover, string.Empty).SetProperty(release => release.LocalSmallCover, string.Empty)
            .SetProperty(release => release.LocalVerySmallCover, string.Empty).SetProperty(release => release.OptimizedCover, string.Empty)
            .SetProperty(release => release.OptimizedSmallCover, string.Empty).SetProperty(release => release.OptimizedVerySmallCover, string.Empty), cancellationToken)
        : database.Recordings.Where(recording => recording.Id == identifier
            && (expectedJobVersion == null || database.CoverAssetJobs.Any(job => job.EntityKind == kind
                && job.EntityIdentifier == identifier && job.Version == expectedJobVersion))
            && (expectedLocalAddress == null || recording.LocalCover == expectedLocalAddress)).ExecuteUpdateAsync(update => update
            .SetProperty(recording => recording.LocalCover, string.Empty).SetProperty(recording => recording.LocalSmallCover, string.Empty)
            .SetProperty(recording => recording.LocalVerySmallCover, string.Empty).SetProperty(recording => recording.OptimizedCover, string.Empty)
            .SetProperty(recording => recording.OptimizedSmallCover, string.Empty).SetProperty(recording => recording.OptimizedVerySmallCover, string.Empty), cancellationToken);
}
