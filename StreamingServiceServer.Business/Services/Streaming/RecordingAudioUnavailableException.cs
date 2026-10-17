namespace StreamingServiceServer.Business.Services.MusicSearch;

public sealed class RecordingAudioUnavailableException(Guid recordingId)
    : Exception($"Audio is not available for recording {recordingId}.");
