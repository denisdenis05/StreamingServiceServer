using System.Net.Http.Headers;
using System.Text;
using System.Text.Json;
using System.Text.Json.Serialization;

namespace StreamingServiceDownloader.Helpers;

/// <summary>
/// Low-level REST API client for the Slskd daemon (Soulseek).
/// Communicates via the Slskd v0 API endpoints.
/// </summary>
public class SlskdHelper
{
    private readonly HttpClient _httpClient;
    private string _baseUrl = "http://localhost:5030";
    private string _apiKey = "";
    private string _username = "";
    private string _password = "";
    private string? _sessionToken;

    public SlskdHelper(IHttpClientFactory httpClientFactory)
    {
        _httpClient = httpClientFactory.CreateClient();
        _httpClient.Timeout = TimeSpan.FromSeconds(30);
    }

    public void Configure(string baseUrl, string apiKey, string username = "", string password = "")
    {
        _baseUrl = baseUrl.TrimEnd('/');
        _apiKey = apiKey;
        _username = username;
        _password = password;
        _sessionToken = null;
    }

    public async Task<bool> LoginAsync(CancellationToken cancellationToken = default)
    {
        if (string.IsNullOrWhiteSpace(_username) || string.IsNullOrWhiteSpace(_password))
            return false;

        try
        {
            var payload = new { username = _username, password = _password };
            var json = JsonSerializer.Serialize(payload);
            var request = new HttpRequestMessage(HttpMethod.Post, $"{_baseUrl}/api/v0/session")
            {
                Content = new StringContent(json, Encoding.UTF8, "application/json")
            };

            var response = await _httpClient.SendAsync(request, cancellationToken);
            if (response.IsSuccessStatusCode)
            {
                var body = await response.Content.ReadAsStringAsync(cancellationToken);
                using var doc = JsonDocument.Parse(body);
                if (doc.RootElement.TryGetProperty("token", out var tokenProp))
                {
                    _sessionToken = tokenProp.GetString();
                    return true;
                }
            }
        }
        catch { }

        return false;
    }

    private void AddAuthHeaders(HttpRequestMessage request)
    {
        if (!string.IsNullOrWhiteSpace(_apiKey))
        {
            request.Headers.Add("X-API-Key", _apiKey);
        }
        else if (!string.IsNullOrWhiteSpace(_sessionToken))
        {
            request.Headers.Authorization = new System.Net.Http.Headers.AuthenticationHeaderValue("Bearer", _sessionToken);
        }
    }

    private async Task EnsureAuthenticatedAsync(CancellationToken cancellationToken)
    {
        if (!string.IsNullOrWhiteSpace(_apiKey))
            return;
        if (string.IsNullOrWhiteSpace(_sessionToken))
        {
            await LoginAsync(cancellationToken);
        }
    }

    private async Task<HttpResponseMessage> SendAuthenticatedAsync(HttpRequestMessage request, CancellationToken cancellationToken)
    {
        await EnsureAuthenticatedAsync(cancellationToken);
        AddAuthHeaders(request);
        var response = await _httpClient.SendAsync(request, cancellationToken);
        if (response.StatusCode == System.Net.HttpStatusCode.Unauthorized && !string.IsNullOrWhiteSpace(_username))
        {
            // Session token expired or invalid, re-login and retry once
            _sessionToken = null;
            if (await LoginAsync(cancellationToken))
            {
                var retryRequest = await CloneHttpRequestAsync(request);
                AddAuthHeaders(retryRequest);
                return await _httpClient.SendAsync(retryRequest, cancellationToken);
            }
        }
        return response;
    }

    private static async Task<HttpRequestMessage> CloneHttpRequestAsync(HttpRequestMessage request)
    {
        var clone = new HttpRequestMessage(request.Method, request.RequestUri);
        if (request.Content != null)
        {
            var ms = new MemoryStream();
            await request.Content.CopyToAsync(ms);
            ms.Position = 0;
            clone.Content = new StreamContent(ms);
            if (request.Content.Headers.ContentType != null)
                clone.Content.Headers.ContentType = request.Content.Headers.ContentType;
        }
        return clone;
    }

    /// <summary>
    /// Start a search on the Soulseek network.
    /// Returns the search ID (GUID) that can be polled for results.
    /// </summary>
    public async Task<Guid?> SearchAsync(string query, CancellationToken cancellationToken = default)
    {
        var searchId = Guid.NewGuid();
        var payload = new { id = searchId.ToString(), searchText = query };
        var json = JsonSerializer.Serialize(payload);

        var request = new HttpRequestMessage(HttpMethod.Post, $"{_baseUrl}/api/v0/searches")
        {
            Content = new StringContent(json, Encoding.UTF8, "application/json")
        };

        var response = await SendAuthenticatedAsync(request, cancellationToken);
        response.EnsureSuccessStatusCode();

        return searchId;
    }

    /// <summary>
    /// Get the results of a previously initiated search.
    /// Fetches both search status and search responses.
    /// </summary>
    public async Task<SlskdSearchResult?> GetSearchResultsAsync(Guid searchId, CancellationToken cancellationToken = default)
    {
        var request = new HttpRequestMessage(HttpMethod.Get, $"{_baseUrl}/api/v0/searches/{searchId}");

        var response = await SendAuthenticatedAsync(request, cancellationToken);
        if (!response.IsSuccessStatusCode) return null;

        var responseJson = await response.Content.ReadAsStringAsync(cancellationToken);
        var result = JsonSerializer.Deserialize<SlskdSearchResult>(responseJson, new JsonSerializerOptions
        {
            PropertyNameCaseInsensitive = true
        });

        if (result != null && (result.Responses == null || result.Responses.Count == 0))
        {
            try
            {
                var respRequest = new HttpRequestMessage(HttpMethod.Get, $"{_baseUrl}/api/v0/searches/{searchId}/responses");
                var respResponse = await SendAuthenticatedAsync(respRequest, cancellationToken);
                if (respResponse.IsSuccessStatusCode)
                {
                    var respJson = await respResponse.Content.ReadAsStringAsync(cancellationToken);
                    var responsesList = JsonSerializer.Deserialize<List<SlskdSearchResponse>>(respJson, new JsonSerializerOptions
                    {
                        PropertyNameCaseInsensitive = true
                    });
                    if (responsesList != null)
                    {
                        result.Responses = responsesList;
                    }
                }
            }
            catch { }
        }

        return result;
    }

    /// <summary>
    /// Wait for search results to complete (poll with retries).
    /// </summary>
    public async Task<SlskdSearchResult?> WaitForSearchResultsAsync(
        Guid searchId, int maxWaitSeconds = 20, CancellationToken cancellationToken = default)
    {
        var deadline = DateTime.UtcNow.AddSeconds(maxWaitSeconds);

        while (DateTime.UtcNow < deadline && !cancellationToken.IsCancellationRequested)
        {
            await Task.Delay(2500, cancellationToken);

            var result = await GetSearchResultsAsync(searchId, cancellationToken);
            if (result == null) continue;

            // The search state can be "InProgress", "Completed", etc.
            if (result.State?.Contains("Completed", StringComparison.OrdinalIgnoreCase) == true
                || result.Responses?.Count > 0)
            {
                return result;
            }
        }

        // Return whatever we have even if incomplete
        return await GetSearchResultsAsync(searchId, cancellationToken);
    }

    /// <summary>
    /// Enqueue a file download from a specific user.
    /// Includes filename and size in payload to prevent TransferSizeMismatchException.
    /// </summary>
    public async Task<bool> EnqueueDownloadAsync(
        string username, SlskdFileDownloadRequest fileRequest, CancellationToken cancellationToken = default)
    {
        var encodedUsername = Uri.EscapeDataString(username);
        var payload = new[] { new { filename = fileRequest.Filename, size = fileRequest.Size } };
        var json = JsonSerializer.Serialize(payload);

        var request = new HttpRequestMessage(HttpMethod.Post,
            $"{_baseUrl}/api/v0/transfers/downloads/{encodedUsername}")
        {
            Content = new StringContent(json, Encoding.UTF8, "application/json")
        };

        var response = await SendAuthenticatedAsync(request, cancellationToken);
        return response.IsSuccessStatusCode;
    }

    /// <summary>
    /// Enqueue an entire directory of files from a specific user with sizes.
    /// </summary>
    public async Task<int> EnqueueDirectoryDownloadAsync(
        string username, List<SlskdFileDownloadRequest> fileRequests, CancellationToken cancellationToken = default)
    {
        int enqueued = 0;
        foreach (var fileReq in fileRequests)
        {
            try
            {
                if (await EnqueueDownloadAsync(username, fileReq, cancellationToken))
                    enqueued++;
            }
            catch { }
        }
        return enqueued;
    }

    /// <summary>
    /// Get all current downloads and their statuses.
    /// </summary>
    public async Task<List<SlskdTransferUser>?> GetDownloadsAsync(CancellationToken cancellationToken = default)
    {
        var request = new HttpRequestMessage(HttpMethod.Get, $"{_baseUrl}/api/v0/transfers/downloads");

        var response = await SendAuthenticatedAsync(request, cancellationToken);
        if (!response.IsSuccessStatusCode) return null;

        var json = await response.Content.ReadAsStringAsync(cancellationToken);
        return JsonSerializer.Deserialize<List<SlskdTransferUser>>(json, new JsonSerializerOptions
        {
            PropertyNameCaseInsensitive = true
        });
    }

    /// <summary>
    /// Remove completed/errored transfer records from Slskd for a specific user.
    /// </summary>
    public async Task RemoveCompletedTransfersAsync(string username, CancellationToken cancellationToken = default)
    {
        try
        {
            var downloads = await GetDownloadsAsync(cancellationToken);
            if (downloads == null) return;

            var userTransfers = downloads.FirstOrDefault(d => 
                d.Username?.Equals(username, StringComparison.OrdinalIgnoreCase) == true);

            if (userTransfers?.Directories == null) return;

            foreach (var dir in userTransfers.Directories)
            {
                if (dir.Files == null) continue;
                foreach (var file in dir.Files)
                {
                    if (!string.IsNullOrWhiteSpace(file.Id))
                    {
                        try
                        {
                            var encodedUser = Uri.EscapeDataString(username);
                            var req = new HttpRequestMessage(HttpMethod.Delete, $"{_baseUrl}/api/v0/transfers/downloads/{encodedUser}/{file.Id}");
                            await SendAuthenticatedAsync(req, cancellationToken);
                        }
                        catch { }
                    }
                }
            }
        }
        catch { }
    }

    /// <summary>
    /// Test connection to the Slskd instance with detailed status message.
    /// </summary>
    public async Task<(bool success, string message)> TestConnectionDetailedAsync(CancellationToken cancellationToken = default)
    {
        try
        {
            // 1. Try API key auth if provided
            if (!string.IsNullOrWhiteSpace(_apiKey))
            {
                var request = new HttpRequestMessage(HttpMethod.Get, $"{_baseUrl}/api/v0/application");
                AddAuthHeaders(request);

                var response = await _httpClient.SendAsync(request, cancellationToken);
                if (response.IsSuccessStatusCode)
                {
                    return (true, "Connected to Slskd successfully via API Key.");
                }

                if (response.StatusCode == System.Net.HttpStatusCode.Unauthorized)
                {
                    // Check if username/password can be tried
                    if (!string.IsNullOrWhiteSpace(_username) && !string.IsNullOrWhiteSpace(_password))
                    {
                        if (await LoginAsync(cancellationToken))
                            return (true, "Connected to Slskd successfully via Web Credentials.");
                    }
                    return (false, "Slskd reached at " + _baseUrl + ", but API Key was rejected (401). Check slskd.yml configuration & CIDR rules.");
                }
            }

            // 2. Try Username/Password auth if provided
            if (!string.IsNullOrWhiteSpace(_username) && !string.IsNullOrWhiteSpace(_password))
            {
                if (await LoginAsync(cancellationToken))
                {
                    return (true, "Connected to Slskd successfully via Web Credentials.");
                }
                return (false, "Slskd reached at " + _baseUrl + ", but Web Credentials were rejected.");
            }

            // 3. Test unauthenticated ping to see if server is up
            var pingReq = new HttpRequestMessage(HttpMethod.Get, $"{_baseUrl}/");
            var pingRes = await _httpClient.SendAsync(pingReq, cancellationToken);
            if (pingRes.IsSuccessStatusCode)
            {
                return (false, "Slskd server reached at " + _baseUrl + ", but authentication credentials (API Key or Web Login) are required.");
            }

            return (false, $"Slskd server returned HTTP {(int)pingRes.StatusCode}.");
        }
        catch (HttpRequestException ex)
        {
            return (false, $"Cannot reach Slskd at {_baseUrl}: {ex.Message}");
        }
        catch (Exception ex)
        {
            return (false, $"Connection test failed: {ex.Message}");
        }
    }

    public async Task<bool> TestConnectionAsync(CancellationToken cancellationToken = default)
    {
        var (success, _) = await TestConnectionDetailedAsync(cancellationToken);
        return success;
    }

    /// <summary>
    /// Delete/cancel a search to clean up resources.
    /// </summary>
    public async Task DeleteSearchAsync(Guid searchId, CancellationToken cancellationToken = default)
    {
        try
        {
            var request = new HttpRequestMessage(HttpMethod.Delete, $"{_baseUrl}/api/v0/searches/{searchId}");
            AddAuthHeaders(request);
            await _httpClient.SendAsync(request, cancellationToken);
        }
        catch { }
    }
}

// ─── Slskd API response models ───

public class SlskdSearchResult
{
    public string? Id { get; set; }
    public string? State { get; set; }
    public string? SearchText { get; set; }
    public int FileCount { get; set; }
    public List<SlskdSearchResponse>? Responses { get; set; }
}

public class SlskdSearchResponse
{
    public string? Username { get; set; }
    public int FreeUploadSlots { get; set; }
    public long UploadSpeed { get; set; }
    public int FileCount { get; set; }
    public List<SlskdFile>? Files { get; set; }
}

public class SlskdFile
{
    public string? Filename { get; set; }
    public long Size { get; set; }
    public int? BitRate { get; set; }
    public int? SampleRate { get; set; }
    public int? BitDepth { get; set; }
    public string? Extension { get; set; }
    public bool IsLocked { get; set; }
}

public class SlskdFileDownloadRequest
{
    public string Filename { get; set; } = string.Empty;
    public long Size { get; set; }
}

public class SlskdTransferUser
{
    public string? Username { get; set; }
    public List<SlskdTransferFile>? Directories { get; set; }
}

public class SlskdTransferFile
{
    public string? Directory { get; set; }
    public List<SlskdTransferItem>? Files { get; set; }
}

public class SlskdTransferItem
{
    public string? Id { get; set; }
    public string? Filename { get; set; }
    public string? State { get; set; }
    public long Size { get; set; }
    public long BytesTransferred { get; set; }
    public double? PercentComplete { get; set; }
}
