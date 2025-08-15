using Microsoft.Extensions.Configuration;
using StreamingServiceDownloader.Models;
using StreamingServiceServer.Business.Helpers;

namespace StreamingServiceDownloader.Helpers;

using System;
using System.Collections.Generic;
using System.Linq;
using System.Net;
using System.Net.Http;
using System.Text.Json;
using System.Threading.Tasks;

public class QBittorrentHelper: ITorrentHelper
{
    private readonly HttpClient _httpClient;
    private readonly IConfiguration _configuration;
    private readonly string _baseUrl;
    private readonly string _username;
    private readonly string _password;
    private bool _isLoggedIn;
    

    public QBittorrentHelper(IConfiguration configuration, HttpClient httpClient)
    {
        _httpClient = httpClient;
        _configuration = configuration;
        
        _baseUrl = _configuration["Music:DownloadSource:Torrent:Url"].TrimEnd('/');
        _username = _configuration["Music:DownloadSource:Torrent:User"];
        _password = _configuration["Music:DownloadSource:Torrent:Password"];;
    }

    public async Task LoginAsync()
    {
        Console.WriteLine("Logging in...");
        
        var loginData = new FormUrlEncodedContent(new[]
        {
            new KeyValuePair<string, string>("username", _username),
            new KeyValuePair<string, string>("password", _password)
        });

        var response = await _httpClient.PostAsync($"{_baseUrl}/api/v2/auth/login", loginData);
        response.EnsureSuccessStatusCode();

        var body = await response.Content.ReadAsStringAsync();
        if (body.Contains("Ok", StringComparison.OrdinalIgnoreCase))
        {
            _isLoggedIn = true;
            Console.WriteLine("Logged in...");
        }
        else
        {
            throw new Exception("Failed to log in to qBittorrent.");
        }
    }

    public async Task AddTorrentAsync(string magnetLink, string? targetFolder = null)
    {
        if (!_isLoggedIn)
            await LoginAsync();

        var content = new MultipartFormDataContent
        {
            { new StringContent(magnetLink), "urls" }
        };

        if (!string.IsNullOrWhiteSpace(targetFolder))
        {
            content.Add(new StringContent(targetFolder), "savepath");
            content.Add(new StringContent("false"), "root_folder");
        }

        var response = await _httpClient.PostAsync($"{_baseUrl}/api/v2/torrents/add", content);
        response.EnsureSuccessStatusCode();
        
        Console.WriteLine("Added");
    }

    public async Task AddTorrentAsync(TorrentResult torrent)
    {
        await AddTorrentAsync(torrent.MagnetLink);
    }

    public async Task<TorrentInfo?> GetTorrentInfoAsync(string searchTerm)
    {
        if (!_isLoggedIn)
            await LoginAsync();

        var response = await _httpClient.GetAsync($"{_baseUrl}/api/v2/torrents/info");
        response.EnsureSuccessStatusCode();

        var json = await response.Content.ReadAsStringAsync();

        var torrents = JsonSerializer.Deserialize<List<TorrentInfo>>(json);

        if (torrents == null) return null;

        return torrents.FirstOrDefault(t =>
            t.Hash.Equals(searchTerm, StringComparison.OrdinalIgnoreCase) ||
            t.Name.Contains(searchTerm, StringComparison.OrdinalIgnoreCase));
    }
    
    public async Task RemoveTorrentAsync(string torrentName, bool deleteFiles = true)
    {
        if (!_isLoggedIn)
            await LoginAsync();

        var torrent = await GetTorrentInfoAsync(torrentName);
        if (torrent == null)
        {
            return;
        }

        var content = new FormUrlEncodedContent(new[]
        {
            new KeyValuePair<string, string>("hashes", torrent.Hash),
            new KeyValuePair<string, string>("deleteFiles", deleteFiles ? "true" : "false")
        });

        var response = await _httpClient.PostAsync($"{_baseUrl}/api/v2/torrents/delete", content);
        response.EnsureSuccessStatusCode();
    }
    
    public async Task<int> GetActiveDownloadCountAsync()
    {
        if (!_isLoggedIn)
            await LoginAsync();

        var response = await _httpClient.GetAsync($"{_baseUrl}/api/v2/torrents/info");
        response.EnsureSuccessStatusCode();

        var json = await response.Content.ReadAsStringAsync();
        var torrents = JsonSerializer.Deserialize<List<TorrentInfo>>(json);

        if (torrents == null) return 0;

        var downloadingStates = new[]
        {
            "downloading",
            "stalledDL",
            "queuedDL",
            "checkingDL",
            "metaDL"
        };

        return torrents.Count(t =>
            t.Progress < 1.0 &&
            downloadingStates.Contains(t.State, StringComparer.OrdinalIgnoreCase));
    }
}
