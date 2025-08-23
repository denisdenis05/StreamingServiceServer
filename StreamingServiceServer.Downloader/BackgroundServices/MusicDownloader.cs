using System.Web;
using FuzzySharp;
using HtmlAgilityPack;
using Microsoft.Extensions.Configuration;
using Microsoft.Extensions.Hosting;
using StreamingServiceDownloader.Helpers;
using StreamingServiceDownloader.Models;
using StreamingServiceServer.Business.Helpers;
using StreamingServiceServer.Data;
using StreamingServiceServer.Data.Models;

public class MusicDownloader : BackgroundService
{
    private readonly StreamingDbContext _dbContext;
    private readonly HttpClient _httpClient;
    private readonly IConfiguration _configuration;
    private readonly string _downloadPath ,_baseUrl, _urlParameters, _retryUrlParameters;
    private readonly int _maxDownloadCount;
    private readonly ITorrentHelper _torrentHelper;
    
    public MusicDownloader(StreamingDbContext dbContext ,IHttpClientFactory httpClientFactory, IConfiguration configuration, ITorrentHelper torrentHelper)
    {
        _httpClient = httpClientFactory.CreateClient("unsafeHttp");
        _torrentHelper = torrentHelper;
        _configuration = configuration;
        
        _downloadPath = _configuration["Music:DownloadPath"];
        _maxDownloadCount = Int32.Parse(_configuration["Music:DownloadSource:Torrent:MaxDownloads"]);
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

            await Task.Delay(TimeSpan.FromSeconds(15), stoppingToken);
        }
    }

    private async Task DownloadTrackAsync()
    {
        await Task.Delay(3000);
        
        if (await _torrentHelper.GetActiveDownloadCountAsync() > _maxDownloadCount)
            return;
        
        var albumsToDownload = _dbContext.ReleasesToDownload
            .Where(release => !_dbContext.PendingDownloads
                .Any(p => p.Id == release.Id))
            .Where(release => !_dbContext.FailedDownloads
                .Any(p => p.Id == release.Id))
            .ToList();

        if (!albumsToDownload.Any())
            return;
        
        foreach (var album in albumsToDownload)
        {
            if (await _torrentHelper.GetActiveDownloadCountAsync() > _maxDownloadCount)
                return;
            
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
                var fullAlbumPath = Path.Combine(_downloadPath, MusicLocationHelper.SanitizeFolderName(matchingTorrent.Title));
                await _torrentHelper.AddTorrentAsync(matchingTorrent.MagnetLink, fullAlbumPath);
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
                var fullAlbumPath = Path.Combine(_downloadPath, MusicLocationHelper.SanitizeFolderName(matchingTorrent.Title));
                await _torrentHelper.AddTorrentAsync(matchingTorrent.MagnetLink, fullAlbumPath);
                return matchingTorrent.Title;
            }
        }
        
        await SetFailedDownload(releaseToDownload);
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
    
    private List<(string candidate, int score, int artistScore, int albumScore)> 
        GetMatchesSorted(string artist, string album, List<string> candidates)
    {
        string normalizedArtist = StringSanitizers.SanitizeTitle(artist).ToLower();
        string normalizedAlbum = StringSanitizers.SanitizeTitle(album).ToLower();
        string target = $"{normalizedArtist} {normalizedAlbum}";

        var scoredList = new List<(string candidate, int score, int artistScore, int albumScore)>();

        foreach (var candidate in candidates)
        {
            string normalizedCandidate = StringSanitizers.SanitizeTitle(candidate).ToLower();

            int artistScore = Fuzz.PartialRatio(normalizedArtist, normalizedCandidate);
            int albumScore = Fuzz.PartialRatio(normalizedAlbum, normalizedCandidate);

            if (artistScore == 0)
                continue;

            int combinedScore = (int)(artistScore * 0.6 + albumScore * 0.4);

            scoredList.Add((candidate, combinedScore, artistScore, albumScore));
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
        
        var (match, score, artistScore, albumScore) = matches.First();
        if(score > 70 && albumScore > 60 && artistScore > 60)
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
    
    private async Task SetFailedDownload(ReleaseToDownload releaseToDownload)
    {
        await _dbContext.FailedDownloads.AddAsync(new FailedDownload
        {
            Id = releaseToDownload.Id,
            Title = releaseToDownload.Title,
            Artist = releaseToDownload.Artist,
        });
        await _dbContext.SaveChangesAsync();
    }
}