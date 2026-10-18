namespace StreamingServiceServer.Business.Models.MusicSearch;

public sealed record ReleaseSelection(Guid Id, string Title, string Artist, Guid GroupId,
    string GroupType, int? RecordingCount, string Date, string Country, string Format, string Disambiguation);

public sealed record ReleaseGroupSelection(Guid Id, string Title, string Artist, string Type,
    ReleaseSelection Release);

public sealed record ReleaseSelectionPage(IReadOnlyList<ReleaseSelection> Releases, int? NextOffset);
