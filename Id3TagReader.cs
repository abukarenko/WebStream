using System.IO;
using System.Text;

namespace WebStream;

public sealed record Id3TagInfo(string Title, string Artist, string Album, string Genre, bool IsEdited = false);

public static class Id3TagReader
{
    public static Id3TagInfo Read(string path)
    {
        try
        {
            using var input = File.OpenRead(path);
            Span<byte> header = stackalloc byte[10];
            if (input.Read(header) != 10 || header[0] != 'I' || header[1] != 'D' || header[2] != '3')
                return FromFileName(path);

            var version = header[3];
            var tagSize = FromSyncSafe(header[6..10]);
            var tag = new byte[tagSize];
            if (input.Read(tag, 0, tag.Length) != tag.Length)
                return FromFileName(path);

            var title = string.Empty;
            var artist = string.Empty;
            var album = string.Empty;
            var genre = string.Empty;
            var isEdited = false;
            var offset = 0;
            while (offset + 10 <= tag.Length)
            {
                var id = Encoding.ASCII.GetString(tag, offset, 4);
                if (id.Any(ch => ch < 'A' || ch > 'Z') && !id.Any(char.IsDigit)) break;

                var size = version == 4
                    ? FromSyncSafe(tag.AsSpan(offset + 4, 4))
                    : FromBigEndian(tag.AsSpan(offset + 4, 4));
                if (size <= 0 || offset + 10 + size > tag.Length) break;

                var value = ReadTextFrame(tag.AsSpan(offset + 10, size));
                switch (id)
                {
                    case "TIT2": title = value; break;
                    case "TPE1": artist = value; break;
                    case "TALB": album = value; break;
                    case "TCON": genre = value; break;
                    case "WXXX": isEdited |= IsWebStreamEditedMarker(tag.AsSpan(offset + 10, size)); break;
                }

                offset += 10 + size;
            }

            isEdited |= ContainsWebStreamEditedMarker(tag);
            var tagInfo = string.IsNullOrWhiteSpace(title) && string.IsNullOrWhiteSpace(artist)
                ? FromFileName(path)
                : new Id3TagInfo(title, artist, album, genre);
            return tagInfo with { IsEdited = isEdited };
        }
        catch (IOException)
        {
            return FromFileName(path);
        }
        catch (UnauthorizedAccessException)
        {
            return FromFileName(path);
        }
    }

    private static Id3TagInfo FromFileName(string path)
    {
        var name = Path.GetFileNameWithoutExtension(path);
        var separators = new[] { " - ", " – ", " — " };
        foreach (var separator in separators)
        {
            var index = name.IndexOf(separator, StringComparison.Ordinal);
            if (index <= 0) continue;
            return new Id3TagInfo(name[(index + separator.Length)..].Trim(), name[..index].Trim(), string.Empty, string.Empty);
        }

        return new Id3TagInfo(name, string.Empty, string.Empty, string.Empty);
    }

    private static string ReadTextFrame(ReadOnlySpan<byte> content)
    {
        if (content.Length == 0) return string.Empty;
        var encoding = content[0];
        var data = content[1..];
        var value = encoding switch
        {
            0 => Encoding.Latin1.GetString(data),
            1 => Encoding.Unicode.GetString(data),
            2 => Encoding.BigEndianUnicode.GetString(data),
            3 => Encoding.UTF8.GetString(data),
            _ => Encoding.UTF8.GetString(data)
        };
        return value.Trim('\0', '\uFEFF', ' ', '\r', '\n');
    }

    private static bool IsWebStreamEditedMarker(ReadOnlySpan<byte> content)
    {
        if (content.Length == 0) return false;
        var value = Encoding.Latin1.GetString(content[1..]);
        return value.Contains("WebStreamEdited", StringComparison.OrdinalIgnoreCase);
    }

    private static bool ContainsWebStreamEditedMarker(byte[] tag)
    {
        return Encoding.Latin1.GetString(tag)
            .Contains("WebStreamEdited", StringComparison.OrdinalIgnoreCase);
    }

    private static int FromSyncSafe(ReadOnlySpan<byte> value)
    {
        return (value[0] << 21) | (value[1] << 14) | (value[2] << 7) | value[3];
    }

    private static int FromBigEndian(ReadOnlySpan<byte> value)
    {
        return (value[0] << 24) | (value[1] << 16) | (value[2] << 8) | value[3];
    }
}
