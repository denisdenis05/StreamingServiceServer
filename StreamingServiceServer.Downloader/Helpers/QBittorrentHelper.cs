using StreamingServiceDownloader.Models;

namespace StreamingServiceDownloader.Helpers;

using System;
using System.Collections.Generic;
using System.Linq;
using System.Net;
using System.Net.Http;
using System.Text.Json;
using System.Threading.Tasks;

public class QbittorrentClient: ITorrentHelper
{
    private readonly HttpClient _httpClient;
    private readonly string _baseUrl;
    private readonly string _username;
    private readonly string _password;
    private bool _isLoggedIn;

    public QbittorrentClient(string baseUrl, string username, string password)
    {
        _baseUrl = baseUrl.TrimEnd('/');
        _username = username;
        _password = password;

        var handler = new HttpClientHandler
        {
            CookieContainer = new CookieContainer(),
            UseCookies = true
        };

        _httpClient = new HttpClient(handler);
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

    public async Task AddTorrentAsync(string magnetLink)
    {
        if (!_isLoggedIn)
            await LoginAsync();

        var content = new MultipartFormDataContent
        {
            { new StringContent(magnetLink), "urls" }
        };

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
}
