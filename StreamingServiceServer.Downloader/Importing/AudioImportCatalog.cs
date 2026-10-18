using StreamingServiceServer.Business.Helpers;
using StreamingServiceServer.Business.Models.MusicSearch;
using StreamingServiceServer.Business.Services.Images;

namespace StreamingServiceDownloader.Importing;

public static class AudioImportCatalog
{
    public static RecordingResponse CreateExpected(RecordingDto recording, IImageService images)
    {
        var response = recording.ToResponse(images);
        var albumArtist = recording.Releases?.FirstOrDefault()?.Artist?.Name;
        if (!string.IsNullOrWhiteSpace(albumArtist)) response.ArtistName = albumArtist;
        return response;
    }

    public static void RequireMatchingDestinations(IReadOnlyList<RecordingResponse> expected, IReadOnlyList<RecordingResponse> saved)
    {
        var savedByIdentifier = saved.ToDictionary(recording => recording.Id);
        if (!savedByIdentifier.Keys.ToHashSet().SetEquals(expected.Select(recording => recording.Id))
            || expected.Any(recording => MusicLocationHelper.GetRelativeFolderPathForRecording(recording)
                != MusicLocationHelper.GetRelativeFolderPathForRecording(savedByIdentifier[recording.Id])))
            throw new IOException("Saved album metadata differs from admitted import destinations; source is retained.");
    }
}
