using System.Net.Http.Headers;
using System.Text;
using System.Text.Json;
using StreamingServiceDownloader.Models.Slskd;

namespace StreamingServiceDownloader.Helpers;

public class SlskdHelper
{
    private readonly HttpClient _httpClient;
    private readonly TimeProvider _timeProvider;
    private string _baseUrl = "http://localhost:5030";
    private string _apiKey = "";
    private string _username = "";
    private string _password = "";
    private string? _sessionToken;

    public SlskdHelper(IHttpClientFactory httpClientFactory, TimeProvider? timeProvider = null)
    {
        _httpClient = httpClientFactory.CreateClient();
        _timeProvider = timeProvider ?? TimeProvider.System;
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


    public async Task<SlskdSearchResult?> GetSearchResultsAsync(Guid searchId, CancellationToken cancellationToken = default)
    {
        using var request = new HttpRequestMessage(HttpMethod.Get, $"{_baseUrl}/api/v0/searches/{searchId}");

        using var response = await SendAuthenticatedAsync(request, cancellationToken);
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
                using var respRequest = new HttpRequestMessage(HttpMethod.Get, $"{_baseUrl}/api/v0/searches/{searchId}/responses");
                using var respResponse = await SendAuthenticatedAsync(respRequest, cancellationToken);
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


    public async Task<SlskdSearchResult?> WaitForSearchResultsAsync(
        Guid searchId, int maxWaitSeconds = 90, CancellationToken cancellationToken = default)
    {
        var startTime = DateTime.UtcNow;
        var deadline = startTime.AddSeconds(maxWaitSeconds);

        SlskdSearchResult? lastResult = null;

        while (DateTime.UtcNow < deadline && !cancellationToken.IsCancellationRequested)
        {
            await Task.Delay(2000, cancellationToken);

            var result = await GetSearchStatusAsync(searchId, cancellationToken);
            if (result == null) continue;

            lastResult = result;
            var elapsedSeconds = (DateTime.UtcNow - startTime).TotalSeconds;
            var isCompleted = result.State != null && (
                result.State.Contains("Completed", StringComparison.OrdinalIgnoreCase) ||
                result.State.Contains("Cancelled", StringComparison.OrdinalIgnoreCase));

            Console.WriteLine($"[Soulseek][Search] id={searchId} elapsed={elapsedSeconds:F0}s state='{result.State}' responseCount={result.ResponseCount} fileCount={result.FileCount}");

            if (isCompleted)
            {
                Console.WriteLine($"[Soulseek][Search] Search completed with {result.ResponseCount} responses. Fetching results...");
                return await FetchCompletedSearchResultsAsync(searchId, result, cancellationToken);
            }

            if (result.ResponseCount >= 250 && elapsedSeconds >= 10)
            {
                Console.WriteLine($"[Soulseek][Search] Response limit reached early ({result.ResponseCount}). Fetching results...");
                return await FetchCompletedSearchResultsAsync(searchId, result, cancellationToken);
            }
        }

        Console.WriteLine($"[Soulseek][Search] Timeout after {maxWaitSeconds}s. Last state: '{lastResult?.State}' responseCount={lastResult?.ResponseCount}");
        if (lastResult != null)
            return await FetchCompletedSearchResultsAsync(searchId, lastResult, cancellationToken);
        return await GetSearchResultsAsync(searchId, cancellationToken);
    }

    private async Task<SlskdSearchResult?> FetchCompletedSearchResultsAsync(
        Guid searchId, SlskdSearchResult statusResult, CancellationToken cancellationToken)
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
                if (responsesList != null && responsesList.Count > 0)
                {
                    statusResult.Responses = responsesList;
                    Console.WriteLine($"[Soulseek][Search] Loaded {responsesList.Count} responses from /responses endpoint.");
                    return statusResult;
                }
            }
        }
        catch { }

        return await GetSearchResultsAsync(searchId, cancellationToken);
    }

    private async Task<SlskdSearchResult?> GetSearchStatusAsync(Guid searchId, CancellationToken cancellationToken)
    {
        var request = new HttpRequestMessage(HttpMethod.Get, $"{_baseUrl}/api/v0/searches/{searchId}");
        var response = await SendAuthenticatedAsync(request, cancellationToken);
        if (!response.IsSuccessStatusCode) return null;
        var json = await response.Content.ReadAsStringAsync(cancellationToken);
        return JsonSerializer.Deserialize<SlskdSearchResult>(json, new JsonSerializerOptions { PropertyNameCaseInsensitive = true });
    }


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

    public async Task EnqueueSelectedFilesAsync(string username, IReadOnlyList<SlskdFileDownloadRequest> files,
        CancellationToken cancellationToken)
    {
        using var request = new HttpRequestMessage(HttpMethod.Post,
            $"{_baseUrl}/api/v0/transfers/downloads/{Uri.EscapeDataString(username)}")
        {
            Content = new StringContent(JsonSerializer.Serialize(files.Select(file =>
                new { filename = file.Filename, size = file.Size })), Encoding.UTF8, "application/json")
        };
        using var response = await SendAuthenticatedAsync(request, cancellationToken);
        if (!response.IsSuccessStatusCode)
            throw new HttpRequestException($"slskd rejected the download request (HTTP {(int)response.StatusCode}).",
                null, response.StatusCode);
    }

    public async Task RemoveSelectedTransfersAsync(SlskdSourceIdentity source, CancellationToken cancellationToken,
        IReadOnlySet<string>? retainedFiles = null)
    {
        var downloads = await GetDownloadsAsync(cancellationToken)
            ?? throw new HttpRequestException("Could not read slskd transfers; no transfers were removed.");
        var user = downloads.FirstOrDefault(candidate => candidate.Username == source.Username);
        foreach (var directory in user?.Directories ?? [])
        foreach (var file in directory.Files ?? [])
        {
            var filename = SlskdSourceIdentity.NormalizePath(file.Filename ?? string.Empty);
            var scopedFilename = filename.Contains('/') ? filename
                : SlskdSourceIdentity.NormalizePath(source.DirectoryPath) + "/" + filename;
            if (!source.Matches(directory, file) || string.IsNullOrWhiteSpace(file.Id)
                || retainedFiles?.Contains(filename) == true || retainedFiles?.Contains(scopedFilename) == true) continue;
            using var request = new HttpRequestMessage(HttpMethod.Delete,
                $"{_baseUrl}/api/v0/transfers/downloads/{Uri.EscapeDataString(source.Username)}/{Uri.EscapeDataString(file.Id)}");
            using var response = await SendAuthenticatedAsync(request, cancellationToken);
            if (!response.IsSuccessStatusCode && response.StatusCode != System.Net.HttpStatusCode.NotFound)
                throw new HttpRequestException($"slskd could not remove a selected transfer (HTTP {(int)response.StatusCode}).",
                    null, response.StatusCode);
        }
    }

    public async Task EnsureSelectedFilesEnqueuedAsync(SlskdSourceIdentity source, CancellationToken cancellationToken)
    {
        var before = await GetDownloadsAsync(cancellationToken)
            ?? throw new HttpRequestException("Could not read slskd transfers before changing source.");
        var missing = source.MissingFiles(before);
        if (missing.Count == 0) return;
        await EnqueueSelectedFilesAsync(source.Username, missing, cancellationToken);
        var after = await GetDownloadsAsync(cancellationToken)
            ?? throw new HttpRequestException("slskd accepted the request, but its transfers could not be verified. The previous source is retained.");
        var remaining = source.MissingFiles(after);
        if (remaining.Count > 0)
            throw new HttpRequestException($"slskd has not queued {remaining.Count} of {source.Files.Count} selected files. The previous source is retained; retry the selection.");
    }

    public async Task StartAlbumDownloadAsync(SlskdSourceIdentity source, CancellationToken cancellationToken)
    {
        try
        {
            await EnsureSelectedFilesEnqueuedAsync(source, cancellationToken);
        }
        catch (Exception failure) when (failure is HttpRequestException or TaskCanceledException
            && !cancellationToken.IsCancellationRequested)
        {
            var downloads = await GetDownloadsAsync(cancellationToken);
            if (downloads is null || !downloads.Where(user => user.Username == source.Username)
                .SelectMany(user => user.Directories ?? [])
                .Any(directory => (directory.Files ?? []).Any(file => source.Matches(directory, file)))) throw;
            Console.WriteLine($"[Soulseek] Album transfers retained for '{source.Username}'; missing or failed tracks will retry individually: {failure.Message}");
        }
    }

    public async Task<bool> TryRetryMissingFileAsync(SlskdSourceIdentity source,
        SoulseekFileRetrySchedule schedule, CancellationToken cancellationToken)
    {
        try { return await RetryMissingFileAsync(source, schedule, cancellationToken); }
        catch (Exception failure) when (failure is HttpRequestException or IOException or OperationCanceledException
            && !cancellationToken.IsCancellationRequested)
        {
            Console.WriteLine($"[Soulseek] Track retry deferred; album stall detection remains active: {failure.Message}");
            return false;
        }
    }

    public async Task<bool> RetryMissingFileAsync(SlskdSourceIdentity source,
        SoulseekFileRetrySchedule schedule, CancellationToken cancellationToken)
    {
        var downloads = await GetDownloadsAsync(cancellationToken)
            ?? throw new HttpRequestException("Could not read slskd transfers before retrying a track.");
        if (source.Files.Count == 0)
        {
            var user = downloads.FirstOrDefault(candidate => candidate.Username == source.Username);
            var files = (user?.Directories ?? []).SelectMany(directory => (directory.Files ?? [])
                .Where(file => source.Matches(directory, file) && SlskdSourceIdentity.IsFailed(file)
                    && file.Size > 0)
                .Select(file => new SlskdFileDownloadRequest
                {
                    Filename = SlskdSourceIdentity.NormalizePath(file.Filename!).Contains('/')
                        ? file.Filename! : source.DirectoryPath.TrimEnd('/', '\\') + "/" + file.Filename,
                    Size = file.Size
                })).ToArray();
            source = source with { Files = files };
        }
        var missing = source.MissingFiles(downloads);
        if (missing.Count == 0) return false;
        var selected = schedule.TakeNext(missing, _timeProvider.GetUtcNow());
        if (selected is not null)
        {
            Console.WriteLine($"[Soulseek] Retrying track '{selected.Filename}' from '{source.Username}'.");
            await EnqueueSelectedFilesAsync(source.Username, [selected], cancellationToken);
        }
        return true;
    }


    public async Task<List<SlskdTransferUser>?> GetDownloadsAsync(CancellationToken cancellationToken = default)
    {
        using var request = new HttpRequestMessage(HttpMethod.Get, $"{_baseUrl}/api/v0/transfers/downloads");

        using var response = await SendAuthenticatedAsync(request, cancellationToken);
        if (!response.IsSuccessStatusCode) return null;

        var json = await response.Content.ReadAsStringAsync(cancellationToken);
        return JsonSerializer.Deserialize<List<SlskdTransferUser>>(json, new JsonSerializerOptions
        {
            PropertyNameCaseInsensitive = true
        });
    }


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


    public async Task<(bool success, string message)> TestConnectionDetailedAsync(CancellationToken cancellationToken = default)
    {
        try
        {

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

                    if (!string.IsNullOrWhiteSpace(_username) && !string.IsNullOrWhiteSpace(_password))
                    {
                        if (await LoginAsync(cancellationToken))
                            return (true, "Connected to Slskd successfully via Web Credentials.");
                    }
                    return (false, "Slskd reached at " + _baseUrl + ", but API Key was rejected (401). Check slskd.yml configuration & CIDR rules.");
                }
            }


            if (!string.IsNullOrWhiteSpace(_username) && !string.IsNullOrWhiteSpace(_password))
            {
                if (await LoginAsync(cancellationToken))
                {
                    return (true, "Connected to Slskd successfully via Web Credentials.");
                }
                return (false, "Slskd reached at " + _baseUrl + ", but Web Credentials were rejected.");
            }


            var pingReq = new HttpRequestMessage(HttpMethod.Get, $"{_baseUrl}/");
            var pingRes = await _httpClient.SendAsync(pingReq, cancellationToken);
            if (pingRes.IsSuccessStatusCode)
            {
                return (false, "Slskd server reached at " + _baseUrl + ", but authentication credentials (API Key or Web Login) are required.");
            }

            return (false, $"Slskd server returned HTTP {(int)pingRes.StatusCode}.");
        }
        catch (HttpRequestException exception)
        {
            return (false, $"Cannot reach Slskd at {_baseUrl}: {exception.Message}");
        }
        catch (Exception exception)
        {
            return (false, $"Connection test failed: {exception.Message}");
        }
    }

    public async Task<bool> TestConnectionAsync(CancellationToken cancellationToken = default)
    {
        var (success, _) = await TestConnectionDetailedAsync(cancellationToken);
        return success;
    }


    public async Task DeleteSearchAsync(Guid searchId, CancellationToken cancellationToken = default)
    {
        try
        {
            var request = new HttpRequestMessage(HttpMethod.Delete, $"{_baseUrl}/api/v0/searches/{searchId}");
            AddAuthHeaders(request);
        }
        catch { }
    }
}
