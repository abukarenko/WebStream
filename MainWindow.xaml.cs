using System.Collections.ObjectModel;
using System.Diagnostics;
using System.IO;
using System.Net.Http;
using System.Text.Json;
using System.Text;
using System.Text.RegularExpressions;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Media;
using System.Windows.Media.Imaging;
using System.Windows.Threading;

namespace WebStream;

public partial class MainWindow : Window
{
    private const int MaxConcurrentSongRecordings = 3;
    private const long AudioBufferBytes = 32L * 1024 * 1024;
    private static readonly HttpClient MetadataClient = new();
    private readonly AudioBackBuffer _audioBuffer = new(AudioBufferBytes);
    private readonly ObservableCollection<RadioStation> _stations = new();
    private readonly ObservableCollection<RadioStation> _history = new();
    private readonly List<ActiveSongRecording> _songRecordings = new();
    private readonly DispatcherTimer _recordingIndicatorTimer = new();
    private readonly string _historyPath = Path.Combine(
        Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData), "WebStream", "history.json");
    private readonly string _playlistPath = Path.Combine(
        Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData), "WebStream", "playlist.json");
    private readonly string _statePath = Path.Combine(
        Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData), "WebStream", "state.json");
    private bool _isPlaying;
    private CancellationTokenSource? _metadataCancellation;
    private Uri? _currentStreamUri;
    private long _currentTrackStartPosition;
    private string? _currentTrackTitle;
    private string? _currentTrackKey;
    private string? _currentArtworkUrl;
    private string? _lastArtworkQuery;
    private bool _hasStreamArtwork;
    private double _lastVolume = 0.3;

    public MainWindow()
    {
        InitializeComponent();
        StationsList.ItemsSource = _stations;
        HistoryList.ItemsSource = _history;
        SetPlayerVolume(VolumeSlider.Value / 100);
        UpdateRecordingIndicator();
        _recordingIndicatorTimer.Interval = TimeSpan.FromSeconds(2);
        _recordingIndicatorTimer.Tick += (_, _) => CleanupFinishedRecorders();
        _recordingIndicatorTimer.Start();
        LoadPlaylist();
        LoadHistory();
        LoadAppState();
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

    private void ShowHistoryPanel_Click(object sender, RoutedEventArgs e)
    {
        HistoryPanel.Visibility = Visibility.Visible;
        PlaylistPanel.Visibility = Visibility.Collapsed;
        HistoryPanelButton.Background = (System.Windows.Media.Brush)FindResource("Accent");
        HistoryPanelButton.Foreground = System.Windows.Media.Brushes.Black;
        PlaylistPanelButton.ClearValue(BackgroundProperty);
        PlaylistPanelButton.ClearValue(ForegroundProperty);
    }

    private void ShowPlaylistPanel_Click(object sender, RoutedEventArgs e)
    {
        HistoryPanel.Visibility = Visibility.Collapsed;
        PlaylistPanel.Visibility = Visibility.Visible;
        PlaylistPanelButton.Background = (System.Windows.Media.Brush)FindResource("Accent");
        PlaylistPanelButton.Foreground = System.Windows.Media.Brushes.Black;
        HistoryPanelButton.ClearValue(BackgroundProperty);
        HistoryPanelButton.ClearValue(ForegroundProperty);
    }

    private void AddHistoryItemToPlaylist_Click(object sender, RoutedEventArgs e)
    {
        if (!TryGetContextStation(sender, out var station)) return;
        if (_stations.Any(item => string.Equals(item.StreamUrl, station.StreamUrl, StringComparison.OrdinalIgnoreCase))) return;

        _stations.Insert(0, station);
        SavePlaylist();
    }

    private void RemovePlaylistItem_Click(object sender, RoutedEventArgs e)
    {
        if (!TryGetContextStation(sender, out var station)) return;
        _stations.Remove(station);
        SavePlaylist();
    }

    private static bool TryGetContextStation(object sender, out RadioStation station)
    {
        station = default!;
        if (sender is not System.Windows.Controls.MenuItem { DataContext: RadioStation contextStation }) return false;
        station = contextStation;
        return true;
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
            SaveAppState();
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
        _audioBuffer.Reset();
        _currentStreamUri = uri;
        _currentTrackStartPosition = 0;
        _currentTrackTitle = null;
        _currentTrackKey = null;
        Player.Stop();
        ResetArtwork();
        Player.Source = uri;
        Player.Play();
        StartMetadataReader(uri);
        _isPlaying = true;
        PlayButton.Content = "Ⅱ  Пауза";
        SaveAppState();
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
        SaveAppState();
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
        SaveAppState();
    }

    private void AddStation_Click(object sender, RoutedEventArgs e)
    {
        StreamUrlBox.Focus();
        StreamUrlBox.SelectAll();
        TrackText.Text = "Вставьте ссылку на поток в нижнее поле и нажмите «Слушать».";
    }

    private void PasteReplacementUrl_Click(object sender, RoutedEventArgs e)
    {
        if (!System.Windows.Clipboard.ContainsText()) return;
        StreamUrlBox.Text = System.Windows.Clipboard.GetText().Trim();
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

    private void VolumeSlider_ValueChanged(object sender, RoutedPropertyChangedEventArgs<double> e)
    {
        if (!IsLoaded) return;
        var volume = Math.Clamp(e.NewValue / 100, 0, 1);
        SetPlayerVolume(volume);
        if (volume > 0) _lastVolume = volume;
    }

    private void MuteButton_Click(object sender, RoutedEventArgs e)
    {
        if (Player.Volume > 0)
        {
            _lastVolume = Player.Volume;
            VolumeSlider.Value = 0;
            return;
        }

        VolumeSlider.Value = Math.Max(_lastVolume, 0.3) * 100;
    }

    private void OpenSoundSettings_Click(object sender, RoutedEventArgs e)
    {
        OpenWindowsPanel("ms-settings:sound");
    }

    private void OpenSoundDevices_Click(object sender, RoutedEventArgs e)
    {
        OpenWindowsPanel("mmsys.cpl");
    }

    private void RecordButton_Click(object sender, RoutedEventArgs e)
    {
        CaptureCurrentSong();
    }

    private void CaptureCurrentSong()
    {
        if (_currentStreamUri is null || (_currentStreamUri.Scheme != Uri.UriSchemeHttp && _currentStreamUri.Scheme != Uri.UriSchemeHttps))
        {
            StatusText.Text = "ПРОВЕРЬТЕ URL";
            MetaStatusText.Text = "Нужна ссылка HTTP(S) на MP3-поток";
            return;
        }

        if (_currentStreamUri.AbsolutePath.EndsWith(".m3u8", StringComparison.OrdinalIgnoreCase))
        {
            StatusText.Text = "НЕ MP3";
            MetaStatusText.Text = "HLS-плейлист нельзя сохранить как MP3 без перекодирования";
            return;
        }

        CleanupFinishedRecorders();
        var recordingKey = BuildRecordingKey();
        if (!string.IsNullOrWhiteSpace(recordingKey)
            && _songRecordings.Any(recording => string.Equals(recording.TrackKey, recordingKey, StringComparison.OrdinalIgnoreCase)))
        {
            StatusText.Text = "УЖЕ ПИШЕТСЯ";
            MetaStatusText.Text = "Трек уже сохраняется";
            _ = WindowsNotifier.ShowAsync("Трек уже сохраняется", BuildRecordingNotificationText());
            return;
        }

        if (_songRecordings.Count >= MaxConcurrentSongRecordings)
        {
            StatusText.Text = "ЛИМИТ";
            MetaStatusText.Text = "Уже запущены 3 фоновые записи";
            return;
        }

        var seedBytes = _audioBuffer.SnapshotFrom(_currentTrackStartPosition);
        if (seedBytes.Length == 0)
        {
            StatusText.Text = "БУФЕР ПУСТ";
            MetaStatusText.Text = "Поток еще не накопил MP3-данные для записи";
            return;
        }

        var recordingsFolder = Path.Combine(
            Environment.GetFolderPath(Environment.SpecialFolder.MyMusic),
            "WebStream");
        Directory.CreateDirectory(recordingsFolder);

        var tempFolder = Path.Combine(Path.GetTempPath(), "WebStream");
        Directory.CreateDirectory(tempFolder);
        var seedPath = Path.Combine(tempFolder, $"{Guid.NewGuid():N}.mp3seed");
        File.WriteAllBytes(seedPath, seedBytes);

        var outputPath = Path.Combine(recordingsFolder, BuildRecordingFileName());
        var process = StartSongRecorder(_currentStreamUri, seedPath, outputPath, _currentTrackTitle ?? string.Empty, _currentArtworkUrl);
        if (process is null)
        {
            TryDeleteFile(seedPath);
            StatusText.Text = "ОШИБКА";
            MetaStatusText.Text = "Не удалось запустить фоновую запись";
            return;
        }

        var recordingTitle = BuildRecordingNotificationText();
        _songRecordings.Add(new ActiveSongRecording(recordingKey, process, recordingTitle, MetaStationText.Text, outputPath, DateTime.Now));
        UpdateRecordingIndicator();
        StatusText.Text = "ЗАПИСЬ";
        MetaStatusText.Text = $"Сохраняю песню: {Path.GetFileName(outputPath)}";
        AppendMetadata($"СТАРТ ЗАПИСИ\n{recordingTitle}\n{Path.GetFileName(outputPath)}");
        _ = WindowsNotifier.ShowAsync("Запись песни", recordingTitle);
    }

    private Process? StartSongRecorder(Uri streamUri, string seedPath, string outputPath, string title, string? artworkUrl)
    {
        var appExePath = Path.Combine(AppContext.BaseDirectory, "WebStream.exe");
        var runsFromAppHost = File.Exists(appExePath);
        var executablePath = runsFromAppHost ? appExePath : Environment.ProcessPath;
        if (string.IsNullOrWhiteSpace(executablePath)) return null;

        var startInfo = new ProcessStartInfo(executablePath)
        {
            UseShellExecute = false,
            CreateNoWindow = true
        };
        if (!runsFromAppHost)
            startInfo.ArgumentList.Add(typeof(App).Assembly.Location);

        startInfo.ArgumentList.Add("--record-song");
        startInfo.ArgumentList.Add("--url");
        startInfo.ArgumentList.Add(streamUri.ToString());
        startInfo.ArgumentList.Add("--seed");
        startInfo.ArgumentList.Add(seedPath);
        startInfo.ArgumentList.Add("--output");
        startInfo.ArgumentList.Add(outputPath);
        startInfo.ArgumentList.Add("--title");
        startInfo.ArgumentList.Add(title);
        if (!string.IsNullOrWhiteSpace(artworkUrl))
        {
            startInfo.ArgumentList.Add("--artwork");
            startInfo.ArgumentList.Add(artworkUrl);
        }

        return Process.Start(startInfo);
    }

    private void CleanupFinishedRecorders()
    {
        var completed = _songRecordings.Where(IsRecordingFinished).ToList();
        foreach (var recording in completed)
        {
            var duration = DateTime.Now - recording.StartedAt;
            AppendMetadata($"СТОП ЗАПИСИ\n{recording.Title}\n{Path.GetFileName(recording.OutputPath)}\nДлительность процесса: {duration:mm\\:ss}");
        }

        var removed = _songRecordings.RemoveAll(recording => completed.Contains(recording));
        if (removed > 0) UpdateRecordingIndicator();
    }

    private static bool IsRecordingFinished(ActiveSongRecording recording)
    {
        try
        {
            return recording.Process.HasExited;
        }
        catch (InvalidOperationException)
        {
            return true;
        }
    }

    private void UpdateRecordingIndicator()
    {
        var count = Math.Clamp(_songRecordings.Count, 0, MaxConcurrentSongRecordings);
        RecordStatusText.Text = $"{count}/{MaxConcurrentSongRecordings}";
        RecordStatusDot.Fill = count switch
        {
            0 => System.Windows.Media.Brushes.White,
            1 => System.Windows.Media.Brushes.DodgerBlue,
            2 => System.Windows.Media.Brushes.LimeGreen,
            _ => System.Windows.Media.Brushes.Red
        };
        RecordButton.ToolTip = BuildRecordingToolTip();
    }

    private string BuildRecordingToolTip()
    {
        if (_songRecordings.Count == 0)
            return "Сохранить текущую песню из MP3-буфера";

        var lines = _songRecordings.Select(recording =>
            $"{recording.StartedAt:HH:mm:ss}  {recording.Station} — {recording.Title}");
        return $"Сейчас сохраняется: {_songRecordings.Count}/{MaxConcurrentSongRecordings}\n" + string.Join("\n", lines);
    }

    private string BuildRecordingKey()
    {
        var title = !string.IsNullOrWhiteSpace(_currentTrackTitle)
            ? _currentTrackTitle
            : TrackText.Text;
        return BuildSongCompareKey(title);
    }

    private string BuildRecordingFileName()
    {
        var title = !string.IsNullOrWhiteSpace(_currentTrackTitle)
            ? _currentTrackTitle
            : string.IsNullOrWhiteSpace(StationNameText.Text) || StationNameText.Text == "Выберите станцию"
            ? "WebStream"
            : StationNameText.Text;
        var safeName = string.Join("_", title.Split(Path.GetInvalidFileNameChars(), StringSplitOptions.RemoveEmptyEntries)).Trim();
        safeName = Regex.Replace(safeName, @"\s+", " ");
        if (safeName.Length > 120) safeName = safeName[..120].Trim();
        if (string.IsNullOrWhiteSpace(safeName)) safeName = "WebStream";
        return $"{safeName}_{DateTime.Now:yyyyMMdd_HHmmss}.mp3";
    }

    private string BuildRecordingNotificationText()
    {
        if (!string.IsNullOrWhiteSpace(_currentTrackTitle)) return _currentTrackTitle;
        if (!string.IsNullOrWhiteSpace(TrackText.Text) && TrackText.Text != "Поток воспроизводится.") return TrackText.Text;
        return StationNameText.Text;
    }

    private static void TryDeleteFile(string path)
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

    private static void OpenWindowsPanel(string target)
    {
        Process.Start(new ProcessStartInfo(target) { UseShellExecute = true });
    }

    private void SetPlayerVolume(double volume)
    {
        Player.Volume = volume;
        VolumeValueText.Text = Math.Round(volume * 100).ToString("0");
        VolumeIconText.Text = volume <= 0 ? "🔇" : "🔊";
        MuteButton.Content = volume <= 0 ? "Вкл." : "Выкл.";
    }

    protected override void OnClosed(EventArgs e)
    {
        _recordingIndicatorTimer.Stop();
        SaveAppState();
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
                await ReadAudioToBufferAsync(stream, buffer, interval, cancellationToken);
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

    private async Task ReadAudioToBufferAsync(Stream stream, byte[] buffer, int bytesToRead, CancellationToken cancellationToken)
    {
        var remaining = bytesToRead;
        while (remaining > 0)
        {
            var read = await stream.ReadAsync(buffer.AsMemory(0, Math.Min(buffer.Length, remaining)), cancellationToken);
            if (read == 0) throw new EndOfStreamException();
            _audioBuffer.Append(buffer.AsSpan(0, read));
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
        var changed = false;
        if (!string.IsNullOrWhiteSpace(stationName))
        {
            StationNameText.Text = stationName;
            MetaStationText.Text = stationName;
            ArtworkCaptionText.Text = stationName.ToUpperInvariant();
            changed = true;
        }
        if (!string.IsNullOrWhiteSpace(genre))
        {
            MetaDescriptionText.Text = genre;
            changed = true;
        }
        if (changed) SaveCurrentStationToHistory();
    }

    private void SetCurrentTrack(string title)
    {
        var normalizedTitle = NormalizeTitle(title);
        var songKey = BuildSongCompareKey(title);
        if (!string.IsNullOrWhiteSpace(normalizedTitle)
            && !string.Equals(_currentTrackKey, songKey, StringComparison.OrdinalIgnoreCase))
        {
            _currentTrackTitle = normalizedTitle;
            _currentTrackKey = songKey;
            _currentTrackStartPosition = _audioBuffer.CurrentPosition;
        }

        TrackText.Text = title;
        MetaStatusText.Text = "В эфире · метаданные обновлены";
        if (string.Equals(_lastArtworkQuery, title, StringComparison.Ordinal)) return;
        _lastArtworkQuery = title;
        _ = FindArtworkAsync(title, _metadataCancellation?.Token ?? CancellationToken.None);
    }

    private static string NormalizeTitle(string title)
    {
        return Regex.Replace(title, @"\s+", " ").Trim();
    }

    private static string BuildSongCompareKey(string? title)
    {
        var normalized = NormalizeTitle(title ?? string.Empty).ToUpperInvariant();
        normalized = Regex.Replace(normalized, @"\s+[-–—]\s+(OFFICIAL|RADIO|LIVE|HD|HQ|STEREO|MONO|REMIX|VERSION|EDIT|VIDEO).*$", "", RegexOptions.IgnoreCase);
        normalized = Regex.Replace(normalized, @"\s*\((OFFICIAL|RADIO|LIVE|HD|HQ|STEREO|MONO|REMIX|VERSION|EDIT|VIDEO)[^)]*\)", "", RegexOptions.IgnoreCase);
        normalized = Regex.Replace(normalized, @"\s*\[(OFFICIAL|RADIO|LIVE|HD|HQ|STEREO|MONO|REMIX|VERSION|EDIT|VIDEO)[^\]]*\]", "", RegexOptions.IgnoreCase);
        normalized = Regex.Replace(normalized, @"[^\p{L}\p{N}]+", " ");
        return Regex.Replace(normalized, @"\s+", " ").Trim();
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
        _currentArtworkUrl = coverUrl;
        if (isStreamArtwork) _hasStreamArtwork = true;
    }

    private void ResetArtwork()
    {
        ArtworkImage.Source = null;
        ArtworkImage.Visibility = Visibility.Collapsed;
        ArtworkPlaceholder.Visibility = Visibility.Visible;
        _currentArtworkUrl = null;
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

        var station = new RadioStation(StationNameText.Text, MetaDescriptionText.Text, streamUrl, DateTime.Now);
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

    private void LoadAppState()
    {
        try
        {
            if (!File.Exists(_statePath)) return;
            var state = JsonSerializer.Deserialize<AppState>(File.ReadAllText(_statePath));
            if (state is null || string.IsNullOrWhiteSpace(state.StreamUrl)) return;

            StreamUrlBox.Text = state.StreamUrl;
            if (state.WasPlaying)
            {
                StationNameText.Text = "Мой поток";
                StartPlayback();
            }
        }
        catch (JsonException)
        {
        }
        catch (IOException)
        {
        }
    }

    private void SaveAppState()
    {
        try
        {
            var streamUrl = StreamUrlBox.Text.Trim();
            Directory.CreateDirectory(Path.GetDirectoryName(_statePath)!);
            File.WriteAllText(_statePath, JsonSerializer.Serialize(new AppState(streamUrl, _isPlaying), new JsonSerializerOptions { WriteIndented = true }));
        }
        catch (IOException)
        {
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

    private void SavePlaylist()
    {
        try
        {
            Directory.CreateDirectory(Path.GetDirectoryName(_playlistPath)!);
            File.WriteAllText(_playlistPath, JsonSerializer.Serialize(_stations, new JsonSerializerOptions { WriteIndented = true }));
        }
        catch (IOException)
        {
            // The current session can still use the playlist even if it cannot be saved.
        }
    }

    private void LoadPlaylist()
    {
        try
        {
            if (File.Exists(_playlistPath))
            {
                var stations = JsonSerializer.Deserialize<List<RadioStation>>(File.ReadAllText(_playlistPath));
                if (stations is not null)
                {
                    foreach (var station in stations.Where(station => !string.IsNullOrWhiteSpace(station.StreamUrl)))
                        _stations.Add(station);
                }
            }
        }
        catch (JsonException)
        {
            _stations.Clear();
        }
        catch (IOException)
        {
            _stations.Clear();
        }

        if (_stations.Count > 0) return;
        AddBuiltInStations();
        SavePlaylist();
    }
}

public sealed record RadioStation(string Name, string Description, string StreamUrl, DateTime? PlayedAt = null);

public sealed record ActiveSongRecording(string TrackKey, Process Process, string Title, string Station, string OutputPath, DateTime StartedAt);

public sealed record AppState(string? StreamUrl, bool WasPlaying);
