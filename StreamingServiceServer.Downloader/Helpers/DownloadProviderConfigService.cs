using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.Configuration;
using StreamingServiceServer.Data;
using StreamingServiceServer.Data.Models;

namespace StreamingServiceDownloader.Helpers;

/// <summary>
/// Service that reads provider configs from DB (source of truth) and 
/// seeds from appsettings.json on first boot.
/// </summary>
public class DownloadProviderConfigService
{
    private readonly StreamingDbContext _dbContext;
    private readonly IConfiguration _configuration;
    private readonly TorrentProvider _torrentProvider;
    private readonly SoulseekProvider _soulseekProvider;

    public DownloadProviderConfigService(
        StreamingDbContext dbContext,
        IConfiguration configuration,
        TorrentProvider torrentProvider,
        SoulseekProvider soulseekProvider)
    {
        _dbContext = dbContext;
        _configuration = configuration;
        _torrentProvider = torrentProvider;
        _soulseekProvider = soulseekProvider;
    }

    /// <summary>
    /// On first boot, if no provider configs exist in DB, seed from appsettings.json.
    /// </summary>
    public async Task SeedFromAppSettingsIfEmptyAsync()
    {
        if (await _dbContext.DownloadProviderConfigs.AnyAsync())
            return;

        // Seed Torrent provider from existing appsettings
        var torrentConfig = new DownloadProviderConfig
        {
            Id = Guid.NewGuid(),
            ProviderName = "Torrent",
            PriorityOrder = 1,
            IsEnabled = true,
            ApiUrl = _configuration["Music:DownloadSource:Torrent:Url"] ?? "http://localhost:8080",
            Username = _configuration["Music:DownloadSource:Torrent:User"] ?? "admin",
            Password = _configuration["Music:DownloadSource:Torrent:Password"] ?? "adminadmin",
            ApiKey = "",
            FormatPreferences = "FLAC,WAV,MP3,ANY",
            ExtraConfig = System.Text.Json.JsonSerializer.Serialize(new
            {
                BaseUrl = _configuration["Music:DownloadSource:BaseUrl"] ?? "",
                Parameters = _configuration["Music:DownloadSource:Parameters"] ?? "",
                RetryParameters = _configuration["Music:DownloadSource:RetryParameters"] ?? "",
                MaxDownloads = _configuration["Music:DownloadSource:Torrent:MaxDownloads"] ?? "200"
            }),
            LastUpdated = DateTime.UtcNow
        };

        // Seed Soulseek provider with defaults (disabled until configured)
        var soulseekConfig = new DownloadProviderConfig
        {
            Id = Guid.NewGuid(),
            ProviderName = "Soulseek",
            PriorityOrder = 2,
            IsEnabled = false,
            ApiUrl = "http://localhost:5030",
            Username = "",
            Password = "",
            ApiKey = "",
            FormatPreferences = "FLAC,WAV,MP3,ANY",
            ExtraConfig = "{}",
            LastUpdated = DateTime.UtcNow
        };

        _dbContext.DownloadProviderConfigs.Add(torrentConfig);
        _dbContext.DownloadProviderConfigs.Add(soulseekConfig);
        await _dbContext.SaveChangesAsync();

        Console.WriteLine("[ProviderConfig] Seeded default provider configs from appsettings.json.");
    }

    /// <summary>
    /// Get active providers ordered by priority. Applies DB config to each provider instance.
    /// </summary>
    public async Task<List<(IDownloadProvider provider, DownloadProviderConfig config)>> GetActiveProvidersOrderedAsync()
    {
        await SeedFromAppSettingsIfEmptyAsync();

        var configs = await _dbContext.DownloadProviderConfigs
            .Where(c => c.IsEnabled)
            .OrderBy(c => c.PriorityOrder)
            .ToListAsync();

        var result = new List<(IDownloadProvider, DownloadProviderConfig)>();

        foreach (var config in configs)
        {
            IDownloadProvider? provider = config.ProviderName switch
            {
                "Torrent" => _torrentProvider,
                "Soulseek" => _soulseekProvider,
                _ => null
            };

            if (provider == null) continue;

            // Apply DB credentials to provider
            if (provider is TorrentProvider tp) tp.ApplyConfig(config);
            if (provider is SoulseekProvider sp) sp.ApplyConfig(config);

            result.Add((provider, config));
        }

        return result;
    }

    /// <summary>
    /// Get all provider configs (for dashboard display).
    /// </summary>
    public async Task<List<DownloadProviderConfig>> GetAllConfigsAsync()
    {
        await SeedFromAppSettingsIfEmptyAsync();
        return await _dbContext.DownloadProviderConfigs
            .OrderBy(c => c.PriorityOrder)
            .ToListAsync();
    }

    /// <summary>
    /// Update a provider config from dashboard input.
    /// </summary>
    public async Task UpdateConfigAsync(DownloadProviderConfig updatedConfig)
    {
        var existing = await _dbContext.DownloadProviderConfigs
            .FirstOrDefaultAsync(c => c.Id == updatedConfig.Id);

        if (existing == null) return;

        existing.PriorityOrder = updatedConfig.PriorityOrder;
        existing.IsEnabled = updatedConfig.IsEnabled;
        existing.ApiUrl = updatedConfig.ApiUrl;
        existing.Username = updatedConfig.Username;
        existing.Password = updatedConfig.Password;
        existing.ApiKey = updatedConfig.ApiKey;
        existing.FormatPreferences = updatedConfig.FormatPreferences;
        existing.ExtraConfig = updatedConfig.ExtraConfig;
        existing.LastUpdated = DateTime.UtcNow;

        await _dbContext.SaveChangesAsync();
    }
}
