using System.Net.Http;

namespace WebStream;

public static class StreamPlaylistResolver
{
    private static readonly HttpClient Client = new();

    public static async Task<Uri> ResolveAsync(Uri streamUri, CancellationToken cancellationToken = default)
    {
        if (!LooksLikePlaylist(streamUri)) return streamUri;

        using var request = new HttpRequestMessage(HttpMethod.Get, streamUri);
        request.Headers.TryAddWithoutValidation("User-Agent", "WebStream/1.0");
        using var response = await Client.SendAsync(request, cancellationToken);
        response.EnsureSuccessStatusCode();

        var text = await response.Content.ReadAsStringAsync(cancellationToken);
        return TryResolveM3u(streamUri, text)
            ?? TryResolvePls(streamUri, text)
            ?? streamUri;
    }

    private static bool LooksLikePlaylist(Uri streamUri)
    {
        var path = streamUri.AbsolutePath;
        return path.EndsWith(".m3u", StringComparison.OrdinalIgnoreCase)
            || path.EndsWith(".pls", StringComparison.OrdinalIgnoreCase);
    }

    private static Uri? TryResolveM3u(Uri baseUri, string text)
    {
        foreach (var rawLine in text.Split(['\r', '\n'], StringSplitOptions.RemoveEmptyEntries))
        {
            var line = rawLine.Trim();
            if (line.Length == 0 || line.StartsWith('#')) continue;
            if (Uri.TryCreate(baseUri, line, out var resolved)
                && (resolved.Scheme == Uri.UriSchemeHttp || resolved.Scheme == Uri.UriSchemeHttps))
                return resolved;
        }

        return null;
    }

    private static Uri? TryResolvePls(Uri baseUri, string text)
    {
        foreach (var rawLine in text.Split(['\r', '\n'], StringSplitOptions.RemoveEmptyEntries))
        {
            var line = rawLine.Trim();
            if (!line.StartsWith("File", StringComparison.OrdinalIgnoreCase)) continue;
            var separator = line.IndexOf('=');
            if (separator < 0 || separator == line.Length - 1) continue;
            var value = line[(separator + 1)..].Trim();
            if (Uri.TryCreate(baseUri, value, out var resolved)
                && (resolved.Scheme == Uri.UriSchemeHttp || resolved.Scheme == Uri.UriSchemeHttps))
                return resolved;
        }

        return null;
    }
}
