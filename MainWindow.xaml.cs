using System.Collections.ObjectModel;
using System.IO;
using System.Net.Http;
using System.Text.Json;
using System.Text;
using System.Text.RegularExpressions;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Media.Imaging;

namespace WebStream;

public partial class MainWindow : Window
{
    private static readonly HttpClient MetadataClient = new();
    private readonly ObservableCollection<RadioStation> _stations = new();
    private readonly ObservableCollection<RadioStation> _history = new();
    private readonly string _historyPath = Path.Combine(
        Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData), "WebStream", "history.json");
    private bool _isPlaying;
    private CancellationTokenSource? _metadataCancellation;
    private string? _lastArtworkQuery;
    private bool _hasStreamArtwork;

    public MainWindow()
    {
        InitializeComponent();
        StationsList.ItemsSource = _stations;
        HistoryList.ItemsSource = _history;
        AddBuiltInStations();
        LoadHistory();
    }

    private void AddBuiltInStations()
    {
        _stations.Add(new RadioStation("SomaFM Groove Salad", "Ambient · Demo", "https://ice1.somafm.com/groovesalad-128-mp3"));
        _stations.Add(new RadioStation("SomaFM Drone Zone", "Ambient · Demo", "https://ice1.somafm.com/dronezone-128-mp3"));
    }

    private void StationsList_SelectionChanged(object sender, SelectionChangedEventArgs e)
    {
        if (StationsList.SelectedItem is not RadioStation station) return;
        HistoryList.SelectedItem = null;
        SelectStation(station);
    }

    private void HistoryList_SelectionChanged(object sender, SelectionChangedEventArgs e)
    {
        if (HistoryList.SelectedItem is not RadioStation station) return;
        StationsList.SelectedItem = null;
        SelectStation(station);
    }

    private void SelectStation(RadioStation station)
    {
        StationNameText.Text = station.Name;
        TrackText.Text = station.Description;
        StreamUrlBox.Text = station.StreamUrl;
        UpdateMetadata(station.Name, station.Description, station.StreamUrl, "Подключение к станции…");
        StartPlayback();
    }

    private void PlayButton_Click(object sender, RoutedEventArgs e)
    {
        if (_isPlaying)
        {
            Player.Pause();
            _isPlaying = false;
            PlayButton.Content = "▶  Слушать";
            StatusText.Text = "ПАУЗА";
            return;
        }
        StartPlayback();
    }

    private void StartPlayback()
    {
        var value = StreamUrlBox.Text.Trim();
        if (!Uri.TryCreate(value, UriKind.Absolute, out var uri) || (uri.Scheme != Uri.UriSchemeHttp && uri.Scheme != Uri.UriSchemeHttps))
        {
            StatusText.Text = "ПРОВЕРЬТЕ URL";
            TrackText.Text = "Укажите корректную ссылку HTTP(S) на аудиопоток.";
            return;
        }

        if (StationsList.SelectedItem is null) StationNameText.Text = "Мой поток";
        StatusText.Text = "ПОДКЛЮЧЕНИЕ…";
        TrackText.Text = "Соединяемся с радиостанцией…";
        MetadataLogText.Clear();
        AppendMetadata($"Подключение\n{value}");
        UpdateMetadata(StationNameText.Text, MetaDescriptionText.Text == "—" ? "Пользовательский поток" : MetaDescriptionText.Text, value, "Подключение к станции…");
        Player.Stop();
        ResetArtwork();
        Player.Source = uri;
        Player.Play();
        StartMetadataReader(uri);
        _isPlaying = true;
        PlayButton.Content = "Ⅱ  Пауза";
    }

    private void StopButton_Click(object sender, RoutedEventArgs e)
    {
        Player.Stop();
        StopMetadataReader();
        _isPlaying = false;
        PlayButton.Content = "▶  Слушать";
        StatusText.Text = "ОСТАНОВЛЕНО";
        TrackText.Text = "Воспроизведение остановлено.";
        MetaStatusText.Text = "Воспроизведение остановлено";
    }

    private void Player_MediaOpened(object sender, RoutedEventArgs e)
    {
        StatusText.Text = "В ЭФИРЕ";
        TrackText.Text = "Поток воспроизводится.";
        MetaStatusText.Text = "В эфире";
        SaveCurrentStationToHistory();
    }

    private void Player_MediaFailed(object sender, ExceptionRoutedEventArgs e)
    {
        _isPlaying = false;
        PlayButton.Content = "▶  Слушать";
        StatusText.Text = "ОШИБКА";
        TrackText.Text = "Не удалось открыть поток. Проверьте адрес или формат станции.";
        MetaStatusText.Text = "Не удалось открыть поток";
        StopMetadataReader();
    }

    private void AddStation_Click(object sender, RoutedEventArgs e)
    {
        StreamUrlBox.Focus();
        StreamUrlBox.SelectAll();
        TrackText.Text = "Вставьте ссылку на поток в нижнее поле и нажмите «Слушать».";
    }

    private void PasteReplacementUrl_Click(object sender, RoutedEventArgs e)
    {
        if (!Clipboard.ContainsText()) return;
        StreamUrlBox.Text = Clipboard.GetText().Trim();
        StreamUrlBox.Focus();
        StreamUrlBox.CaretIndex = StreamUrlBox.Text.Length;
    }

    private void ClearStreamUrl_Click(object sender, RoutedEventArgs e)
    {
        StreamUrlBox.Clear();
        StreamUrlBox.Focus();
    }

    private void SelectAllStreamUrl_Click(object sender, RoutedEventArgs e)
    {
        StreamUrlBox.Focus();
        StreamUrlBox.SelectAll();
    }

    protected override void OnClosed(EventArgs e)
    {
        StopMetadataReader();
        base.OnClosed(e);
    }

    private void StartMetadataReader(Uri streamUri)
    {
        StopMetadataReader();
        _metadataCancellation = new CancellationTokenSource();
        _ = streamUri.AbsolutePath.EndsWith(".m3u8", StringComparison.OrdinalIgnoreCase)
            ? ReadHlsMetadataAsync(streamUri, _metadataCancellation.Token)
            : ReadIcyMetadataAsync(streamUri, _metadataCancellation.Token);
    }

    private void StopMetadataReader()
    {
        _metadataCancellation?.Cancel();
        _metadataCancellation?.Dispose();
        _metadataCancellation = null;
    }

    private async Task ReadIcyMetadataAsync(Uri streamUri, CancellationToken cancellationToken)
    {
        try
        {
            using var request = new HttpRequestMessage(HttpMethod.Get, streamUri);
            request.Headers.TryAddWithoutValidation("Icy-MetaData", "1");
            request.Headers.TryAddWithoutValidation("User-Agent", "WebStream/1.0");
            using var response = await MetadataClient.SendAsync(request, HttpCompletionOption.ResponseHeadersRead, cancellationToken);
            response.EnsureSuccessStatusCode();

            var metaInterval = GetHeaderValue(response, "icy-metaint");
            var stationName = GetHeaderValue(response, "icy-name");
            var genre = GetHeaderValue(response, "icy-genre");
            await Dispatcher.InvokeAsync(() =>
            {
                UpdateIcyHeaders(stationName, genre);
                AppendMetadata($"ICY headers\nicy-name: {stationName ?? "—"}\nicy-genre: {genre ?? "—"}\nicy-metaint: {metaInterval ?? "—"}");
            });

            if (!int.TryParse(metaInterval, out var interval) || interval <= 0) return;

            await using var stream = await response.Content.ReadAsStreamAsync(cancellationToken);
            var buffer = new byte[Math.Min(interval, 8192)];
            while (!cancellationToken.IsCancellationRequested)
            {
                await SkipExactlyAsync(stream, buffer, interval, cancellationToken);
                var length = stream.ReadByte();
                if (length < 0) break;

                var metadataLength = length * 16;
                if (metadataLength == 0) continue;
                var metadata = new byte[metadataLength];
                await ReadExactlyAsync(stream, metadata, metadataLength, cancellationToken);
                var text = Encoding.Latin1.GetString(metadata);
                var title = ExtractStreamTitle(text);
                await Dispatcher.InvokeAsync(() => AppendMetadata($"ICY metadata\n{text.Trim('\0', ' ')}"));
                if (!string.IsNullOrWhiteSpace(title))
                    await Dispatcher.InvokeAsync(() => SetCurrentTrack(title));
                var coverUrl = ExtractCoverArtUrl(text);
                if (!string.IsNullOrWhiteSpace(coverUrl))
                    await Dispatcher.InvokeAsync(() => SetArtwork(coverUrl));
            }
        }
        catch (OperationCanceledException)
        {
            // Switching or stopping a station intentionally ends the metadata stream.
        }
        catch (HttpRequestException)
        {
            await Dispatcher.InvokeAsync(() => MetaStatusText.Text = "Эфир без ICY-метаданных");
        }
        catch (EndOfStreamException)
        {
            await Dispatcher.InvokeAsync(() => MetaStatusText.Text = "Поток метаданных завершён");
        }
    }

    private async Task ReadHlsMetadataAsync(Uri streamUri, CancellationToken cancellationToken)
    {
        try
        {
            var playlistUri = await ResolveMediaPlaylistUriAsync(streamUri, cancellationToken);
            while (!cancellationToken.IsCancellationRequested)
            {
                var playlist = await GetPlaylistTextAsync(playlistUri, cancellationToken);
                var metadataLine = playlist.Split('\n')
                    .Select(line => line.Trim())
                    .FirstOrDefault(line => line.StartsWith("#EXTINF:", StringComparison.Ordinal));

                if (!string.IsNullOrWhiteSpace(metadataLine))
                {
                    await Dispatcher.InvokeAsync(() => AppendMetadata($"HLS metadata\n{metadataLine}"));
                    var title = ExtractHlsAttribute(metadataLine, "title");
                    var artist = ExtractHlsAttribute(metadataLine, "artist");
                    var track = string.IsNullOrWhiteSpace(artist) ? title : $"{artist} — {title}";
                    if (!string.IsNullOrWhiteSpace(track))
                        await Dispatcher.InvokeAsync(() => SetCurrentTrack(track));

                    var artwork = ExtractHlsArtworkUrl(metadataLine);
                    if (!string.IsNullOrWhiteSpace(artwork))
                        await Dispatcher.InvokeAsync(() => SetArtwork(artwork));
                    await Dispatcher.InvokeAsync(() => MetaStatusText.Text = "В эфире · HLS-метаданные");
                }

                await Task.Delay(TimeSpan.FromSeconds(8), cancellationToken);
            }
        }
        catch (OperationCanceledException)
        {
            // Switching or stopping a station intentionally ends the metadata reader.
        }
        catch (HttpRequestException)
        {
            await Dispatcher.InvokeAsync(() => MetaStatusText.Text = "HLS-метаданные недоступны");
        }
    }

    private static async Task<Uri> ResolveMediaPlaylistUriAsync(Uri streamUri, CancellationToken cancellationToken)
    {
        var masterPlaylist = await GetPlaylistTextAsync(streamUri, cancellationToken);
        var lines = masterPlaylist.Split('\n').Select(line => line.Trim()).ToArray();
        if (lines.Any(line => line.StartsWith("#EXTINF:", StringComparison.Ordinal))) return streamUri;

        for (var index = 0; index < lines.Length - 1; index++)
        {
            if (!lines[index].StartsWith("#EXT-X-STREAM-INF:", StringComparison.Ordinal)) continue;
            var childPlaylist = lines.Skip(index + 1).FirstOrDefault(line => !string.IsNullOrWhiteSpace(line) && !line.StartsWith('#'));
            if (!string.IsNullOrWhiteSpace(childPlaylist)) return new Uri(streamUri, childPlaylist);
        }
        return streamUri;
    }

    private static async Task<string> GetPlaylistTextAsync(Uri playlistUri, CancellationToken cancellationToken)
    {
        using var request = new HttpRequestMessage(HttpMethod.Get, playlistUri);
        request.Headers.TryAddWithoutValidation("User-Agent", "WebStream/1.0");
        using var response = await MetadataClient.SendAsync(request, HttpCompletionOption.ResponseContentRead, cancellationToken);
        response.EnsureSuccessStatusCode();
        return await response.Content.ReadAsStringAsync(cancellationToken);
    }

    private static string? ExtractHlsAttribute(string line, string attribute)
    {
        var match = Regex.Match(line, $@"\b{Regex.Escape(attribute)}=""(?<value>[^""]*)""", RegexOptions.IgnoreCase);
        return match.Success ? match.Groups["value"].Value : null;
    }

    private static string? ExtractHlsArtworkUrl(string line)
    {
        var match = Regex.Match(line, @"amgArtworkURL=\\?""(?<url>https?[^""\\]+)", RegexOptions.IgnoreCase);
        return match.Success ? match.Groups["url"].Value : null;
    }

    private static async Task ReadExactlyAsync(Stream stream, byte[] buffer, int bytesToRead, CancellationToken cancellationToken)
    {
        var offset = 0;
        while (offset < bytesToRead)
        {
            var read = await stream.ReadAsync(buffer.AsMemory(offset, Math.Min(buffer.Length - offset, bytesToRead - offset)), cancellationToken);
            if (read == 0) throw new EndOfStreamException();
            offset += read;
        }
    }

    private static async Task SkipExactlyAsync(Stream stream, byte[] buffer, int bytesToSkip, CancellationToken cancellationToken)
    {
        var remaining = bytesToSkip;
        while (remaining > 0)
        {
            var read = await stream.ReadAsync(buffer.AsMemory(0, Math.Min(buffer.Length, remaining)), cancellationToken);
            if (read == 0) throw new EndOfStreamException();
            remaining -= read;
        }
    }

    private static string? GetHeaderValue(HttpResponseMessage response, string name)
    {
        if (response.Headers.TryGetValues(name, out var values)) return values.FirstOrDefault();
        return response.Content.Headers.TryGetValues(name, out values) ? values.FirstOrDefault() : null;
    }

    private static string? ExtractStreamTitle(string metadata)
    {
        return ExtractMetadataValue(metadata, "StreamTitle");
    }

    private static string? ExtractCoverArtUrl(string metadata)
    {
        var candidates = new[] { "CoverArtUrl", "CoverArtURL", "AlbumArtUrl", "AlbumArtURL", "ArtworkUrl", "ArtworkURL", "ImageUrl", "ImageURL" };
        var coverUrl = candidates.Select(key => ExtractMetadataValue(metadata, key)).FirstOrDefault(value => !string.IsNullOrWhiteSpace(value));
        if (!string.IsNullOrWhiteSpace(coverUrl)) return coverUrl;

        var streamUrl = ExtractMetadataValue(metadata, "StreamUrl");
        return IsImageUrl(streamUrl) ? streamUrl : null;
    }

    private static string? ExtractMetadataValue(string metadata, string name)
    {
        foreach (var quote in new[] { '\'', '"' })
        {
            var prefix = $"{name}={quote}";
            var start = metadata.IndexOf(prefix, StringComparison.OrdinalIgnoreCase);
            if (start < 0) continue;
            start += prefix.Length;
            var end = metadata.IndexOf(quote, start);
            return (end < 0 ? metadata[start..] : metadata[start..end]).Trim('\0', ' ');
        }
        return null;
    }

    private static bool IsImageUrl(string? value)
    {
        if (!Uri.TryCreate(value, UriKind.Absolute, out var uri)) return false;
        var path = uri.AbsolutePath;
        return path.EndsWith(".jpg", StringComparison.OrdinalIgnoreCase)
            || path.EndsWith(".jpeg", StringComparison.OrdinalIgnoreCase)
            || path.EndsWith(".png", StringComparison.OrdinalIgnoreCase)
            || path.EndsWith(".webp", StringComparison.OrdinalIgnoreCase);
    }

    private void UpdateIcyHeaders(string? stationName, string? genre)
    {
        if (!string.IsNullOrWhiteSpace(stationName))
        {
            StationNameText.Text = stationName;
            MetaStationText.Text = stationName;
            ArtworkCaptionText.Text = stationName.ToUpperInvariant();
        }
        if (!string.IsNullOrWhiteSpace(genre)) MetaDescriptionText.Text = genre;
    }

    private void SetCurrentTrack(string title)
    {
        TrackText.Text = title;
        MetaStatusText.Text = "В эфире · метаданные обновлены";
        if (string.Equals(_lastArtworkQuery, title, StringComparison.Ordinal)) return;
        _lastArtworkQuery = title;
        _ = FindArtworkAsync(title, _metadataCancellation?.Token ?? CancellationToken.None);
    }

    private void AppendMetadata(string value)
    {
        var timestamp = DateTime.Now.ToString("HH:mm:ss");
        var entry = $"[{timestamp}] {value.Trim()}\n\n";
        MetadataLogText.AppendText(entry);
        if (MetadataLogText.Text.Length > 20_000)
            MetadataLogText.Text = MetadataLogText.Text[^15_000..];
        MetadataLogText.ScrollToEnd();
    }

    private async Task FindArtworkAsync(string trackTitle, CancellationToken cancellationToken)
    {
        try
        {
            var endpoint = $"https://itunes.apple.com/search?media=music&entity=song&limit=1&term={Uri.EscapeDataString(trackTitle)}";
            var json = await MetadataClient.GetStringAsync(endpoint, cancellationToken);
            using var document = JsonDocument.Parse(json);
            var result = document.RootElement.GetProperty("results").EnumerateArray().FirstOrDefault();
            if (result.ValueKind != JsonValueKind.Object || !result.TryGetProperty("artworkUrl100", out var artwork)) return;
            var coverUrl = artwork.GetString()?.Replace("100x100bb", "600x600bb", StringComparison.Ordinal);
            if (string.IsNullOrWhiteSpace(coverUrl) || cancellationToken.IsCancellationRequested || _hasStreamArtwork) return;
            SetArtwork(coverUrl, isStreamArtwork: false);
        }
        catch (OperationCanceledException)
        {
            // A new station or a new track superseded this lookup.
        }
        catch (HttpRequestException)
        {
            // Album art is an enhancement; playback does not depend on the lookup service.
        }
        catch (JsonException)
        {
            // Ignore an unexpected response from the public music catalog.
        }
    }

    private void SetArtwork(string coverUrl, bool isStreamArtwork = true)
    {
        if (!Uri.TryCreate(coverUrl, UriKind.Absolute, out var uri)) return;
        var image = new BitmapImage();
        image.BeginInit();
        image.UriSource = uri;
        image.CreateOptions = BitmapCreateOptions.IgnoreImageCache;
        image.EndInit();
        ArtworkImage.Source = image;
        ArtworkImage.Visibility = Visibility.Visible;
        ArtworkPlaceholder.Visibility = Visibility.Collapsed;
        if (isStreamArtwork) _hasStreamArtwork = true;
    }

    private void ResetArtwork()
    {
        ArtworkImage.Source = null;
        ArtworkImage.Visibility = Visibility.Collapsed;
        ArtworkPlaceholder.Visibility = Visibility.Visible;
        _hasStreamArtwork = false;
        _lastArtworkQuery = null;
    }

    private void ArtworkImage_ImageFailed(object sender, ExceptionRoutedEventArgs e)
    {
        ResetArtwork();
    }

    private void UpdateMetadata(string station, string description, string streamUrl, string status)
    {
        MetaStationText.Text = station;
        MetaDescriptionText.Text = description;
        MetaUrlText.Text = streamUrl;
        MetaStatusText.Text = status;
        ArtworkCaptionText.Text = station.ToUpperInvariant();
    }

    private void SaveCurrentStationToHistory()
    {
        var streamUrl = StreamUrlBox.Text.Trim();
        if (string.IsNullOrWhiteSpace(streamUrl)) return;

        var station = new RadioStation(StationNameText.Text, MetaDescriptionText.Text, streamUrl);
        var existing = _history.FirstOrDefault(item => string.Equals(item.StreamUrl, streamUrl, StringComparison.OrdinalIgnoreCase));
        if (existing is not null) _history.Remove(existing);
        _history.Insert(0, station);

        try
        {
            Directory.CreateDirectory(Path.GetDirectoryName(_historyPath)!);
            File.WriteAllText(_historyPath, JsonSerializer.Serialize(_history, new JsonSerializerOptions { WriteIndented = true }));
        }
        catch (IOException)
        {
            // The player remains usable even if local history is temporarily unavailable.
        }
    }

    private void LoadHistory()
    {
        try
        {
            if (!File.Exists(_historyPath)) return;
            var stations = JsonSerializer.Deserialize<List<RadioStation>>(File.ReadAllText(_historyPath));
            if (stations is null) return;
            foreach (var station in stations.Where(station => !string.IsNullOrWhiteSpace(station.StreamUrl)))
                _history.Add(station);
        }
        catch (JsonException)
        {
            // Ignore a malformed history file; playback should still start normally.
        }
        catch (IOException)
        {
            // Ignore unavailable local storage.
        }
    }
}

public sealed record RadioStation(string Name, string Description, string StreamUrl);
