namespace StreamingServiceDownloader.Helpers;

public static class StringSanitizers
{
    public static string NormalizeSpecialCharacters(string text)
    {
        if (string.IsNullOrWhiteSpace(text)) return string.Empty;

        text = text.Replace("Ø", "O").Replace("ø", "o")
                   .Replace("Æ", "AE").Replace("æ", "ae")
                   .Replace("Œ", "OE").Replace("œ", "oe")
                   .Replace("Ł", "L").Replace("ł", "l")
                   .Replace("Đ", "D").Replace("đ", "d")
                   .Replace("ß", "ss")
                   .Replace("Þ", "TH").Replace("þ", "th")
                   .Replace("Ð", "D").Replace("ð", "d");

        var normalizedString = text.Normalize(System.Text.NormalizationForm.FormD);
        var stringBuilder = new System.Text.StringBuilder(capacity: normalizedString.Length);

        foreach (var c in normalizedString)
        {
            var unicodeCategory = System.Globalization.CharUnicodeInfo.GetUnicodeCategory(c);
            if (unicodeCategory != System.Globalization.UnicodeCategory.NonSpacingMark)
            {
                stringBuilder.Append(c);
            }
        }

        return stringBuilder.ToString().Normalize(System.Text.NormalizationForm.FormC);
    }

    public static string SanitizeTitle(string? title)
    {
        if (string.IsNullOrWhiteSpace(title)) return string.Empty;

        title = NormalizeSpecialCharacters(title);

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
