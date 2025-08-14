using StreamingServiceDownloader.Models;

namespace StreamingServiceDownloader.Helpers;

public interface ITorrentHelper
{
    Task LoginAsync();
    Task AddTorrentAsync(string magnetLink);
    Task AddTorrentAsync(TorrentResult torrent);
    Task<TorrentInfo?> GetTorrentInfoAsync(string searchTerm);
    Task RemoveTorrentAsync(string torrentName, bool deleteFiles = true);
    Task<int> GetActiveDownloadCountAsync();
}