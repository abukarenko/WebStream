using System.Net.Http;
using System.IO;
using System.Text.Json;
using System.Text.RegularExpressions;

namespace WebStream;

public sealed record MetadataSuggestion(string Title, string Artist, string Album, string Genre);

public static partial class MetadataLookupService
{
    private static readonly HttpClient Client = new()
    {
        Timeout = TimeSpan.FromSeconds(8)
    };

    public static async Task<MetadataSuggestion?> FindAsync(string title, string artist, string fileName)
    {
        var query = BuildQuery(title, artist, fileName);
        if (string.IsNullOrWhiteSpace(query)) return null;

        var url = $"https://itunes.apple.com/search?term={Uri.EscapeDataString(query)}&media=music&entity=song&limit=8";
        await using var stream = await Client.GetStreamAsync(url);
        using var document = await JsonDocument.ParseAsync(stream);
        if (!document.RootElement.TryGetProperty("results", out var results) || results.GetArrayLength() == 0)
            return null;

        MetadataSuggestion? best = null;
        var bestScore = int.MinValue;
        foreach (var result in results.EnumerateArray())
        {
            var suggestion = new MetadataSuggestion(
                GetString(result, "trackName"),
                GetString(result, "artistName"),
                GetString(result, "collectionName"),
                GetString(result, "primaryGenreName"));
            var score = Score(suggestion, title, artist, fileName);
            if (score <= bestScore) continue;
            best = suggestion;
            bestScore = score;
        }

        return best;
    }

    private static string BuildQuery(string title, string artist, string fileName)
    {
        var parts = new[] { artist, title }
            .Where(part => !string.IsNullOrWhiteSpace(part))
            .Select(part => part.Trim());
        var query = string.Join(" ", parts);
        return string.IsNullOrWhiteSpace(query)
            ? CleanFileName(fileName)
            : query;
    }

    private static int Score(MetadataSuggestion suggestion, string title, string artist, string fileName)
    {
        var score = 0;
        var cleanFileName = CleanFileName(fileName);
        if (EqualsNormalized(suggestion.Title, title)) score += 8;
        if (EqualsNormalized(suggestion.Artist, artist)) score += 8;
        if (ContainsNormalized(cleanFileName, suggestion.Title)) score += 3;
        if (ContainsNormalized(cleanFileName, suggestion.Artist)) score += 3;
        if (!string.IsNullOrWhiteSpace(suggestion.Album)) score += 1;
        if (!string.IsNullOrWhiteSpace(suggestion.Genre)) score += 1;
        return score;
    }

    private static bool EqualsNormalized(string left, string right)
    {
        return !string.IsNullOrWhiteSpace(left) &&
               !string.IsNullOrWhiteSpace(right) &&
               string.Equals(Normalize(left), Normalize(right), StringComparison.OrdinalIgnoreCase);
    }

    private static bool ContainsNormalized(string haystack, string needle)
    {
        return !string.IsNullOrWhiteSpace(haystack) &&
               !string.IsNullOrWhiteSpace(needle) &&
               Normalize(haystack).Contains(Normalize(needle), StringComparison.OrdinalIgnoreCase);
    }

    private static string CleanFileName(string fileName)
    {
        var name = Path.GetFileNameWithoutExtension(fileName);
        name = TimestampSuffixRegex().Replace(name, " ");
        name = SeparatorsRegex().Replace(name, " ");
        return string.Join(" ", name.Split((char[]?)null, StringSplitOptions.RemoveEmptyEntries));
    }

    private static string Normalize(string value)
    {
        return SeparatorsRegex().Replace(value, " ").Trim();
    }

    private static string GetString(JsonElement element, string propertyName)
    {
        return element.TryGetProperty(propertyName, out var value) && value.ValueKind == JsonValueKind.String
            ? value.GetString() ?? string.Empty
            : string.Empty;
    }

    [GeneratedRegex(@"_\d{8}_\d{6}|\b\d{8}_\d{6}\b")]
    private static partial Regex TimestampSuffixRegex();

    [GeneratedRegex(@"[_\-.]+")]
    private static partial Regex SeparatorsRegex();
}
