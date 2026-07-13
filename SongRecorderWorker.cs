using System.IO;
using System.Net.Http;
using System.Diagnostics;
using System.Text;
using System.Text.RegularExpressions;

namespace WebStream;

public sealed record SongRecordingRequest(Uri StreamUri, string SeedPath, string OutputPath, string InitialTitle, string? ArtworkUrl);

public sealed class SongRecordingState(string targetTitle, bool hasSeenTargetTitle)
{
    public string TargetTitle { get; set; } = targetTitle;
    public bool HasSeenTargetTitle { get; set; } = hasSeenTargetTitle;
    public DateTime LastMetadataAt { get; set; } = DateTime.UtcNow;
    public string? PendingNextTitle { get; set; }
    public DateTime PendingNextTitleSince { get; set; }
    public int PendingNextTitleHits { get; set; }
}

public static class SongRecorderWorker
{
    private static readonly HttpClient Client = new();

    public static bool TryParse(string[] args, out SongRecordingRequest request)
    {
        request = default!;
        if (args.Length == 0 || !args.Contains("--record-song", StringComparer.OrdinalIgnoreCase)) return false;

        var url = GetArg(args, "--url");
        var seedPath = GetArg(args, "--seed");
        var outputPath = GetArg(args, "--output");
        var title = GetArg(args, "--title") ?? string.Empty;
        var artworkUrl = GetArg(args, "--artwork");
        if (!Uri.TryCreate(url, UriKind.Absolute, out var streamUri)
            || string.IsNullOrWhiteSpace(seedPath)
            || string.IsNullOrWhiteSpace(outputPath))
            return false;

        request = new SongRecordingRequest(streamUri, seedPath, outputPath, title, artworkUrl);
        return true;
    }

    public static async Task RunAsync(SongRecordingRequest request)
    {
        Directory.CreateDirectory(Path.GetDirectoryName(request.OutputPath)!);
        var seedBytes = File.Exists(request.SeedPath)
            ? await File.ReadAllBytesAsync(request.SeedPath)
            : [];

        try
        {
            if (LooksLikeMp3Bytes(seedBytes))
            {
                await RecordDirectMp3Async(request, seedBytes);
            }
            else
            {
                await RecordTranscodedMp3Async(request, seedBytes);
            }
        }
        finally
        {
            TryDelete(request.SeedPath);
        }
    }

    private static async Task RecordDirectMp3Async(SongRecordingRequest request, byte[] seedBytes)
    {
        await using (var output = new FileStream(request.OutputPath, FileMode.Create, FileAccess.Write, FileShare.Read, 64 * 1024, useAsync: true))
        {
            if (seedBytes.Length > 0)
                await output.WriteAsync(seedBytes);

            await AppendUntilSongChangesAsync(request.StreamUri, output, request.InitialTitle);
            await output.FlushAsync();
        }

        await FinishRecordingAsync(request);
    }

    private static async Task RecordTranscodedMp3Async(SongRecordingRequest request, byte[] seedBytes)
    {
        var ffmpegPath = FindFfmpegPath();
        if (ffmpegPath is null)
        {
            await WindowsNotifier.ShowAsync("Нужен ffmpeg.exe", "AAC-поток нельзя сохранить в MP3 без ffmpeg");
            return;
        }

        var artworkPath = await DownloadArtworkForFfmpegAsync(request);
        try
        {
            using var ffmpeg = StartFfmpeg(ffmpegPath, request.OutputPath, request.InitialTitle, artworkPath);
            var stderrTask = ffmpeg.StandardError.ReadToEndAsync();
            await using (var ffmpegInput = ffmpeg.StandardInput.BaseStream)
            {
                if (seedBytes.Length > 0)
                    await ffmpegInput.WriteAsync(seedBytes);

                await AppendUntilSongChangesAsync(request.StreamUri, ffmpegInput, request.InitialTitle);
            }

            await ffmpeg.WaitForExitAsync();
            var stderr = await stderrTask;
            if (ffmpeg.ExitCode != 0)
                throw new IOException($"ffmpeg завершился с кодом {ffmpeg.ExitCode}: {stderr}");

            await WindowsNotifier.ShowAsync("Песня сохранена", BuildSavedNotificationText(request));
        }
        finally
        {
            if (!string.IsNullOrWhiteSpace(artworkPath))
                TryDelete(artworkPath);
        }
    }

    private static async Task<string?> DownloadArtworkForFfmpegAsync(SongRecordingRequest request)
    {
        if (!Uri.TryCreate(request.ArtworkUrl, UriKind.Absolute, out var artworkUri)) return null;

        try
        {
            using var response = await Client.GetAsync(artworkUri);
            response.EnsureSuccessStatusCode();
            var artworkBytes = await response.Content.ReadAsByteArrayAsync();
            var mimeType = DetectImageMimeType(artworkBytes, response.Content.Headers.ContentType?.MediaType);
            if (mimeType is null) return null;

            var extension = mimeType.Equals("image/png", StringComparison.OrdinalIgnoreCase) ? ".png" : ".jpg";
            var path = Path.Combine(Path.GetTempPath(), "WebStream", $"{Guid.NewGuid():N}{extension}");
            Directory.CreateDirectory(Path.GetDirectoryName(path)!);
            await File.WriteAllBytesAsync(path, artworkBytes);
            return path;
        }
        catch (Exception ex) when (ex is HttpRequestException or IOException or UnauthorizedAccessException)
        {
            return null;
        }
    }

    private static Process StartFfmpeg(string ffmpegPath, string outputPath, string title, string? artworkPath)
    {
        var startInfo = new ProcessStartInfo(ffmpegPath)
        {
            UseShellExecute = false,
            CreateNoWindow = true,
            RedirectStandardInput = true,
            RedirectStandardError = true
        };
        startInfo.ArgumentList.Add("-hide_banner");
        startInfo.ArgumentList.Add("-loglevel");
        startInfo.ArgumentList.Add("error");
        startInfo.ArgumentList.Add("-y");
        startInfo.ArgumentList.Add("-i");
        startInfo.ArgumentList.Add("pipe:0");
        if (!string.IsNullOrWhiteSpace(artworkPath))
        {
            startInfo.ArgumentList.Add("-i");
            startInfo.ArgumentList.Add(artworkPath);
            startInfo.ArgumentList.Add("-map");
            startInfo.ArgumentList.Add("0:a");
            startInfo.ArgumentList.Add("-map");
            startInfo.ArgumentList.Add("1:v");
            startInfo.ArgumentList.Add("-c:v");
            startInfo.ArgumentList.Add("mjpeg");
            startInfo.ArgumentList.Add("-disposition:v");
            startInfo.ArgumentList.Add("attached_pic");
        }
        if (string.IsNullOrWhiteSpace(artworkPath))
        {
            startInfo.ArgumentList.Add("-vn");
        }
        var (artist, songTitle) = SplitTitleForMetadata(title);
        if (!string.IsNullOrWhiteSpace(songTitle))
        {
            startInfo.ArgumentList.Add("-metadata");
            startInfo.ArgumentList.Add($"title={songTitle}");
        }
        if (!string.IsNullOrWhiteSpace(artist))
        {
            startInfo.ArgumentList.Add("-metadata");
            startInfo.ArgumentList.Add($"artist={artist}");
        }
        startInfo.ArgumentList.Add("-codec:a");
        startInfo.ArgumentList.Add("libmp3lame");
        startInfo.ArgumentList.Add("-b:a");
        startInfo.ArgumentList.Add("192k");
        startInfo.ArgumentList.Add("-id3v2_version");
        startInfo.ArgumentList.Add("3");
        startInfo.ArgumentList.Add(outputPath);
        return Process.Start(startInfo) ?? throw new IOException("Не удалось запустить ffmpeg.exe");
    }

    private static (string Artist, string Title) SplitTitleForMetadata(string title)
    {
        var clean = NormalizeTitle(title);
        var separators = new[] { " - ", " – ", " — " };
        foreach (var separator in separators)
        {
            var index = clean.IndexOf(separator, StringComparison.Ordinal);
            if (index <= 0) continue;
            return (clean[..index].Trim(), clean[(index + separator.Length)..].Trim());
        }
        return (string.Empty, clean);
    }

    private static async Task EmbedArtworkAsync(SongRecordingRequest request)
    {
        if (!Uri.TryCreate(request.ArtworkUrl, UriKind.Absolute, out var artworkUri)) return;

        using var response = await Client.GetAsync(artworkUri);
        response.EnsureSuccessStatusCode();
        var artworkBytes = await response.Content.ReadAsByteArrayAsync();
        var mimeType = DetectImageMimeType(artworkBytes, response.Content.Headers.ContentType?.MediaType);
        if (mimeType is null) return;

        await Id3TagWriter.WriteAsync(request.OutputPath, request.InitialTitle, artworkBytes, mimeType);
    }

    private static async Task FinishRecordingAsync(SongRecordingRequest request)
    {
        try
        {
            await EmbedArtworkAsync(request);
            await WindowsNotifier.ShowAsync("Песня сохранена", BuildSavedNotificationText(request));
        }
        catch (Exception ex) when (ex is HttpRequestException or IOException or UnauthorizedAccessException)
        {
            await WindowsNotifier.ShowAsync("Песня сохранена", $"{BuildSavedNotificationText(request)}\nОбложка не встроена");
        }
    }

    private static async Task AppendUntilSongChangesAsync(Uri streamUri, Stream output, string initialTitle)
    {
        var state = new SongRecordingState(BuildSongCompareKey(initialTitle), string.IsNullOrWhiteSpace(BuildSongCompareKey(initialTitle)));
        var startedAt = DateTime.UtcNow;
        var minimumRecordingDuration = TimeSpan.FromSeconds(60);
        var maximumRecordingDuration = TimeSpan.FromMinutes(20);
        var metadataSilenceTimeout = TimeSpan.FromMinutes(5);

        while (DateTime.UtcNow - startedAt < maximumRecordingDuration)
        {
            if (DateTime.UtcNow - startedAt >= minimumRecordingDuration
                && DateTime.UtcNow - state.LastMetadataAt >= metadataSilenceTimeout)
                break;

            try
            {
                if (await AppendOneConnectionAsync(streamUri, output, state, startedAt, minimumRecordingDuration, metadataSilenceTimeout))
                    break;
            }
            catch (EndOfStreamException)
            {
            }
            catch (IOException)
            {
            }
            catch (HttpRequestException)
            {
            }

            await Task.Delay(TimeSpan.FromSeconds(1));
        }
    }

    private static async Task<bool> AppendOneConnectionAsync(
        Uri streamUri,
        Stream output,
        SongRecordingState state,
        DateTime startedAt,
        TimeSpan minimumRecordingDuration,
        TimeSpan metadataSilenceTimeout)
    {
        using var request = new HttpRequestMessage(HttpMethod.Get, streamUri);
        request.Headers.TryAddWithoutValidation("Icy-MetaData", "1");
        request.Headers.TryAddWithoutValidation("User-Agent", "WebStream/1.0 recorder");
        using var response = await Client.SendAsync(request, HttpCompletionOption.ResponseHeadersRead);
        response.EnsureSuccessStatusCode();

        await using var source = await response.Content.ReadAsStreamAsync();
        var metaIntervalText = GetHeaderValue(response, "icy-metaint");
        if (!int.TryParse(metaIntervalText, out var interval) || interval <= 0)
        {
            await CopyForFallbackDurationAsync(source, output, TimeSpan.FromSeconds(30), state, startedAt, minimumRecordingDuration, metadataSilenceTimeout);
            return false;
        }

        var audioBuffer = new byte[Math.Min(interval, 8192)];

        while (true)
        {
            if (DateTime.UtcNow - startedAt >= minimumRecordingDuration
                && DateTime.UtcNow - state.LastMetadataAt >= metadataSilenceTimeout)
                return true;

            await CopyAudioExactlyAsync(source, output, audioBuffer, interval);

            var length = source.ReadByte();
            if (length < 0) return false;

            var metadataLength = length * 16;
            if (metadataLength == 0) continue;

            var metadata = new byte[metadataLength];
            await ReadExactlyAsync(source, metadata, metadataLength);
            var title = ExtractStreamTitle(Encoding.Latin1.GetString(metadata));
            var songKey = BuildSongCompareKey(title);
            title = NormalizeTitle(title);
            if (string.IsNullOrWhiteSpace(title)) continue;
            state.LastMetadataAt = DateTime.UtcNow;

            if (string.IsNullOrWhiteSpace(state.TargetTitle))
            {
                state.TargetTitle = songKey;
                state.HasSeenTargetTitle = true;
                continue;
            }

            if (string.Equals(songKey, state.TargetTitle, StringComparison.OrdinalIgnoreCase))
            {
                state.HasSeenTargetTitle = true;
                state.PendingNextTitle = null;
                state.PendingNextTitleHits = 0;
                continue;
            }

            if (IsConfidentSongChange(state.TargetTitle, songKey))
                return true;

            if (!state.HasSeenTargetTitle || DateTime.UtcNow - startedAt < minimumRecordingDuration)
                continue;

            if (!string.Equals(songKey, state.PendingNextTitle, StringComparison.OrdinalIgnoreCase))
            {
                state.PendingNextTitle = songKey;
                state.PendingNextTitleSince = DateTime.UtcNow;
                state.PendingNextTitleHits = 1;
                continue;
            }

            state.PendingNextTitleHits++;
            if (state.PendingNextTitleHits >= 3 && DateTime.UtcNow - state.PendingNextTitleSince >= TimeSpan.FromSeconds(30))
                return true;
        }
    }

    private static async Task CopyAudioExactlyAsync(Stream source, Stream output, byte[] buffer, int bytesToCopy)
    {
        var remaining = bytesToCopy;
        while (remaining > 0)
        {
            var read = await source.ReadAsync(buffer.AsMemory(0, Math.Min(buffer.Length, remaining)));
            if (read == 0) throw new EndOfStreamException();
            await output.WriteAsync(buffer.AsMemory(0, read));
            remaining -= read;
        }
    }

    private static async Task CopyForFallbackDurationAsync(
        Stream source,
        Stream output,
        TimeSpan duration,
        SongRecordingState state,
        DateTime startedAt,
        TimeSpan minimumRecordingDuration,
        TimeSpan metadataSilenceTimeout)
    {
        var buffer = new byte[64 * 1024];
        var fallbackStartedAt = DateTime.UtcNow;
        while (DateTime.UtcNow - fallbackStartedAt < duration)
        {
            if (DateTime.UtcNow - startedAt >= minimumRecordingDuration
                && DateTime.UtcNow - state.LastMetadataAt >= metadataSilenceTimeout)
                break;

            var read = await source.ReadAsync(buffer);
            if (read == 0) break;
            await output.WriteAsync(buffer.AsMemory(0, read));
        }
    }

    private static async Task ReadExactlyAsync(Stream stream, byte[] buffer, int bytesToRead)
    {
        var offset = 0;
        while (offset < bytesToRead)
        {
            var read = await stream.ReadAsync(buffer.AsMemory(offset, bytesToRead - offset));
            if (read == 0) throw new EndOfStreamException();
            offset += read;
        }
    }

    private static string? GetHeaderValue(HttpResponseMessage response, string name)
    {
        if (response.Headers.TryGetValues(name, out var values)) return values.FirstOrDefault();
        return response.Content.Headers.TryGetValues(name, out values) ? values.FirstOrDefault() : null;
    }

    private static string? ExtractStreamTitle(string metadata)
    {
        foreach (var quote in new[] { '\'', '"' })
        {
            var prefix = $"StreamTitle={quote}";
            var start = metadata.IndexOf(prefix, StringComparison.OrdinalIgnoreCase);
            if (start < 0) continue;
            start += prefix.Length;
            var end = metadata.IndexOf(quote, start);
            return (end < 0 ? metadata[start..] : metadata[start..end]).Trim('\0', ' ');
        }
        return null;
    }

    private static string NormalizeTitle(string? title)
    {
        return Regex.Replace(title ?? string.Empty, @"\s+", " ").Trim();
    }

    private static string BuildSongCompareKey(string? title)
    {
        var normalized = NormalizeTitle(title).ToUpperInvariant();
        normalized = Regex.Replace(normalized, @"\s+[-–—]\s+(OFFICIAL|RADIO|LIVE|HD|HQ|STEREO|MONO|REMIX|VERSION|EDIT|VIDEO).*$", "", RegexOptions.IgnoreCase);
        normalized = Regex.Replace(normalized, @"\s*\((OFFICIAL|RADIO|LIVE|HD|HQ|STEREO|MONO|REMIX|VERSION|EDIT|VIDEO)[^)]*\)", "", RegexOptions.IgnoreCase);
        normalized = Regex.Replace(normalized, @"\s*\[(OFFICIAL|RADIO|LIVE|HD|HQ|STEREO|MONO|REMIX|VERSION|EDIT|VIDEO)[^\]]*\]", "", RegexOptions.IgnoreCase);
        normalized = Regex.Replace(normalized, @"[^\p{L}\p{N}]+", " ");
        return Regex.Replace(normalized, @"\s+", " ").Trim();
    }

    private static bool IsConfidentSongChange(string currentKey, string nextKey)
    {
        if (string.IsNullOrWhiteSpace(currentKey) || string.IsNullOrWhiteSpace(nextKey)) return false;
        if (string.Equals(currentKey, nextKey, StringComparison.OrdinalIgnoreCase)) return false;
        return CountWords(currentKey) >= 3 && CountWords(nextKey) >= 3;
    }

    private static int CountWords(string value)
    {
        return value.Split(' ', StringSplitOptions.RemoveEmptyEntries).Length;
    }

    private static bool LooksLikeMp3Bytes(byte[] bytes)
    {
        if (LooksLikeAacAdtsBytes(bytes)) return false;
        if (bytes.Length >= 3 && bytes[0] == 'I' && bytes[1] == 'D' && bytes[2] == '3') return true;

        var scanLimit = Math.Min(bytes.Length - 1, 4096);
        for (var index = 0; index < scanLimit; index++)
        {
            var frameLength = GetMp3FrameLength(bytes, index);
            if (frameLength <= 0) continue;
            var nextFrame = index + frameLength;
            if (nextFrame + 1 < bytes.Length && GetMp3FrameLength(bytes, nextFrame) > 0)
                return true;
        }

        return false;
    }

    private static bool LooksLikeAacAdtsBytes(byte[] bytes)
    {
        var scanLimit = Math.Min(bytes.Length - 7, 4096);
        for (var index = 0; index < scanLimit; index++)
        {
            var frameLength = GetAdtsFrameLength(bytes, index);
            if (frameLength <= 0) continue;
            var nextFrame = index + frameLength;
            if (nextFrame + 7 < bytes.Length && GetAdtsFrameLength(bytes, nextFrame) > 0)
                return true;
        }

        return false;
    }

    private static int GetAdtsFrameLength(byte[] bytes, int index)
    {
        if (index + 6 >= bytes.Length) return -1;
        if (bytes[index] != 0xFF || (bytes[index + 1] & 0xF0) != 0xF0) return -1;
        if ((bytes[index + 1] & 0x06) != 0) return -1;
        var frameLength = ((bytes[index + 3] & 0x03) << 11) | (bytes[index + 4] << 3) | ((bytes[index + 5] & 0xE0) >> 5);
        return frameLength >= 7 ? frameLength : -1;
    }

    private static int GetMp3FrameLength(byte[] bytes, int index)
    {
        if (index + 3 >= bytes.Length) return -1;
        if (bytes[index] != 0xFF || (bytes[index + 1] & 0xE0) != 0xE0) return -1;

        var versionBits = (bytes[index + 1] >> 3) & 0x03;
        var layerBits = (bytes[index + 1] >> 1) & 0x03;
        var bitrateIndex = (bytes[index + 2] >> 4) & 0x0F;
        var sampleRateIndex = (bytes[index + 2] >> 2) & 0x03;
        var padding = (bytes[index + 2] >> 1) & 0x01;
        if (versionBits == 0x01 || layerBits == 0x00 || bitrateIndex == 0x00 || bitrateIndex == 0x0F || sampleRateIndex == 0x03)
            return -1;

        var bitrate = GetMp3Bitrate(versionBits, layerBits, bitrateIndex);
        var sampleRate = GetMp3SampleRate(versionBits, sampleRateIndex);
        if (bitrate <= 0 || sampleRate <= 0) return -1;

        return layerBits == 0x03
            ? ((12 * bitrate / sampleRate) + padding) * 4
            : ((versionBits == 0x03 ? 144 : 72) * bitrate / sampleRate) + padding;
    }

    private static int GetMp3Bitrate(int versionBits, int layerBits, int index)
    {
        int[] mpeg1Layer1 = [0, 32, 64, 96, 128, 160, 192, 224, 256, 288, 320, 352, 384, 416, 448];
        int[] mpeg1Layer2 = [0, 32, 48, 56, 64, 80, 96, 112, 128, 160, 192, 224, 256, 320, 384];
        int[] mpeg1Layer3 = [0, 32, 40, 48, 56, 64, 80, 96, 112, 128, 160, 192, 224, 256, 320];
        int[] mpeg2Layer1 = [0, 32, 48, 56, 64, 80, 96, 112, 128, 144, 160, 176, 192, 224, 256];
        int[] mpeg2Layer23 = [0, 8, 16, 24, 32, 40, 48, 56, 64, 80, 96, 112, 128, 144, 160];
        var table = versionBits == 0x03
            ? layerBits switch { 0x03 => mpeg1Layer1, 0x02 => mpeg1Layer2, _ => mpeg1Layer3 }
            : layerBits == 0x03 ? mpeg2Layer1 : mpeg2Layer23;
        return table[index] * 1000;
    }

    private static int GetMp3SampleRate(int versionBits, int index)
    {
        int[] mpeg1 = [44100, 48000, 32000];
        var rate = mpeg1[index];
        return versionBits switch
        {
            0x03 => rate,
            0x02 => rate / 2,
            0x00 => rate / 4,
            _ => -1
        };
    }

    private static string? FindFfmpegPath()
    {
        var localCandidates = new[]
        {
            Path.Combine(AppContext.BaseDirectory, "ffmpeg.exe"),
            Path.Combine(AppContext.BaseDirectory, "ffmpeg", "bin", "ffmpeg.exe"),
            Path.Combine(AppContext.BaseDirectory, "tools", "ffmpeg.exe"),
            Path.Combine(AppContext.BaseDirectory, "tools", "ffmpeg", "bin", "ffmpeg.exe")
        };

        foreach (var candidate in localCandidates)
            if (File.Exists(candidate))
                return candidate;

        var path = Environment.GetEnvironmentVariable("PATH");
        if (string.IsNullOrWhiteSpace(path)) return null;

        foreach (var directory in path.Split(Path.PathSeparator))
        {
            if (string.IsNullOrWhiteSpace(directory)) continue;
            var candidate = Path.Combine(directory.Trim(), "ffmpeg.exe");
            if (File.Exists(candidate)) return candidate;
        }

        return null;
    }

    private static string? DetectImageMimeType(byte[] bytes, string? headerMimeType)
    {
        if (bytes.Length >= 3 && bytes[0] == 0xFF && bytes[1] == 0xD8 && bytes[2] == 0xFF)
            return "image/jpeg";
        if (bytes.Length >= 8
            && bytes[0] == 0x89
            && bytes[1] == 0x50
            && bytes[2] == 0x4E
            && bytes[3] == 0x47)
            return "image/png";
        if (bytes.Length >= 12
            && bytes[0] == 'R'
            && bytes[1] == 'I'
            && bytes[2] == 'F'
            && bytes[3] == 'F'
            && bytes[8] == 'W'
            && bytes[9] == 'E'
            && bytes[10] == 'B'
            && bytes[11] == 'P')
            return "image/webp";

        return headerMimeType is not null && headerMimeType.StartsWith("image/", StringComparison.OrdinalIgnoreCase)
            ? headerMimeType
            : null;
    }

    private static string BuildSavedNotificationText(SongRecordingRequest request)
    {
        var title = NormalizeTitle(request.InitialTitle);
        return string.IsNullOrWhiteSpace(title)
            ? Path.GetFileNameWithoutExtension(request.OutputPath)
            : title;
    }

    private static bool LooksLikeMp3Stream(Uri streamUri, HttpResponseMessage response)
    {
        var mediaType = response.Content.Headers.ContentType?.MediaType;
        if (mediaType is null) return streamUri.AbsolutePath.Contains("mp3", StringComparison.OrdinalIgnoreCase);
        return mediaType.Equals("audio/mpeg", StringComparison.OrdinalIgnoreCase)
            || mediaType.Equals("audio/mp3", StringComparison.OrdinalIgnoreCase)
            || (mediaType.Equals("application/octet-stream", StringComparison.OrdinalIgnoreCase)
                && streamUri.AbsolutePath.Contains("mp3", StringComparison.OrdinalIgnoreCase));
    }

    private static string? GetArg(string[] args, string name)
    {
        for (var i = 0; i < args.Length - 1; i++)
            if (string.Equals(args[i], name, StringComparison.OrdinalIgnoreCase))
                return args[i + 1];
        return null;
    }

    private static void TryDelete(string path)
    {
        try
        {
            if (File.Exists(path)) File.Delete(path);
        }
        catch (IOException)
        {
        }
        catch (UnauthorizedAccessException)
        {
        }
    }
}
