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

## Recording AAC Streams

MP3 streams are saved directly. AAC streams must be transcoded to MP3 with `ffmpeg.exe`.

To enable AAC -> MP3 recording, place `ffmpeg.exe` in one of these locations:

- Next to `WebStream.exe`
- `ffmpeg\bin\ffmpeg.exe` next to `WebStream.exe`
- `tools\ffmpeg.exe` next to `WebStream.exe`
- `tools\ffmpeg\bin\ffmpeg.exe` next to `WebStream.exe`
- Any folder listed in the Windows `PATH`

Recommended Windows builds are available from:

- https://www.gyan.dev/ffmpeg/builds/

Use the release essentials build, then copy `bin\ffmpeg.exe` into the program folder if you do not want to add FFmpeg to `PATH`.
