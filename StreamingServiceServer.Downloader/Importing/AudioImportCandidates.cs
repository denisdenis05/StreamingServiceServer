namespace StreamingServiceDownloader.Importing;

public static class AudioImportCandidates
{
    public static List<(string fileName, string fullPath)> Read(string sourceDirectory, string musicRoot)
    {
        var extensions = new HashSet<string>(StringComparer.OrdinalIgnoreCase)
            { ".mp3", ".flac", ".wav", ".aac", ".ogg", ".m4a", ".alac", ".wma", ".opus" };
        var sources = Directory.Exists(sourceDirectory)
            ? Directory.EnumerateFiles(sourceDirectory, "*", SearchOption.AllDirectories).Where(path => extensions.Contains(Path.GetExtension(path)))
            : Enumerable.Empty<string>();
        var rejected = Path.Combine(musicRoot, ".audio-import-rejected");
        var recovered = Directory.Exists(rejected) ? Directory.EnumerateFiles(rejected).Take(1000) : [];
        return sources.Concat(recovered).Distinct(StringComparer.Ordinal)
            .Select(path => (Path.GetFileName(path), path)).ToList();
    }
}
