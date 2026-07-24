namespace StreamingServiceDownloader.Models.Slskd;

public class SlskdSearchResult
{
    public string? Id { get; set; }
    public string? State { get; set; }
    public string? SearchText { get; set; }
    public int FileCount { get; set; }
    public List<SlskdSearchResponse>? Responses { get; set; }
}
