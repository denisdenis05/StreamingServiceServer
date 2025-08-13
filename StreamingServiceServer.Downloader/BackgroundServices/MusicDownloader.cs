using System.Web;
using FuzzySharp;
using HtmlAgilityPack;
using Microsoft.Extensions.Configuration;
using Microsoft.Extensions.Hosting;
using StreamingServiceDownloader.Helpers;
using StreamingServiceDownloader.Models;
using StreamingServiceServer.Data;
using StreamingServiceServer.Data.Models;

public class MusicDownloader : BackgroundService
{
    private readonly StreamingDbContext _dbContext;
    private readonly HttpClient _httpClient;
    private readonly IConfiguration _configuration;
    private readonly string _baseUrl, _urlParameters, _retryUrlParameters;
    private readonly ITorrentHelper _torrentHelper;
    
    public MusicDownloader(StreamingDbContext dbContext ,IHttpClientFactory httpClientFactory, IConfiguration configuration, ITorrentHelper torrentHelper)
    {
        _httpClient = httpClientFactory.CreateClient("unsafeHttp");
        _torrentHelper = torrentHelper;
        _configuration = configuration;
        
        _baseUrl = _configuration["Music:DownloadSource:BaseUrl"];
        _urlParameters = _configuration["Music:DownloadSource:Parameters"];
        _retryUrlParameters = _configuration["Music:DownloadSource:RetryParameters"];
        
        _dbContext = dbContext;
    }

    protected override async Task ExecuteAsync(CancellationToken stoppingToken)
    {
        while (!stoppingToken.IsCancellationRequested)
        {
            try
            {
                await DownloadTrackAsync();
            }
            catch
            {
            }

            await Task.Delay(TimeSpan.FromMinutes(5), stoppingToken);
        }
    }

    private async Task DownloadTrackAsync()
    {
        var albumsToDownload = _dbContext.ReleasesToDownload
            .Where(release => !_dbContext.PendingDownloads
                .Any(p => p.Id == release.Id))
            .ToList();

        foreach (var album in albumsToDownload)
        {
            var downloadTitle = await ScrapeMusicAsync(album);

            if (downloadTitle != null)
            {
                await LogDownload(album.Id, downloadTitle);
            }
        }
    }
    
    public async Task<string?> ScrapeMusicAsync(ReleaseToDownload releaseToDownload)
    {
        var queries = new[]
        {
            $"{releaseToDownload.Artist} {releaseToDownload.Title}",
            releaseToDownload.Title,
            releaseToDownload.Artist
        };

        foreach (var query in queries)
        {
            var sanitizedQuery = SanitizeQuery(query);

            var searchPage = await GetSearchPage(sanitizedQuery, _urlParameters);
            var torrentList = await GetTorrents(searchPage);

            var matchingTorrent = GetBestTorrent(releaseToDownload, torrentList);
            if (matchingTorrent != null)
            {
                await _torrentHelper.AddTorrentAsync(matchingTorrent.MagnetLink);
                return matchingTorrent.Title;
            }
        }

        foreach (var query in queries)
        {
            var sanitizedQuery = SanitizeQuery(query);

            var searchPage = await GetSearchPage(sanitizedQuery, _retryUrlParameters);
            var torrentList = await GetTorrents(searchPage);

            var matchingTorrent = GetBestTorrent(releaseToDownload, torrentList);
            if (matchingTorrent != null)
            {
                await _torrentHelper.AddTorrentAsync(matchingTorrent.MagnetLink);
                return matchingTorrent.Title;
            }
        }

        return null;
    }
    
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

        using var response = await _httpClient.GetAsync(
            url,
            HttpCompletionOption.ResponseHeadersRead
        );

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

        if (rows == null)
            return results; 

        foreach (var row in rows)
        {
            var cells = row.SelectNodes("./td");
            if (cells == null || cells.Count < 8) continue; // skip header/pagination rows

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
            Console.WriteLine(torrent.Title);
            Console.WriteLine("././.......");
            results.Add(torrent);
        }
        
        return results;
    }
    
    private List<(string candidate, int score)> GetMatchesSorted(string artist, string album, List<string> candidates)
    {
        string target = $"{artist} {album}".ToLower();

        var scoredList = new List<(string candidate, int score)>();

        foreach (var candidate in candidates)
        {
            string normalized = StringSanitizers.SanitizeTitle(candidate).ToLower();
            int score = Fuzz.TokenSetRatio(target, normalized);
            scoredList.Add((candidate, score));
        }

        return scoredList
            .OrderByDescending(x => x.score)
            .ToList();
    }

    private TorrentResult? GetBestTorrent(ReleaseToDownload releaseToDownload, ICollection<TorrentResult> torrentList)
    {
        var matches = GetMatchesSorted(releaseToDownload.Artist, releaseToDownload.Title, torrentList.Select(result => result.Title).ToList());
        
        if(matches.Count == 0)
            return null;
        
        var (match, score) = matches.First();
        if(score > 60)
            return torrentList.Where(torrent => torrent.Title == match).FirstOrDefault();

        return null;
    }

    private async Task LogDownload(Guid albumId, string downloadTitle)
    {
        await _dbContext.PendingDownloads.AddAsync(new PendingDownload
        {
            Id = albumId,
            SourceName = downloadTitle
        });
        await _dbContext.SaveChangesAsync();
    }
}