using HtmlAgilityPack;
using System.Web;
using FuzzySharp;
using Microsoft.Extensions.Configuration;
using StreamingServiceDownloader.Helpers;
using StreamingServiceDownloader.Models;
using StreamingServiceDownloader.Services.ExternalMusicDownloader;
using StreamingServiceServer.Data.Models;

public class ExternalMusicDownloader : IExternalMusicDownloader
{
    private readonly HttpClient _httpClient;
    private readonly IConfiguration _configuration;
    private readonly string _baseUrl, _urlParameters;
    private readonly ITorrentHelper _torrentHelper;
    
    public ExternalMusicDownloader(IHttpClientFactory httpClientFactory, IConfiguration configuration)
    {
        _httpClient = httpClientFactory.CreateClient("tpb");
        _configuration = configuration;
        
        _baseUrl = _configuration["Music:DownloadSource:BaseUrl"];
        _urlParameters = _configuration["Music:DownloadSource:Parameters"];
        
        var torrentClientUrl = _configuration["Music:DownloadSource:Torrent:Url"];
        var torrentClientUser = _configuration["Music:DownloadSource:Torrent:User"];
        var torrentClientPassword = _configuration["Music:DownloadSource:Torrent:Password"];;
        _torrentHelper = new QbittorrentClient(torrentClientUrl, torrentClientUser, torrentClientPassword);
    }

    public async Task<bool> ScrapeMusicAsync(ReleaseToDownload releaseToDownload)
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

            var searchPage = await GetSearchPage(sanitizedQuery);
            var torrentList = await GetTorrents(searchPage);

            var matchingTorrent = GetBestTorrent(releaseToDownload, torrentList);
            if (matchingTorrent != null)
            {
                await _torrentHelper.AddTorrentAsync(matchingTorrent.MagnetLink);
                await LogDownload();
                return true;
            }
        }

        return false;
    }
    
    private string SanitizeQuery(string query)
    {
        var sanitized = System.Text.RegularExpressions.Regex.Replace(query, @"[-'()\[\]]", " ");
        sanitized = System.Text.RegularExpressions.Regex.Replace(sanitized, @"\s+", " ").Trim();
        
        return sanitized;
    }
    
    private static string SanitizeTitle(string title)
    {
        title = System.Text.RegularExpressions.Regex.Replace(title, @"\[[^\]]*\]", "");
        title = System.Text.RegularExpressions.Regex.Replace(title, @"\([^\)]*\)", "");
        
        title = System.Text.RegularExpressions.Regex.Replace(title, @"[^\w\s]", "");
        title = System.Text.RegularExpressions.Regex.Replace(title, @"\p{So}+", "");

        var noiseWords = new[] { "flac", "mp3", "pmedia", "vinyl", "cd", "album" };
        foreach (var noise in noiseWords)
        {
            title = System.Text.RegularExpressions.Regex.Replace(
                title, $@"\b{noise}\b", "", System.Text.RegularExpressions.RegexOptions.IgnoreCase
            );
        }

        title = System.Text.RegularExpressions.Regex.Replace(title, @"\s+", " ").Trim();

        return title;
    }

    private async Task<HtmlDocument> GetSearchPage(string query)
    {
        string encodedQuery = Uri.EscapeDataString(query);
        string url = $"{_baseUrl}{encodedQuery}{_urlParameters}";

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
    /*private async Task<HtmlDocument> GetSearchPage(string query)
    {
        using var stream = File.OpenRead("temp.txt");
        
        var searchPage = new HtmlDocument();
        searchPage.Load(stream);

        return searchPage;
    }*/

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
            string normalized = SanitizeTitle(candidate).ToLower();
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
        foreach (var idk in matches)
        {
            Console.WriteLine($"{idk.candidate}: {idk.score}");
        }

        if(matches.Count == 0)
            return null;
        
        var (match, score) = matches.First();
        if(score > 60)
            return torrentList.Where(torrent => torrent.Title == match).FirstOrDefault();

        return null;
    }

    private async Task LogDownload()
    {
        
    }
}