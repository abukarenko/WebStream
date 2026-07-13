using System.IO;

namespace WebStream;

public sealed class AudioBackBuffer
{
    private readonly Queue<AudioChunk> _chunks = new();
    private readonly object _sync = new();
    private readonly long _maxBytes;
    private long _firstPosition;
    private long _nextPosition;

    public AudioBackBuffer(long maxBytes)
    {
        _maxBytes = maxBytes;
    }

    public long CurrentPosition
    {
        get
        {
            lock (_sync) return _nextPosition;
        }
    }

    public void Reset()
    {
        lock (_sync)
        {
            _chunks.Clear();
            _firstPosition = 0;
            _nextPosition = 0;
        }
    }

    public void Append(ReadOnlySpan<byte> data)
    {
        if (data.Length == 0) return;

        var copy = data.ToArray();
        lock (_sync)
        {
            _chunks.Enqueue(new AudioChunk(_nextPosition, copy));
            _nextPosition += copy.Length;
            Trim();
        }
    }

    public byte[] SnapshotFrom(long startPosition)
    {
        lock (_sync)
        {
            var start = Math.Clamp(startPosition, _firstPosition, _nextPosition);
            using var output = new MemoryStream();
            foreach (var chunk in _chunks)
            {
                var chunkEnd = chunk.StartPosition + chunk.Bytes.Length;
                if (chunkEnd <= start) continue;

                var offset = (int)Math.Max(0, start - chunk.StartPosition);
                output.Write(chunk.Bytes, offset, chunk.Bytes.Length - offset);
            }
            return output.ToArray();
        }
    }

    private void Trim()
    {
        while (_chunks.Count > 0 && _nextPosition - _firstPosition > _maxBytes)
        {
            var removed = _chunks.Dequeue();
            _firstPosition = removed.StartPosition + removed.Bytes.Length;
        }
    }

    private sealed record AudioChunk(long StartPosition, byte[] Bytes);
}
