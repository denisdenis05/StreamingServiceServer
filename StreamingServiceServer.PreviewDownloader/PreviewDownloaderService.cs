using System.Diagnostics;
using Microsoft.Extensions.Configuration;
using StreamingServiceServer.Business.Models.MusicSearch;
using StreamingServiceServer.Data.Models;

namespace StreamingServiceServer.PreviewDownloader;

public class PreviewDownloaderService : IPreviewDownloaderService
{
    private readonly IConfiguration _configuration;

    public PreviewDownloaderService(IConfiguration configuration)
    {
        _configuration = configuration;
    }

    public async Task<string> DownloadPreview(RecordingResponse recording)
    {
        var downloadPath = _configuration["Music:Path"];
        var tempPath = Path.Combine(downloadPath, "TEMP");
        
        if (!Directory.Exists(tempPath))
        {
            Directory.CreateDirectory(tempPath);
        }

        var outputPath = Path.Combine(tempPath, $"{recording.Id}.mp3");

        if (File.Exists(outputPath))
        {
            return outputPath;
        }

        var query = $"{recording.Title} {recording.ArtistName} audio";
        
        var toolName = "preview-downloader";
        var toolsPath = Path.Combine(AppDomain.CurrentDomain.BaseDirectory, "Tools", toolName);

        var arguments = _configuration["Music:PreviewDownloader:Arguments"];
        var searchArguments = _configuration["Music:PreviewDownloader:SearchArguments"];
        
        var processStartInfo = new ProcessStartInfo
        {
            FileName = toolsPath,
            Arguments = $"{arguments} \"{outputPath}\" \"{searchArguments}{query}\"",
            RedirectStandardOutput = true,
            RedirectStandardError = true,
            UseShellExecute = false,
            CreateNoWindow = true
        };

        using var process = new Process();
        process.StartInfo = processStartInfo;
        
        process.Start();
        await process.WaitForExitAsync();

        if (process.ExitCode != 0)
        {
             var error = await process.StandardError.ReadToEndAsync();
             throw new Exception($"Preview downloader tool failed: {error}");
        }

        return outputPath;
    }
}
