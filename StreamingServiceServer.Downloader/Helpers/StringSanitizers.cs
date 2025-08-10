namespace StreamingServiceDownloader.Helpers;

public static class StringSanitizers
{
    public static string SanitizeTitle(string title)
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
}