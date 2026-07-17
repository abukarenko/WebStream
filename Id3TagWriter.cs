using System.IO;
using System.Text;

namespace WebStream;

public static class Id3TagWriter
{
    private const string EditedMarkerDescription = "WebStreamEdited";
    private const string EditedMarkerValue = "1";

    public static async Task WriteAsync(string mp3Path, string title, byte[] artworkBytes, string mimeType)
    {
        if (artworkBytes.Length == 0 || !File.Exists(mp3Path)) return;

        var original = await File.ReadAllBytesAsync(mp3Path);
        var audioStart = GetAudioStart(original);
        var frames = BuildFrames(title, artworkBytes, mimeType);
        var tag = BuildTag(frames);
        var tempPath = $"{mp3Path}.tagtmp";

        await using (var output = new FileStream(tempPath, FileMode.Create, FileAccess.Write, FileShare.None, 64 * 1024, useAsync: true))
        {
            await output.WriteAsync(tag);
            await output.WriteAsync(original.AsMemory(audioStart));
        }

        File.Copy(tempPath, mp3Path, overwrite: true);
        File.Delete(tempPath);
    }

    public static async Task MarkEditedAsync(string mp3Path)
    {
        if (!File.Exists(mp3Path) || Id3TagReader.Read(mp3Path).IsEdited) return;

        var original = await File.ReadAllBytesAsync(mp3Path);
        var audioStart = GetAudioStart(original);
        var existingFrames = TrimPadding(audioStart > 10 ? original[10..audioStart] : []);

        using var frames = new MemoryStream();
        frames.Write(existingFrames);
        WriteUrlFrame(frames, EditedMarkerDescription, EditedMarkerValue);

        var tag = BuildTag(frames.ToArray());
        var tempPath = $"{mp3Path}.marktmp";
        await using (var output = new FileStream(tempPath, FileMode.Create, FileAccess.Write, FileShare.None, 64 * 1024, useAsync: true))
        {
            await output.WriteAsync(tag);
            await output.WriteAsync(original.AsMemory(audioStart));
        }

        File.Copy(tempPath, mp3Path, overwrite: true);
        File.Delete(tempPath);
    }

    public static async Task WriteMissingTextTagsAsync(string mp3Path, MetadataSuggestion suggestion)
    {
        if (!File.Exists(mp3Path)) return;

        var current = Id3TagReader.Read(mp3Path);
        var missingFrames = new Dictionary<string, string>();
        AddMissing(missingFrames, "TIT2", current.Title, suggestion.Title);
        AddMissing(missingFrames, "TPE1", current.Artist, suggestion.Artist);
        AddMissing(missingFrames, "TALB", current.Album, suggestion.Album);
        AddMissing(missingFrames, "TCON", current.Genre, suggestion.Genre);
        if (missingFrames.Count == 0) return;

        var original = await File.ReadAllBytesAsync(mp3Path);
        var audioStart = GetAudioStart(original);
        var existingFrames = TrimPadding(audioStart > 10 ? original[10..audioStart] : []);

        using var frames = new MemoryStream();
        frames.Write(existingFrames);
        foreach (var frame in missingFrames)
            WriteTextFrame(frames, frame.Key, frame.Value);

        var tag = BuildTag(frames.ToArray());
        var tempPath = $"{mp3Path}.autotagtmp";
        await using (var output = new FileStream(tempPath, FileMode.Create, FileAccess.Write, FileShare.None, 64 * 1024, useAsync: true))
        {
            await output.WriteAsync(tag);
            await output.WriteAsync(original.AsMemory(audioStart));
        }

        File.Copy(tempPath, mp3Path, overwrite: true);
        File.Delete(tempPath);
    }

    private static byte[] BuildFrames(string title, byte[] artworkBytes, string mimeType)
    {
        using var frames = new MemoryStream();
        var cleanTitle = NormalizeText(title);
        if (!string.IsNullOrWhiteSpace(cleanTitle))
        {
            var (artist, songTitle) = SplitTitle(cleanTitle);
            WriteTextFrame(frames, "TIT2", songTitle);
            if (!string.IsNullOrWhiteSpace(artist))
                WriteTextFrame(frames, "TPE1", artist);
        }

        WriteApicFrame(frames, artworkBytes, mimeType);
        return frames.ToArray();
    }

    private static void AddMissing(IDictionary<string, string> frames, string id, string currentValue, string suggestedValue)
    {
        if (!string.IsNullOrWhiteSpace(currentValue) || string.IsNullOrWhiteSpace(suggestedValue))
            return;
        frames[id] = NormalizeText(suggestedValue);
    }

    private static byte[] BuildTag(byte[] frames)
    {
        using var tag = new MemoryStream();
        tag.Write(Encoding.ASCII.GetBytes("ID3"));
        tag.WriteByte(3);
        tag.WriteByte(0);
        tag.WriteByte(0);
        tag.Write(ToSyncSafe(frames.Length));
        tag.Write(frames);
        return tag.ToArray();
    }

    private static void WriteTextFrame(Stream output, string id, string value)
    {
        using var content = new MemoryStream();
        content.WriteByte(1);
        content.Write(Encoding.Unicode.GetPreamble());
        content.Write(Encoding.Unicode.GetBytes(value));
        WriteFrame(output, id, content.ToArray());
    }

    private static void WriteApicFrame(Stream output, byte[] artworkBytes, string mimeType)
    {
        using var content = new MemoryStream();
        content.WriteByte(0);
        content.Write(Encoding.ASCII.GetBytes(mimeType));
        content.WriteByte(0);
        content.WriteByte(3);
        content.WriteByte(0);
        content.Write(artworkBytes);
        WriteFrame(output, "APIC", content.ToArray());
    }

    private static void WriteUrlFrame(Stream output, string description, string url)
    {
        using var content = new MemoryStream();
        content.WriteByte(0);
        content.Write(Encoding.Latin1.GetBytes(description));
        content.WriteByte(0);
        content.Write(Encoding.Latin1.GetBytes(url));
        WriteFrame(output, "WXXX", content.ToArray());
    }

    private static void WriteFrame(Stream output, string id, byte[] content)
    {
        output.Write(Encoding.ASCII.GetBytes(id));
        output.Write(ToBigEndian(content.Length));
        output.Write(new byte[] { 0, 0 });
        output.Write(content);
    }

    private static byte[] TrimPadding(byte[] frames)
    {
        var end = frames.Length;
        while (end > 0 && frames[end - 1] == 0)
            end--;
        return end == frames.Length ? frames : frames[..end];
    }

    private static int GetAudioStart(byte[] data)
    {
        if (data.Length < 10 || data[0] != 'I' || data[1] != 'D' || data[2] != '3') return 0;
        return 10 + FromSyncSafe(data.AsSpan(6, 4));
    }

    private static byte[] ToSyncSafe(int value)
    {
        return
        [
            (byte)((value >> 21) & 0x7F),
            (byte)((value >> 14) & 0x7F),
            (byte)((value >> 7) & 0x7F),
            (byte)(value & 0x7F)
        ];
    }

    private static int FromSyncSafe(ReadOnlySpan<byte> value)
    {
        return (value[0] << 21) | (value[1] << 14) | (value[2] << 7) | value[3];
    }

    private static byte[] ToBigEndian(int value)
    {
        return
        [
            (byte)((value >> 24) & 0xFF),
            (byte)((value >> 16) & 0xFF),
            (byte)((value >> 8) & 0xFF),
            (byte)(value & 0xFF)
        ];
    }

    private static (string Artist, string Title) SplitTitle(string title)
    {
        var separators = new[] { " - ", " – ", " — " };
        foreach (var separator in separators)
        {
            var index = title.IndexOf(separator, StringComparison.Ordinal);
            if (index <= 0) continue;
            return (title[..index].Trim(), title[(index + separator.Length)..].Trim());
        }

        return (string.Empty, title);
    }

    private static string NormalizeText(string value)
    {
        return string.Join(" ", value.Split((char[]?)null, StringSplitOptions.RemoveEmptyEntries));
    }
}
