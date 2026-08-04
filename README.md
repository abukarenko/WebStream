# WebStream

WebStream is a Windows WPF internet radio player with stream metadata, artwork lookup, playlist/history, and background song capture.

## Features

- Plays HTTP/HTTPS radio streams through the built-in Windows media stack.
- Reads ICY/HLS metadata and shows station, genre, track title, stream URL, and cover art.
- Keeps playlist and successful station history with playback timestamps.
- Remembers the last stream URL and whether playback was active on exit.
- Keeps an in-memory back buffer for the current stream so a song can be saved after it has already started.
- Saves up to three songs in parallel using background WebStream recorder processes.
- For streams without usable metadata/back buffer, records a 5-minute URL-based MP3 segment named from the stream host and timestamp.
- Shows Windows notifications when song recording starts and when the saved file is complete.
- Embeds available cover art and basic ID3 metadata into saved MP3 files.

## FFmpeg Tools in the Release

The Windows release archive already includes the FFmpeg command-line tools next to `WebStream.exe`, so a normal release install does not require a separate FFmpeg download.

- `ffmpeg.exe` is used by WebStream when an AAC stream must be converted to MP3, when a stream without usable metadata/back buffer is recorded from its URL, when the signal indicator analyzes an active stream, and when saved tracks are trimmed or rewritten with updated tags/artwork.
- `ffplay.exe` is included for manual playback checks from the command line, for example when testing whether a stream or saved audio file can be decoded outside WebStream.
- `ffprobe.exe` is included for manual media inspection from the command line, for example when checking codecs, duration, bitrate, stream layout, or metadata in a saved recording.

The bundled tools come from the Windows FFmpeg builds by Gyan Doshi:

- https://www.gyan.dev/ffmpeg/builds/
