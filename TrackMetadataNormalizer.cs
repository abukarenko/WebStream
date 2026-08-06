using System.Text.RegularExpressions;

namespace WebStream;

public static partial class TrackMetadataNormalizer
{
    [GeneratedRegex(@"\s+(?:§\d{4,}|\$\d{6,})\s*$", RegexOptions.CultureInvariant)]
    private static partial Regex ServiceIdSuffixRegex();

    [GeneratedRegex(@"^(?<artist>.+?)\s*[-–—]\s*text\s*=\s*(?<quote>[""'])(?<title>.*?)\k<quote>(?:\s+[\w-]+\s*=\s*[""'][^""']*[""'])+\s*$",
        RegexOptions.IgnoreCase | RegexOptions.CultureInvariant)]
    private static partial Regex EmbeddedAttributeMetadataRegex();

    public static string NormalizeTitle(string? title)
    {
        var clean = Regex.Replace(title ?? string.Empty, @"\s*\|\|.*$", "", RegexOptions.Singleline);
        clean = ServiceIdSuffixRegex().Replace(clean, string.Empty);
        clean = Regex.Replace(clean, @"\s+", " ").Trim();

        var embeddedMetadata = EmbeddedAttributeMetadataRegex().Match(clean);
        if (embeddedMetadata.Success)
        {
            var artist = embeddedMetadata.Groups["artist"].Value.Trim();
            var trackTitle = embeddedMetadata.Groups["title"].Value.Trim();
            if (!string.IsNullOrWhiteSpace(artist) && !string.IsNullOrWhiteSpace(trackTitle))
                return $"{artist} - {trackTitle}";
        }

        return clean;
    }
}
