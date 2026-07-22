using System.Web;
using FuzzySharp;
using HtmlAgilityPack;
using Microsoft.Extensions.Configuration;
using StreamingServiceDownloader.Models;
using StreamingServiceServer.Business.Helpers;
using StreamingServiceServer.Data;
using StreamingServiceServer.Data.Models;

namespace StreamingServiceDownloader.Helpers;

public class TorrentProvider : IDownloadProvider
{
    private readonly HttpClient _httpClient;
    private readonly ITorrentHelper _torrentHelper;
    private readonly IConfiguration _configuration;
    private readonly string _downloadPath;
    private readonly string _baseUrl;
    private readonly string _urlParameters;
    private readonly string _retryUrlParameters;
    private readonly int _maxDownloadCount;

    public string ProviderName => "Torrent";

    public TorrentProvider(
        IHttpClientFactory httpClientFactory,
        ITorrentHelper torrentHelper,
        IConfiguration configuration)
    {
        _httpClient = httpClientFactory.CreateClient("unsafeHttp");
        _torrentHelper = torrentHelper;
        _configuration = configuration;

        _downloadPath = _configuration["Music:DownloadPath"] ?? "./Music";
        _maxDownloadCount = int.TryParse(_configuration["Music:DownloadSource:Torrent:MaxDownloads"], out var max) ? max : 3;
        _baseUrl = _configuration["Music:DownloadSource:BaseUrl"] ?? "http://localhost";
        _urlParameters = _configuration["Music:DownloadSource:Parameters"] ?? "";
        _retryUrlParameters = _configuration["Music:DownloadSource:RetryParameters"] ?? "";
    }

    /// <summary>
    /// Reconfigure this provider with DB-stored settings at runtime.
    /// </summary>
    public void ApplyConfig(DownloadProviderConfig config)
    {
        // ExtraConfig is a JSON blob with TPB-specific settings
        if (!string.IsNullOrWhiteSpace(config.ExtraConfig) && config.ExtraConfig != "{}")
        {
            try
            {
                var extra = System.Text.Json.JsonSerializer.Deserialize<Dictionary<string, string>>(config.ExtraConfig);
                if (extra != null)
                {
                    // These could override _baseUrl, _urlParameters, etc. if present
                }
            }
            catch { }
        }
    }

    public async Task<DownloadSearchResult?> SearchAndEnqueueAsync(
        ReleaseToDownload release, string[] formatOrder, CancellationToken cancellationToken)
    {
        if (await _torrentHelper.GetActiveDownloadCountAsync() > _maxDownloadCount)
            return null;

        var queries = new[]
        {
            $"{release.Artist} {release.Title}",
            release.Title,
            release.Artist
        };

        // First pass with primary parameters
        foreach (var query in queries)
        {
            var sanitizedQuery = SanitizeQuery(query);
            var searchPage = await GetSearchPage(sanitizedQuery, _urlParameters);
            var torrentList = await GetTorrents(searchPage);
            var matchingTorrent = GetBestTorrent(release, torrentList);

            if (matchingTorrent != null)
            {
                var fullAlbumPath = Path.Combine(_downloadPath, MusicLocationHelper.SanitizeFolderName(matchingTorrent.Title));
                await _torrentHelper.AddTorrentAsync(matchingTorrent.MagnetLink, fullAlbumPath);
                return new DownloadSearchResult
                {
                    Success = true,
                    SourceIdentifier = matchingTorrent.Title,
                    SavePath = fullAlbumPath
                };
            }
        }

        // Second pass with retry parameters
        foreach (var query in queries)
        {
            var sanitizedQuery = SanitizeQuery(query);
            var searchPage = await GetSearchPage(sanitizedQuery, _retryUrlParameters);
            var torrentList = await GetTorrents(searchPage);
            var matchingTorrent = GetBestTorrent(release, torrentList);

            if (matchingTorrent != null)
            {
                var fullAlbumPath = Path.Combine(_downloadPath, MusicLocationHelper.SanitizeFolderName(matchingTorrent.Title));
                await _torrentHelper.AddTorrentAsync(matchingTorrent.MagnetLink, fullAlbumPath);
                return new DownloadSearchResult
                {
                    Success = true,
                    SourceIdentifier = matchingTorrent.Title,
                    SavePath = fullAlbumPath
                };
            }
        }

        return null;
    }

    public async Task<DownloadProgressInfo?> CheckProgressAsync(string sourceIdentifier, CancellationToken cancellationToken)
    {
        var info = await _torrentHelper.GetTorrentInfoAsync(sourceIdentifier);
        if (info == null) return null;

        return new DownloadProgressInfo
        {
            Progress = info.Progress,
            SavePath = info.SavePath,
            State = info.State
        };
    }

    public async Task CleanUpAsync(string sourceIdentifier, bool deleteFiles)
    {
        await _torrentHelper.RemoveTorrentAsync(sourceIdentifier, deleteFiles);
    }

    // ─── Private helpers (extracted from original MusicDownloader) ───

    private string SanitizeQuery(string query)
    {
        var sanitized = System.Text.RegularExpressions.Regex.Replace(query, @"[-'()\[\]]", " ");
        sanitized = System.Text.RegularExpressions.Regex.Replace(sanitized, @"\s+", " ").Trim();
        return sanitized;
    }

    private async Task<HtmlDocument> GetSearchPage(string query, string urlParameters)
    {
        string encodedQuery = Uri.EscapeDataString(query);
        string url = $"{_baseUrl}{encodedQuery}{urlParameters}";

        using var response = await _httpClient.GetAsync(url, HttpCompletionOption.ResponseHeadersRead);
        response.EnsureSuccessStatusCode();

        await using var stream = await response.Content.ReadAsStreamAsync();
        var searchPage = new HtmlDocument();
        searchPage.Load(stream);
        return searchPage;
    }

    private async Task<ICollection<TorrentResult>> GetTorrents(HtmlDocument searchPage)
    {
        var results = new List<TorrentResult>();
        var rows = searchPage.DocumentNode.SelectNodes("//table[@id='searchResult']/tr");
        if (rows == null) return results;

        foreach (var row in rows)
        {
            var cells = row.SelectNodes("./td");
            if (cells == null || cells.Count < 8) continue;

            var torrent = new TorrentResult
            {
                Title = HttpUtility.HtmlDecode(cells[1].SelectSingleNode(".//a").InnerText.Trim()),
                Uploaded = HttpUtility.HtmlDecode(cells[2].InnerText.Trim()),
                MagnetLink = cells[3].SelectSingleNode(".//a[starts-with(@href,'magnet:')]")
                    ?.GetAttributeValue("href", ""),
                Size = HttpUtility.HtmlDecode(cells[4].InnerText.Trim()),
                Seeders = int.TryParse(cells[5].InnerText.Trim(), out var s) ? s : 0,
                Leechers = int.TryParse(cells[6].InnerText.Trim(), out var l) ? l : 0,
                Uploader = HttpUtility.HtmlDecode(cells[7].InnerText.Trim())
            };
            results.Add(torrent);
        }

        return results;
    }

    private TorrentResult? GetBestTorrent(ReleaseToDownload releaseToDownload, ICollection<TorrentResult> torrentList)
    {
        var matches = GetMatchesSorted(releaseToDownload.Artist, releaseToDownload.Title,
            torrentList.Select(result => result.Title).ToList());

        if (matches.Count == 0) return null;

        var (match, score, artistScore, albumScore) = matches.First();
        if (score > 70 && albumScore > 60 && artistScore > 60)
            return torrentList.FirstOrDefault(torrent => torrent.Title == match);

        return null;
    }

    private List<(string candidate, int score, int artistScore, int albumScore)>
        GetMatchesSorted(string artist, string album, List<string> candidates)
    {
        string normalizedArtist = StringSanitizers.SanitizeTitle(artist).ToLower();
        string normalizedAlbum = StringSanitizers.SanitizeTitle(album).ToLower();

        var scoredList = new List<(string candidate, int score, int artistScore, int albumScore)>();

        foreach (var candidate in candidates)
        {
            string normalizedCandidate = StringSanitizers.SanitizeTitle(candidate).ToLower();
            int artistScore = Fuzz.PartialRatio(normalizedArtist, normalizedCandidate);
            int albumScore = Fuzz.PartialRatio(normalizedAlbum, normalizedCandidate);

            if (artistScore == 0) continue;

            int combinedScore = (int)(artistScore * 0.6 + albumScore * 0.4);
            scoredList.Add((candidate, combinedScore, artistScore, albumScore));
        }

        return scoredList.OrderByDescending(x => x.score).ToList();
    }
}
