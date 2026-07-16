using System.Collections.ObjectModel;
using System.ComponentModel;
using System.Diagnostics;
using System.IO;
using System.Net.Http;
using System.Text.Json;
using System.Text;
using System.Text.RegularExpressions;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Data;
using System.Windows.Interop;
using System.Windows.Media;
using System.Windows.Media.Imaging;
using System.Windows.Threading;
using NotifyIcon = System.Windows.Forms.NotifyIcon;
using ToolStripMenuItem = System.Windows.Forms.ToolStripMenuItem;
using Rectangle = System.Windows.Shapes.Rectangle;

namespace WebStream;

public partial class MainWindow : Window
{
    private const int MaxConcurrentSongRecordings = 3;
    private const int SignalBarCount = 48;
    private const double SignalReferenceFloor = 0.01;
    private const long AudioBufferBytes = 32L * 1024 * 1024;
    private static readonly TimeSpan MetadataTrackDelay = TimeSpan.FromSeconds(12);
    private static readonly HttpClient MetadataClient = new();
    private readonly AudioBackBuffer _audioBuffer = new(AudioBufferBytes);
    private readonly ObservableCollection<RadioStation> _stations = new();
    private readonly ObservableCollection<RadioStation> _history = new();
    private ICollectionView? _stationsView;
    private ICollectionView? _historyView;
    private readonly List<RadioStation> _urlHistory = new();
    private readonly List<ActiveSongRecording> _songRecordings = new();
    private readonly List<Rectangle> _signalBars = new();
    private readonly double[] _signalLevels = new double[SignalBarCount];
    private readonly DispatcherTimer _recordingIndicatorTimer = new();
    private readonly NotifyIcon _trayIcon = new();
    private ToolStripMenuItem? _trayShowItem;
    private ToolStripMenuItem? _trayExitItem;
    private readonly string _historyPath = Path.Combine(
        Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData), "WebStream", "history.json");
    private readonly string _playlistPath = Path.Combine(
        Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData), "WebStream", "playlist.json");
    private readonly string _statePath = Path.Combine(
        Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData), "WebStream", "state.json");
    private readonly string _recordingsPath = Path.Combine(
        Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData), "WebStream", "active-recordings.json");
    private readonly string _urlHistoryPath = Path.Combine(
        Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData), "WebStream", "url-history.json");
    private bool _isPlaying;
    private CancellationTokenSource? _metadataCancellation;
    private CancellationTokenSource? _pendingTrackCancellation;
    private CancellationTokenSource? _signalCancellation;
    private Process? _signalProcess;
    private double _signalReferenceLevel = SignalReferenceFloor;
    private int _historyNavigationIndex = -1;
    private bool _isNavigatingUrlHistory;
    private bool _isListFilterActive;
    private StationSearchWindow? _searchWindow;
    private Uri? _currentStreamUri;
    private long _currentTrackStartPosition;
    private DateTime _currentTrackStartedAt = DateTime.Now;
    private string? _currentTrackTitle;
    private string? _currentTrackKey;
    private string? _pendingTrackKey;
    private string? _currentArtworkUrl;
    private string? _lastArtworkQuery;
    private bool _hasStreamArtwork;
    private double _lastVolume = 0.3;
    private string? _trackTextLocalizationKey;
    private string? _statusLocalizationKey;
    private string? _metaStatusLocalizationKey;

    public MainWindow()
    {
        InitializeComponent();
        LocalizationManager.LanguageChanged += (_, _) =>
        {
            UpdateTrayLanguage();
            RefreshLocalizedRuntimeText();
            SetPlayerVolume(Player.Volume);
            UpdateRecordingIndicator();
        };
        LoadWindowIcon();
        InitializeTrayIcon();
        StateChanged += MainWindow_StateChanged;
        StationsList.ItemsSource = _stations;
        HistoryList.ItemsSource = _history;
        _stationsView = CollectionViewSource.GetDefaultView(_stations);
        _historyView = CollectionViewSource.GetDefaultView(_history);
        _stationsView.Filter = FilterRadioStation;
        _historyView.Filter = FilterRadioStation;
        SetPlayerVolume(VolumeSlider.Value / 100);
        InitializeSignalBars();
        UpdateRecordingIndicator();
        _recordingIndicatorTimer.Interval = TimeSpan.FromSeconds(1);
        _recordingIndicatorTimer.Tick += (_, _) =>
        {
            CleanupFinishedRecorders();
            UpdateRecordingTimers();
        };
        _recordingIndicatorTimer.Start();
        LoadPlaylist();
        LoadHistory();
        LoadUrlHistory();
        LoadActiveRecordings();
        LoadAppState();
        UpdateListFilterButton();
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

    private void HistoryList_MouseDoubleClick(object sender, System.Windows.Input.MouseButtonEventArgs e)
    {
        if (HistoryList.SelectedItem is not RadioStation station) return;
        StationsList.SelectedItem = null;
        SelectStation(station);
    }

    private void ListFilterTextBox_TextChanged(object sender, TextChangedEventArgs e)
    {
        RefreshListFilter();
    }

    private void ListFilterButton_Click(object sender, RoutedEventArgs e)
    {
        _isListFilterActive = !_isListFilterActive;
        UpdateListFilterButton();
        RefreshListFilter();
    }

    private bool FilterRadioStation(object item)
    {
        if (!_isListFilterActive) return true;
        if (item is not RadioStation station) return true;

        var filter = ListFilterTextBox?.Text.Trim();
        if (string.IsNullOrWhiteSpace(filter)) return true;

        return ContainsFilter(station.Name, filter)
            || ContainsFilter(station.Description, filter)
            || ContainsFilter(station.StreamUrl, filter)
            || ContainsFilter(station.PlayedAt?.ToString("dd.MM.yyyy HH:mm:ss"), filter);
    }

    private void RefreshListFilter()
    {
        _stationsView?.Refresh();
        _historyView?.Refresh();
    }

    private void UpdateListFilterButton()
    {
        ListFilterButton.BorderBrush = _isListFilterActive
            ? (System.Windows.Media.Brush)FindResource("Accent")
            : new SolidColorBrush(System.Windows.Media.Color.FromRgb(58, 64, 74));
        ListFilterButton.BorderThickness = _isListFilterActive ? new Thickness(2) : new Thickness(1);
        ListFilterIconPath.Opacity = _isListFilterActive ? 1 : 0.55;
        ListFilterClearPath.Opacity = _isListFilterActive ? 0 : 1;
    }

    private static bool ContainsFilter(string? value, string filter)
    {
        return !string.IsNullOrWhiteSpace(value)
            && value.Contains(filter, StringComparison.CurrentCultureIgnoreCase);
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

    private void SearchButton_Click(object sender, RoutedEventArgs e)
    {
        if (_searchWindow is not null)
        {
            _searchWindow.Activate();
            return;
        }

        _searchWindow = new StationSearchWindow(this)
        {
            Owner = this
        };
        _searchWindow.Closed += (_, _) => _searchWindow = null;
        _searchWindow.Show();
    }

    private void RecordedTracksButton_Click(object sender, RoutedEventArgs e)
    {
        StopPlaybackFromSearch();
        var window = new RecordedTracksWindow
        {
            WindowStartupLocation = WindowStartupLocation.Manual,
            Left = Left,
            Top = Top,
            Width = ActualWidth,
            Height = ActualHeight
        };
        Hide();
        try
        {
            window.ShowDialog();
        }
        finally
        {
            Show();
            Activate();
        }
    }

    private void LanguageButton_Click(object sender, RoutedEventArgs e)
    {
        LocalizationManager.ToggleLanguage();
    }

    private static string L(string key) => LocalizationManager.Get(key);

    private static string LF(string key, params object[] args) => LocalizationManager.Format(key, args);

    private void SetTrackKey(string key)
    {
        _trackTextLocalizationKey = key;
        TrackText.Text = L(key);
    }

    private void SetTrackText(string text)
    {
        _trackTextLocalizationKey = null;
        TrackText.Text = text;
    }

    private void SetStatusKey(string key)
    {
        _statusLocalizationKey = key;
        StatusText.Text = L(key);
    }

    private void SetMetaStatusKey(string key)
    {
        _metaStatusLocalizationKey = key;
        MetaStatusText.Text = L(key);
    }

    private void SetMetaStatusText(string text)
    {
        _metaStatusLocalizationKey = null;
        MetaStatusText.Text = text;
    }

    private void RefreshLocalizedRuntimeText()
    {
        if (_trackTextLocalizationKey is not null) TrackText.Text = L(_trackTextLocalizationKey);
        if (_statusLocalizationKey is not null) StatusText.Text = L(_statusLocalizationKey);
        if (_metaStatusLocalizationKey is not null) MetaStatusText.Text = L(_metaStatusLocalizationKey);
    }

    internal void PreviewSearchStation(SearchStationItem station)
    {
        ApplySearchStation(station);
        StartPlayback();
    }

    internal bool AddSearchStationToPlaylist(SearchStationItem station)
    {
        ApplySearchStation(station);
        var radioStation = new RadioStation(station.Name, station.Description, station.StreamUrl, DateTime.Now);
        if (_stations.Any(item => string.Equals(item.StreamUrl, station.StreamUrl, StringComparison.OrdinalIgnoreCase)))
            return false;

        _stations.Insert(0, radioStation);
        SavePlaylist();
        return true;
    }

    internal void PausePlaybackFromSearch()
    {
        if (!_isPlaying) return;
        Player.Pause();
        _isPlaying = false;
        PlayButton.Content = "▶";
        SetStatusKey("Paused");
        StopSignalAnalyzer();
        SaveAppState();
    }

    internal void StopPlaybackFromSearch()
    {
        Player.Stop();
        StopMetadataReader();
        StopSignalAnalyzer();
        _isPlaying = false;
        PlayButton.Content = "▶";
        SetStatusKey("Stopped");
        SetTrackKey("PlaybackStopped");
        SetMetaStatusKey("PlaybackStoppedStatus");
        SaveAppState();
    }

    internal void SetSearchPlaybackVolume(double value)
    {
        VolumeSlider.Value = Math.Clamp(value, 0, 100);
    }

    internal double CurrentPlaybackVolumePercent => Player.Volume * 100;

    private void ApplySearchStation(SearchStationItem station)
    {
        StreamUrlBox.Text = station.StreamUrl;
        StationNameText.Text = string.IsNullOrWhiteSpace(station.Name) ? L("MyStream") : station.Name;
        if (string.IsNullOrWhiteSpace(station.Description))
            SetTrackKey("ImportedFromSearch");
        else
            SetTrackText(station.Description);
        UpdateMetadata(StationNameText.Text, TrackText.Text, station.StreamUrl, "ImportedFromSearch");
    }

    private void AddHistoryItemToPlaylist_Click(object sender, RoutedEventArgs e)
    {
        if (!TryGetContextStation(sender, out var station)) return;
        if (_stations.Any(item => string.Equals(item.StreamUrl, station.StreamUrl, StringComparison.OrdinalIgnoreCase))) return;

        _stations.Insert(0, station);
        SavePlaylist();
    }

    private void ClearHistory_Click(object sender, RoutedEventArgs e)
    {
        _history.Clear();
        SaveHistory();
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
        SetTrackText(station.Description);
        StreamUrlBox.Text = station.StreamUrl;
        UpdateMetadata(station.Name, station.Description, station.StreamUrl, "ConnectToStation");
        StartPlayback();
    }

    private void PlayButton_Click(object sender, RoutedEventArgs e)
    {
        if (_isPlaying)
        {
            Player.Pause();
            _isPlaying = false;
            PlayButton.Content = "▶";
            SetStatusKey("Paused");
            StopSignalAnalyzer();
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
            SetStatusKey("CheckUrl");
            SetTrackKey("InvalidUrl");
            return;
        }

        if (StationsList.SelectedItem is null) StationNameText.Text = L("MyStream");
        SetStatusKey("Connecting");
        SetTrackKey("ConnectingToRadio");
        MetadataLogText.Clear();
        AppendMetadata($"{L("ConnectingLog")}\n{value}");
        UpdateMetadata(StationNameText.Text, MetaDescriptionText.Text == "—" ? L("UserStream") : MetaDescriptionText.Text, value, "ConnectToStation");
        RegisterUrlHistory(StationNameText.Text, MetaDescriptionText.Text, value);
        _audioBuffer.Reset();
        _currentStreamUri = uri;
        _currentTrackStartPosition = 0;
        _currentTrackStartedAt = DateTime.Now;
        _currentTrackTitle = null;
        _currentTrackKey = null;
        CancelPendingTrackUpdate();
        Player.Stop();
        ResetArtwork();
        _ = LoadStationArtworkAsync(uri, _metadataCancellation?.Token ?? CancellationToken.None);
        Player.Source = uri;
        Player.Play();
        StartSignalAnalyzer(uri);
        StartMetadataReader(uri);
        _isPlaying = true;
        PlayButton.Content = "Ⅱ";
        SaveAppState();
    }

    private void StopButton_Click(object sender, RoutedEventArgs e)
    {
        Player.Stop();
        StopMetadataReader();
        StopSignalAnalyzer();
        _isPlaying = false;
        PlayButton.Content = "▶";
        SetStatusKey("Stopped");
        SetTrackKey("PlaybackStopped");
        SetMetaStatusKey("PlaybackStoppedStatus");
        SaveAppState();
    }

    private void Player_MediaOpened(object sender, RoutedEventArgs e)
    {
        SetStatusKey("OnAir");
        if (string.IsNullOrWhiteSpace(_currentTrackTitle))
            SetTrackKey("StreamPlaying");
        SetMetaStatusKey("OnAirStatus");
        SaveCurrentStationToHistory();
    }

    private void Player_MediaFailed(object sender, ExceptionRoutedEventArgs e)
    {
        _isPlaying = false;
        PlayButton.Content = "▶";
        SetStatusKey("PlaybackError");
        SetTrackKey("PlaybackErrorText");
        SetMetaStatusKey("PlaybackErrorStatus");
        StopMetadataReader();
        StopSignalAnalyzer();
        SaveAppState();
    }

    private void MainWindow_StateChanged(object? sender, EventArgs e)
    {
        if (WindowState != WindowState.Minimized) return;
        HideToTray();
    }

    private void HideToTray()
    {
        ShowInTaskbar = false;
        Hide();
        _trayIcon.Visible = true;
    }

    private void RestoreFromTray()
    {
        Show();
        ShowInTaskbar = true;
        WindowState = WindowState.Normal;
        Activate();
        _trayIcon.Visible = false;
    }

    private void InitializeTrayIcon()
    {
        var iconPath = FindAssetPath("webstream.ico");
        if (!string.IsNullOrWhiteSpace(iconPath))
            _trayIcon.Icon = new System.Drawing.Icon(iconPath);

        _trayIcon.Text = "WebStream Radio";
        _trayIcon.Visible = false;
        _trayIcon.DoubleClick += (_, _) => Dispatcher.Invoke(RestoreFromTray);

        _trayShowItem = new ToolStripMenuItem(L("TrayShow"));
        _trayShowItem.Click += (_, _) => Dispatcher.Invoke(RestoreFromTray);
        _trayExitItem = new ToolStripMenuItem(L("TrayExit"));
        _trayExitItem.Click += (_, _) => Dispatcher.Invoke(Close);

        _trayIcon.ContextMenuStrip = new System.Windows.Forms.ContextMenuStrip();
        _trayIcon.ContextMenuStrip.Items.Add(_trayShowItem);
        _trayIcon.ContextMenuStrip.Items.Add(_trayExitItem);
    }

    private void UpdateTrayLanguage()
    {
        if (_trayShowItem is not null) _trayShowItem.Text = L("TrayShow");
        if (_trayExitItem is not null) _trayExitItem.Text = L("TrayExit");
    }

    private void LoadWindowIcon()
    {
        var iconPath = FindAssetPath("webstream.ico");
        if (string.IsNullOrWhiteSpace(iconPath)) return;

        using var icon = new System.Drawing.Icon(iconPath);
        Icon = Imaging.CreateBitmapSourceFromHIcon(
            icon.Handle,
            Int32Rect.Empty,
            BitmapSizeOptions.FromEmptyOptions());
    }

    private static string? FindAssetPath(string fileName)
    {
        var localCandidates = new[]
        {
            Path.Combine(AppContext.BaseDirectory, "Assets", fileName),
            Path.Combine(AppContext.BaseDirectory, fileName),
            Path.Combine(Environment.CurrentDirectory, "Assets", fileName)
        };

        foreach (var candidate in localCandidates)
            if (File.Exists(candidate))
                return candidate;

        var directory = new DirectoryInfo(AppContext.BaseDirectory);
        while (directory is not null)
        {
            var candidate = Path.Combine(directory.FullName, "Assets", fileName);
            if (File.Exists(candidate)) return candidate;
            directory = directory.Parent;
        }

        return null;
    }

    private void AddStation_Click(object sender, RoutedEventArgs e)
    {
        StreamUrlBox.Focus();
        StreamUrlBox.SelectAll();
        SetTrackKey("PasteUrlHint");
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

        VolumeSlider.Value = (_lastVolume > 0 ? _lastVolume : 0.3) * 100;
    }

    private void OpenSoundSettings_Click(object sender, RoutedEventArgs e)
    {
        OpenWindowsPanel("ms-settings:sound");
    }

    private void OpenSoundDevices_Click(object sender, RoutedEventArgs e)
    {
        OpenWindowsPanel("mmsys.cpl");
    }

    private void HistoryPreviousButton_Click(object sender, RoutedEventArgs e)
    {
        NavigateHistory(-1);
    }

    private void HistoryNextButton_Click(object sender, RoutedEventArgs e)
    {
        NavigateHistory(1);
    }

    private void NavigateHistory(int direction)
    {
        if (_urlHistory.Count == 0) return;

        var currentUrl = StreamUrlBox.Text.Trim();
        if (_historyNavigationIndex < 0 || _historyNavigationIndex >= _urlHistory.Count)
            _historyNavigationIndex = _urlHistory.FindLastIndex(item => string.Equals(item.StreamUrl, currentUrl, StringComparison.OrdinalIgnoreCase));
        if (_historyNavigationIndex < 0) _historyNavigationIndex = _urlHistory.Count - 1;

        var nextIndex = Math.Clamp(_historyNavigationIndex + direction, 0, _urlHistory.Count - 1);
        if (nextIndex == _historyNavigationIndex && string.Equals(_urlHistory[nextIndex].StreamUrl, currentUrl, StringComparison.OrdinalIgnoreCase))
            return;

        _historyNavigationIndex = nextIndex;
        _isNavigatingUrlHistory = true;
        try
        {
            SelectStation(_urlHistory[_historyNavigationIndex]);
        }
        finally
        {
            _isNavigatingUrlHistory = false;
        }
    }

    private void RecordButton_Click(object sender, RoutedEventArgs e)
    {
        CaptureCurrentSong();
    }

    private void CaptureCurrentSong()
    {
        if (_currentStreamUri is null || (_currentStreamUri.Scheme != Uri.UriSchemeHttp && _currentStreamUri.Scheme != Uri.UriSchemeHttps))
        {
            SetStatusKey("CheckUrl");
            SetMetaStatusKey("NeedHttpStream");
            return;
        }

        if (_currentStreamUri.AbsolutePath.EndsWith(".m3u8", StringComparison.OrdinalIgnoreCase))
        {
            SetStatusKey("NotMp3");
            SetMetaStatusKey("HlsCannotRecord");
            return;
        }

        CleanupFinishedRecorders();
        var seedBytes = _audioBuffer.SnapshotFrom(_currentTrackStartPosition);
        var directUrlRecording = seedBytes.Length == 0;
        var recordingKey = directUrlRecording ? BuildUrlRecordingKey(_currentStreamUri) : BuildRecordingKey();
        if (!string.IsNullOrWhiteSpace(recordingKey)
            && _songRecordings.Any(recording => !recording.IsCompleted && string.Equals(recording.TrackKey, recordingKey, StringComparison.OrdinalIgnoreCase)))
        {
            SetStatusKey("AlreadyRecording");
            SetMetaStatusKey("TrackAlreadyRecording");
            _ = WindowsNotifier.ShowAsync(L("TrackAlreadyRecording"), BuildRecordingNotificationText());
            return;
        }

        if (_songRecordings.Count(recording => !recording.IsCompleted) >= MaxConcurrentSongRecordings)
        {
            SetStatusKey("Limit");
            SetMetaStatusKey("ThreeRecordingsLimit");
            return;
        }

        MakeRoomForRecordingSlot();

        var recordingsFolder = Path.Combine(
            Environment.GetFolderPath(Environment.SpecialFolder.MyMusic),
            "WebStream");
        Directory.CreateDirectory(recordingsFolder);

        var tempFolder = Path.Combine(Path.GetTempPath(), "WebStream");
        Directory.CreateDirectory(tempFolder);
        var seedPath = Path.Combine(tempFolder, $"{Guid.NewGuid():N}.mp3seed");
        if (!directUrlRecording)
            File.WriteAllBytes(seedPath, seedBytes);

        var outputPath = Path.Combine(recordingsFolder, directUrlRecording ? BuildUrlRecordingFileName(_currentStreamUri) : BuildRecordingFileName());
        var title = directUrlRecording ? BuildUrlRecordingTitle(_currentStreamUri) : _currentTrackTitle ?? string.Empty;
        var process = StartSongRecorder(_currentStreamUri, seedPath, outputPath, title, directUrlRecording ? null : _currentArtworkUrl, directUrlRecording);
        if (process is null)
        {
            TryDeleteFile(seedPath);
            SetStatusKey("PlaybackError");
            SetMetaStatusKey("PlaybackErrorStatus");
            return;
        }

        var recordingTitle = directUrlRecording ? title : BuildRecordingNotificationText();
        var timerOffset = directUrlRecording ? TimeSpan.Zero : DateTime.Now - _currentTrackStartedAt;
        if (timerOffset < TimeSpan.Zero) timerOffset = TimeSpan.Zero;
        var now = DateTime.Now;
        _songRecordings.Add(new ActiveSongRecording(
            recordingKey,
            process,
            recordingTitle,
            MetaStationText.Text,
            MetaDescriptionText.Text,
            _currentStreamUri.ToString(),
            outputPath,
            now,
            now - timerOffset));
        UpdateRecordingIndicator();
        SaveActiveRecordings();
        SetStatusKey("Recording");
        SetMetaStatusText(LF("SavingSongFile", Path.GetFileName(outputPath)));
        AppendMetadata($"{L("StartRecordingLog")}\n{recordingTitle}\n{Path.GetFileName(outputPath)}{(directUrlRecording ? $"\n{L("DirectUrlMode")}" : string.Empty)}");
        _ = WindowsNotifier.ShowAsync(L("SongRecordingNotification"), recordingTitle);
    }

    private Process? StartSongRecorder(Uri streamUri, string seedPath, string outputPath, string title, string? artworkUrl, bool directUrlRecording)
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
        if (directUrlRecording)
            startInfo.ArgumentList.Add("--record-url-direct");
        if (!string.IsNullOrWhiteSpace(artworkUrl))
        {
            startInfo.ArgumentList.Add("--artwork");
            startInfo.ArgumentList.Add(artworkUrl);
        }

        return Process.Start(startInfo);
    }

    private void CleanupFinishedRecorders()
    {
        var completed = _songRecordings.Where(recording => !recording.IsCompleted && IsRecordingFinished(recording)).ToList();
        foreach (var recording in completed)
        {
            MarkRecordingCompleted(recording);
            var duration = recording.ProcessDuration ?? DateTime.Now - recording.StartedAt;
            AppendMetadata($"{L("StopRecordingLog")}\n{recording.Title}\n{Path.GetFileName(recording.OutputPath)}\n{LF("ProcessDuration", duration.ToString(@"mm\:ss"))}");
        }

        if (completed.Count > 0)
        {
            UpdateRecordingIndicator();
            SaveActiveRecordings();
        }
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
        var count = Math.Clamp(_songRecordings.Count(recording => !recording.IsCompleted), 0, MaxConcurrentSongRecordings);
        RecordStatusText.Text = $"{count}/{MaxConcurrentSongRecordings}";
        RecordStatusDot.Fill = count switch
        {
            0 => System.Windows.Media.Brushes.White,
            1 => System.Windows.Media.Brushes.DodgerBlue,
            2 => System.Windows.Media.Brushes.LimeGreen,
            _ => System.Windows.Media.Brushes.Red
        };
        RecordButton.ToolTip = BuildRecordingToolTip();
        UpdateRecordingTimers();
    }

    private void UpdateRecordingTimers()
    {
        var timerTexts = new[] { RecordingTimer1Text, RecordingTimer2Text, RecordingTimer3Text };
        var timerSlots = new[] { RecordingTimer1Slot, RecordingTimer2Slot, RecordingTimer3Slot };
        for (var i = 0; i < timerTexts.Length; i++)
        {
            if (i >= _songRecordings.Count)
            {
                timerTexts[i].Text = string.Empty;
                timerTexts[i].Foreground = new SolidColorBrush(System.Windows.Media.Color.FromRgb(221, 236, 207));
                timerSlots[i].ToolTip = string.Empty;
                continue;
            }

            var recording = _songRecordings[i];
            var duration = recording.CompletedDisplayDuration ?? DateTime.Now - recording.TimerStartedAt;
            timerTexts[i].Text = FormatRecordingDuration(duration);
            timerTexts[i].Foreground = recording.IsCompleted
                ? new SolidColorBrush(System.Windows.Media.Color.FromRgb(82, 88, 96))
                : new SolidColorBrush(System.Windows.Media.Color.FromRgb(221, 236, 207));
            timerSlots[i].ToolTip = Path.GetFileName(recording.OutputPath);
        }
    }

    private void StopRecordingTimerMenu_Click(object sender, RoutedEventArgs e)
    {
        if (!TryGetRecordingFromTimerMenu(sender, out var recording)) return;
        StopRecording(recording, deleteFile: false);
    }

    private void DeleteRecordingTimerMenu_Click(object sender, RoutedEventArgs e)
    {
        if (!TryGetRecordingFromTimerMenu(sender, out var recording)) return;
        StopRecording(recording, deleteFile: true);
    }

    private void RecordingTimerSlot_LeftClick(object sender, System.Windows.Input.MouseButtonEventArgs e)
    {
        if (!TryGetRecordingFromTimerSlot(sender, out var recording)) return;
        StationNameText.Text = recording.Station;
        SetTrackText(recording.Title);
        StreamUrlBox.Text = recording.StreamUrl;
        UpdateMetadata(recording.Station, recording.Description, recording.StreamUrl, "SwitchToRecordingChannel");
        StartPlayback();
    }

    private bool TryGetRecordingFromTimerMenu(object sender, out ActiveSongRecording recording)
    {
        recording = default!;
        if (sender is not System.Windows.Controls.MenuItem menuItem
            || menuItem.Parent is not System.Windows.Controls.ContextMenu contextMenu
            || contextMenu.PlacementTarget is not FrameworkElement { Tag: string tagText }
            || !int.TryParse(tagText, out var index)
            || index < 0
            || index >= _songRecordings.Count)
            return false;

        recording = _songRecordings[index];
        return true;
    }

    private bool TryGetRecordingFromTimerSlot(object sender, out ActiveSongRecording recording)
    {
        recording = default!;
        if (sender is not FrameworkElement { Tag: string tagText }
            || !int.TryParse(tagText, out var index)
            || index < 0
            || index >= _songRecordings.Count)
            return false;

        recording = _songRecordings[index];
        return true;
    }

    private void StopRecording(ActiveSongRecording recording, bool deleteFile)
    {
        TryKillRecordingProcess(recording);
        MarkRecordingCompleted(recording);

        if (deleteFile)
        {
            TryDeleteFile(recording.OutputPath);
            _songRecordings.Remove(recording);
            AppendMetadata($"{L("RecordingDeletedLog")}\n{recording.Title}\n{Path.GetFileName(recording.OutputPath)}");
        }
        else
        {
            AppendMetadata($"{L("RecordingStoppedLog")}\n{recording.Title}\n{Path.GetFileName(recording.OutputPath)}");
        }

        UpdateRecordingIndicator();
        SaveActiveRecordings();
    }

    private static void TryKillRecordingProcess(ActiveSongRecording recording)
    {
        try
        {
            if (!recording.Process.HasExited)
                recording.Process.Kill(entireProcessTree: true);
        }
        catch (InvalidOperationException)
        {
        }
        catch (System.ComponentModel.Win32Exception)
        {
        }
    }

    private static void MarkRecordingCompleted(ActiveSongRecording recording)
    {
        if (recording.IsCompleted) return;
        var now = DateTime.Now;
        recording.IsCompleted = true;
        recording.CompletedAt = now;
        recording.ProcessDuration = now - recording.StartedAt;
        recording.CompletedDisplayDuration = now - recording.TimerStartedAt;
    }

    private void MakeRoomForRecordingSlot()
    {
        if (_songRecordings.Count < MaxConcurrentSongRecordings) return;
        var completed = _songRecordings.FirstOrDefault(recording => recording.IsCompleted);
        if (completed is not null)
            _songRecordings.Remove(completed);
        SaveActiveRecordings();
    }

    private static string FormatRecordingDuration(TimeSpan duration)
    {
        if (duration < TimeSpan.Zero) duration = TimeSpan.Zero;
        return $"{(int)duration.TotalHours:00}:{duration.Minutes:00}:{duration.Seconds:00}";
    }

    private string BuildRecordingToolTip()
    {
        if (_songRecordings.Count == 0)
            return L("RecordingToolTipIdle");

        var lines = _songRecordings.Select(recording =>
            $"{recording.StartedAt:HH:mm:ss}  {recording.Station} — {recording.Title}");
        return LF("RecordingToolTipActive", _songRecordings.Count, MaxConcurrentSongRecordings) + "\n" + string.Join("\n", lines);
    }

    private string BuildRecordingKey()
    {
        var title = !string.IsNullOrWhiteSpace(_currentTrackTitle)
            ? _currentTrackTitle
            : TrackText.Text;
        return BuildSongCompareKey(title);
    }

    private static string BuildUrlRecordingKey(Uri streamUri)
    {
        return $"URL:{streamUri.Host}:{streamUri.AbsolutePath}";
    }

    private string BuildRecordingFileName()
    {
        var title = !string.IsNullOrWhiteSpace(_currentTrackTitle)
            ? _currentTrackTitle
            : string.IsNullOrWhiteSpace(StationNameText.Text) || StationNameText.Text == L("SelectStation")
            ? "WebStream"
            : StationNameText.Text;
        var safeName = string.Join("_", title.Split(Path.GetInvalidFileNameChars(), StringSplitOptions.RemoveEmptyEntries)).Trim();
        safeName = Regex.Replace(safeName, @"\s+", " ");
        if (safeName.Length > 120) safeName = safeName[..120].Trim();
        if (string.IsNullOrWhiteSpace(safeName)) safeName = "WebStream";
        return $"{safeName}_{DateTime.Now:yyyyMMdd_HHmmss}.mp3";
    }

    private static string BuildUrlRecordingFileName(Uri streamUri)
    {
        var host = string.IsNullOrWhiteSpace(streamUri.Host) ? "stream" : streamUri.Host;
        var safeHost = string.Join("_", host.Split(Path.GetInvalidFileNameChars(), StringSplitOptions.RemoveEmptyEntries)).Trim();
        if (string.IsNullOrWhiteSpace(safeHost)) safeHost = "stream";
        return $"{safeHost}_{DateTime.Now:yyyyMMdd_HHmmss}.mp3";
    }

    private static string BuildUrlRecordingTitle(Uri streamUri)
    {
        return string.IsNullOrWhiteSpace(streamUri.Host) ? L("NoMetadataStreamTitle") : streamUri.Host;
    }

    private string BuildRecordingNotificationText()
    {
        if (!string.IsNullOrWhiteSpace(_currentTrackTitle)) return _currentTrackTitle;
        if (!string.IsNullOrWhiteSpace(TrackText.Text) && TrackText.Text != L("StreamPlaying")) return TrackText.Text;
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
        MuteButton.Content = volume <= 0 ? L("MuteOff") : L("MuteOn");
    }

    protected override void OnClosed(EventArgs e)
    {
        _recordingIndicatorTimer.Stop();
        SaveAppState();
        SaveActiveRecordings();
        StopMetadataReader();
        StopSignalAnalyzer();
        _trayIcon.Visible = false;
        _trayIcon.Icon?.Dispose();
        _trayIcon.ContextMenuStrip?.Dispose();
        _trayIcon.Dispose();
        base.OnClosed(e);
    }

    private void InitializeSignalBars()
    {
        SignalBarsHost.Children.Clear();
        _signalBars.Clear();

        for (var i = 0; i < SignalBarCount; i++)
        {
            var bar = new Rectangle
            {
                Height = 2,
                MinHeight = 2,
                Width = 4,
                Margin = new Thickness(1, 0, 1, 0),
                VerticalAlignment = VerticalAlignment.Bottom,
                Fill = new SolidColorBrush(System.Windows.Media.Color.FromRgb(68, 83, 98))
            };
            _signalBars.Add(bar);
            SignalBarsHost.Children.Add(bar);
        }
    }

    private void StartSignalAnalyzer(Uri streamUri)
    {
        StopSignalAnalyzer();
        ResetSignalBars();

        var ffmpegPath = FindFfmpegPath();
        if (ffmpegPath is null)
        {
            AppendMetadata($"{L("SignalLevel")}\n{L("FfmpegNotFound")}");
            return;
        }

        _signalCancellation = new CancellationTokenSource();
        _ = RunSignalAnalyzerAsync(ffmpegPath, streamUri, _signalCancellation.Token);
    }

    private void StopSignalAnalyzer()
    {
        _signalCancellation?.Cancel();
        _signalCancellation?.Dispose();
        _signalCancellation = null;

        try
        {
            if (_signalProcess is { HasExited: false })
                _signalProcess.Kill(entireProcessTree: true);
        }
        catch (InvalidOperationException)
        {
        }
        catch (System.ComponentModel.Win32Exception)
        {
        }

        _signalProcess = null;
        ResetSignalBars();
    }

    private async Task RunSignalAnalyzerAsync(string ffmpegPath, Uri streamUri, CancellationToken cancellationToken)
    {
        Process? process = null;
        try
        {
            var audioUri = await StreamPlaylistResolver.ResolveAsync(streamUri, cancellationToken);
            var startInfo = new ProcessStartInfo(ffmpegPath)
            {
                UseShellExecute = false,
                CreateNoWindow = true,
                RedirectStandardOutput = true,
                RedirectStandardError = true
            };
            startInfo.ArgumentList.Add("-hide_banner");
            startInfo.ArgumentList.Add("-loglevel");
            startInfo.ArgumentList.Add("error");
            startInfo.ArgumentList.Add("-i");
            startInfo.ArgumentList.Add(audioUri.ToString());
            startInfo.ArgumentList.Add("-vn");
            startInfo.ArgumentList.Add("-f");
            startInfo.ArgumentList.Add("s16le");
            startInfo.ArgumentList.Add("-ac");
            startInfo.ArgumentList.Add("1");
            startInfo.ArgumentList.Add("-ar");
            startInfo.ArgumentList.Add("8000");
            startInfo.ArgumentList.Add("pipe:1");

            process = Process.Start(startInfo);
            if (process is null) return;
            _signalProcess = process;
            _ = process.StandardError.ReadToEndAsync(cancellationToken);

            var buffer = new byte[4096];
            while (!cancellationToken.IsCancellationRequested)
            {
                var bytesRead = await process.StandardOutput.BaseStream.ReadAsync(buffer.AsMemory(0, buffer.Length), cancellationToken);
                if (bytesRead <= 0) break;
                var level = CalculatePcmRms(buffer, bytesRead);
                await Dispatcher.InvokeAsync(() => PushSignalLevel(level), DispatcherPriority.Background, cancellationToken);
            }
        }
        catch (OperationCanceledException)
        {
        }
        catch (IOException)
        {
        }
        catch (InvalidOperationException)
        {
        }
        finally
        {
            try
            {
                if (process is { HasExited: false })
                    process.Kill(entireProcessTree: true);
            }
            catch (InvalidOperationException)
            {
            }
            catch (System.ComponentModel.Win32Exception)
            {
            }
            finally
            {
                process?.Dispose();
            }

            if (ReferenceEquals(_signalProcess, process))
                _signalProcess = null;
        }
    }

    private static double CalculatePcmRms(byte[] buffer, int bytesRead)
    {
        var sampleCount = bytesRead / 2;
        if (sampleCount <= 0) return 0;

        double sumSquares = 0;
        for (var i = 0; i < sampleCount * 2; i += 2)
        {
            var sample = BitConverter.ToInt16(buffer, i) / 32768.0;
            sumSquares += sample * sample;
        }

        var rms = Math.Sqrt(sumSquares / sampleCount);
        return Math.Clamp(rms, 0, 1);
    }

    private void PushSignalLevel(double rawLevel)
    {
        _signalReferenceLevel = rawLevel > _signalReferenceLevel
            ? (_signalReferenceLevel * 0.75) + (rawLevel * 0.25)
            : (_signalReferenceLevel * 0.995) + (rawLevel * 0.005);
        _signalReferenceLevel = Math.Max(_signalReferenceLevel, SignalReferenceFloor);

        var visualLevel = rawLevel < 0.0008
            ? 0
            : Math.Clamp(rawLevel / (_signalReferenceLevel * 1.7), 0, 1);

        Array.Copy(_signalLevels, 1, _signalLevels, 0, _signalLevels.Length - 1);
        _signalLevels[^1] = visualLevel;
        RenderSignalBars();
    }

    private void ResetSignalBars()
    {
        Array.Clear(_signalLevels);
        _signalReferenceLevel = SignalReferenceFloor;
        RenderSignalBars();
    }

    private void RenderSignalBars()
    {
        for (var i = 0; i < _signalBars.Count; i++)
        {
            var level = Math.Clamp(_signalLevels[i], 0, 1);
            var bar = _signalBars[i];
            bar.Height = 2 + level * 20;
            bar.Fill = level switch
            {
                > 0.8 => System.Windows.Media.Brushes.OrangeRed,
                > 0.55 => System.Windows.Media.Brushes.Gold,
                > 0.18 => (System.Windows.Media.Brush)FindResource("Accent"),
                _ => new SolidColorBrush(System.Windows.Media.Color.FromRgb(68, 83, 98))
            };
        }
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
        CancelPendingTrackUpdate();
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
                    await Dispatcher.InvokeAsync(() => ScheduleCurrentTrack(title));
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
            await Dispatcher.InvokeAsync(() => SetMetaStatusKey("NoIcyMetadata"));
        }
        catch (EndOfStreamException)
        {
            await Dispatcher.InvokeAsync(() => SetMetaStatusKey("MetadataStreamEnded"));
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
                        await Dispatcher.InvokeAsync(() => ScheduleCurrentTrack(track));

                    var artwork = ExtractHlsArtworkUrl(metadataLine);
                    if (!string.IsNullOrWhiteSpace(artwork))
                        await Dispatcher.InvokeAsync(() => SetArtwork(artwork));
                    await Dispatcher.InvokeAsync(() => SetMetaStatusKey("HlsMetadataOnAir"));
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
            await Dispatcher.InvokeAsync(() => SetMetaStatusKey("HlsMetadataUnavailable"));
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
        if (changed)
        {
            SaveCurrentStationToHistory();
            UpdateCurrentUrlHistoryMetadata();
        }
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
            _currentTrackStartedAt = DateTime.Now;
        }

        SetTrackText(title);
        SetMetaStatusKey("MetadataUpdated");
        if (string.Equals(_lastArtworkQuery, title, StringComparison.Ordinal)) return;
        _lastArtworkQuery = title;
        _ = FindArtworkAsync(title, _metadataCancellation?.Token ?? CancellationToken.None);
    }

    private void ScheduleCurrentTrack(string title)
    {
        var normalizedTitle = NormalizeTitle(title);
        var songKey = BuildSongCompareKey(normalizedTitle);
        if (string.IsNullOrWhiteSpace(normalizedTitle) || string.IsNullOrWhiteSpace(songKey)) return;

        if (string.Equals(_currentTrackKey, songKey, StringComparison.OrdinalIgnoreCase)
            || string.Equals(_pendingTrackKey, songKey, StringComparison.OrdinalIgnoreCase))
            return;

        CancelPendingTrackUpdate();
        _pendingTrackKey = songKey;
        _pendingTrackCancellation = new CancellationTokenSource();
        var token = _pendingTrackCancellation.Token;
        _ = ApplyDelayedTrackAsync(normalizedTitle, token);
        SetMetaStatusText(LF("MetadataWillApply", MetadataTrackDelay.TotalSeconds.ToString("0")));
    }

    private async Task ApplyDelayedTrackAsync(string title, CancellationToken cancellationToken)
    {
        try
        {
            await Task.Delay(MetadataTrackDelay, cancellationToken);
            await Dispatcher.InvokeAsync(() =>
            {
                if (cancellationToken.IsCancellationRequested) return;
                SetCurrentTrack(title);
                _pendingTrackKey = null;
                _pendingTrackCancellation?.Dispose();
                _pendingTrackCancellation = null;
            });
        }
        catch (OperationCanceledException)
        {
        }
    }

    private void CancelPendingTrackUpdate()
    {
        _pendingTrackCancellation?.Cancel();
        _pendingTrackCancellation?.Dispose();
        _pendingTrackCancellation = null;
        _pendingTrackKey = null;
    }

    private void UpdateCurrentUrlHistoryMetadata()
    {
        var streamUrl = StreamUrlBox.Text.Trim();
        if (string.IsNullOrWhiteSpace(streamUrl) || _urlHistory.Count == 0) return;

        var index = _historyNavigationIndex >= 0 && _historyNavigationIndex < _urlHistory.Count
            && string.Equals(_urlHistory[_historyNavigationIndex].StreamUrl, streamUrl, StringComparison.OrdinalIgnoreCase)
            ? _historyNavigationIndex
            : _urlHistory.FindLastIndex(item => string.Equals(item.StreamUrl, streamUrl, StringComparison.OrdinalIgnoreCase));

        if (index < 0) return;
        _urlHistory[index] = new RadioStation(StationNameText.Text, MetaDescriptionText.Text, streamUrl, DateTime.Now);
        SaveUrlHistory();
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

    private async Task LoadStationArtworkAsync(Uri streamUri, CancellationToken cancellationToken)
    {
        var candidates = BuildStationArtworkCandidates(streamUri).ToList();
        candidates.InsertRange(0, await FindRadioBrowserStationArtworkAsync(streamUri, cancellationToken));
        if (candidates.Count == 0) return;

        using var request = new HttpRequestMessage(HttpMethod.Get, new Uri(streamUri.GetLeftPart(UriPartial.Authority)));
        request.Headers.TryAddWithoutValidation("User-Agent", "WebStream/1.0");

        try
        {
            using var response = await MetadataClient.SendAsync(request, HttpCompletionOption.ResponseContentRead, cancellationToken);
            if (response.IsSuccessStatusCode)
            {
                var html = await response.Content.ReadAsStringAsync(cancellationToken);
                candidates.InsertRange(0, ExtractIconLinks(html, response.RequestMessage?.RequestUri ?? streamUri));
            }
        }
        catch (OperationCanceledException)
        {
            return;
        }
        catch (HttpRequestException)
        {
        }
        catch (IOException)
        {
        }

        foreach (var candidate in candidates.DistinctBy(uri => uri.ToString(), StringComparer.OrdinalIgnoreCase))
        {
            if (cancellationToken.IsCancellationRequested
                || _currentArtworkUrl is not null
                || !Equals(_currentStreamUri, streamUri))
                return;
            if (!await IsReachableImageAsync(candidate, cancellationToken)) continue;

            await Dispatcher.InvokeAsync(() =>
            {
                if (!cancellationToken.IsCancellationRequested
                    && _currentArtworkUrl is null
                    && Equals(_currentStreamUri, streamUri))
                    SetArtwork(candidate.ToString(), isStreamArtwork: false);
            });
            return;
        }
    }

    private static IEnumerable<Uri> BuildStationArtworkCandidates(Uri streamUri)
    {
        var root = new Uri(streamUri.GetLeftPart(UriPartial.Authority));
        yield return new Uri(root, "/favicon.ico");
        yield return new Uri(root, "/favicon.png");
        yield return new Uri(root, "/apple-touch-icon.png");
    }

    private static async Task<List<Uri>> FindRadioBrowserStationArtworkAsync(Uri streamUri, CancellationToken cancellationToken)
    {
        var candidates = new List<Uri>();
        try
        {
            var endpoint = $"https://de1.api.radio-browser.info/json/stations/byurl?url={Uri.EscapeDataString(streamUri.ToString())}";
            using var request = new HttpRequestMessage(HttpMethod.Get, endpoint);
            request.Headers.TryAddWithoutValidation("User-Agent", "WebStream/1.0");
            using var response = await MetadataClient.SendAsync(request, HttpCompletionOption.ResponseContentRead, cancellationToken);
            if (!response.IsSuccessStatusCode) return candidates;

            await using var stream = await response.Content.ReadAsStreamAsync(cancellationToken);
            using var document = await JsonDocument.ParseAsync(stream, cancellationToken: cancellationToken);
            foreach (var station in document.RootElement.EnumerateArray())
            {
                AddCandidate(candidates, station, "favicon");
                if (station.TryGetProperty("homepage", out var homepage)
                    && Uri.TryCreate(homepage.GetString(), UriKind.Absolute, out var homepageUri))
                    candidates.AddRange(BuildStationArtworkCandidates(homepageUri));
            }
        }
        catch (OperationCanceledException)
        {
            throw;
        }
        catch (HttpRequestException)
        {
        }
        catch (JsonException)
        {
        }

        return candidates;
    }

    private static void AddCandidate(List<Uri> candidates, JsonElement element, string propertyName)
    {
        if (!element.TryGetProperty(propertyName, out var property)) return;
        if (Uri.TryCreate(property.GetString(), UriKind.Absolute, out var uri))
            candidates.Add(uri);
    }

    private static IEnumerable<Uri> ExtractIconLinks(string html, Uri baseUri)
    {
        foreach (Match match in Regex.Matches(html, @"<link\b[^>]*>", RegexOptions.IgnoreCase))
        {
            var tag = match.Value;
            var rel = ExtractHtmlAttribute(tag, "rel");
            if (rel is null || !rel.Contains("icon", StringComparison.OrdinalIgnoreCase)) continue;

            var href = ExtractHtmlAttribute(tag, "href");
            if (string.IsNullOrWhiteSpace(href)) continue;
            if (Uri.TryCreate(baseUri, href, out var iconUri))
                yield return iconUri;
        }
    }

    private static string? ExtractHtmlAttribute(string tag, string attribute)
    {
        var match = Regex.Match(tag, $@"\b{Regex.Escape(attribute)}\s*=\s*[""'](?<value>[^""']+)[""']", RegexOptions.IgnoreCase);
        return match.Success ? match.Groups["value"].Value : null;
    }

    private static async Task<bool> IsReachableImageAsync(Uri uri, CancellationToken cancellationToken)
    {
        try
        {
            using var request = new HttpRequestMessage(HttpMethod.Head, uri);
            request.Headers.TryAddWithoutValidation("User-Agent", "WebStream/1.0");
            using var response = await MetadataClient.SendAsync(request, HttpCompletionOption.ResponseHeadersRead, cancellationToken);
            return response.IsSuccessStatusCode && IsImageContentType(response.Content.Headers.ContentType?.MediaType);
        }
        catch (OperationCanceledException)
        {
            throw;
        }
        catch (HttpRequestException)
        {
            return false;
        }
    }

    private static bool IsImageContentType(string? mediaType)
    {
        return mediaType?.StartsWith("image/", StringComparison.OrdinalIgnoreCase) == true;
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

    private void UpdateMetadata(string station, string description, string streamUrl, string statusKey)
    {
        MetaStationText.Text = station;
        MetaDescriptionText.Text = description;
        MetaUrlText.Text = streamUrl;
        SetMetaStatusKey(statusKey);
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
        SaveHistory();
    }

    private void SaveHistory()
    {
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

    private void RegisterUrlHistory(string stationName, string description, string streamUrl)
    {
        if (_isNavigatingUrlHistory || string.IsNullOrWhiteSpace(streamUrl)) return;

        if (_historyNavigationIndex >= 0 && _historyNavigationIndex < _urlHistory.Count - 1)
            _urlHistory.RemoveRange(_historyNavigationIndex + 1, _urlHistory.Count - _historyNavigationIndex - 1);

        if (_urlHistory.Count > 0 && string.Equals(_urlHistory[^1].StreamUrl, streamUrl, StringComparison.OrdinalIgnoreCase))
        {
            _urlHistory[^1] = new RadioStation(stationName, description, streamUrl, DateTime.Now);
            _historyNavigationIndex = _urlHistory.Count - 1;
            SaveUrlHistory();
            return;
        }

        _urlHistory.Add(new RadioStation(stationName, description, streamUrl, DateTime.Now));
        if (_urlHistory.Count > 100)
            _urlHistory.RemoveRange(0, _urlHistory.Count - 100);
        _historyNavigationIndex = _urlHistory.Count - 1;
        SaveUrlHistory();
    }

    private void SaveUrlHistory()
    {
        try
        {
            Directory.CreateDirectory(Path.GetDirectoryName(_urlHistoryPath)!);
            File.WriteAllText(_urlHistoryPath, JsonSerializer.Serialize(_urlHistory, new JsonSerializerOptions { WriteIndented = true }));
        }
        catch (IOException)
        {
        }
        catch (UnauthorizedAccessException)
        {
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
                StationNameText.Text = L("MyStream");
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

    private void LoadActiveRecordings()
    {
        try
        {
            if (!File.Exists(_recordingsPath)) return;
            var states = JsonSerializer.Deserialize<List<ActiveRecordingState>>(File.ReadAllText(_recordingsPath));
            if (states is null) return;

            foreach (var state in states.Take(MaxConcurrentSongRecordings))
            {
                if (!TryRestoreRecordingProcess(state, out var process)) continue;
                _songRecordings.Add(new ActiveSongRecording(
                    state.TrackKey,
                    process,
                    state.Title,
                    state.Station,
                    state.Description,
                    state.StreamUrl,
                    state.OutputPath,
                    state.StartedAt,
                    state.TimerStartedAt));
            }

            if (_songRecordings.Count > 0)
            {
                AppendMetadata($"{L("RestoredRecordings")}\n{LF("ActiveProcesses", _songRecordings.Count)}");
                UpdateRecordingIndicator();
            }
            SaveActiveRecordings();
        }
        catch (JsonException)
        {
            SaveActiveRecordings();
        }
        catch (IOException)
        {
        }
        catch (UnauthorizedAccessException)
        {
        }
    }

    private static bool TryRestoreRecordingProcess(ActiveRecordingState state, out Process process)
    {
        process = default!;
        try
        {
            var candidate = Process.GetProcessById(state.ProcessId);
            if (candidate.HasExited)
            {
                candidate.Dispose();
                return false;
            }

            var startDelta = (candidate.StartTime - state.ProcessStartedAt).Duration();
            if (startDelta > TimeSpan.FromSeconds(5))
            {
                candidate.Dispose();
                return false;
            }

            process = candidate;
            return true;
        }
        catch (ArgumentException)
        {
            return false;
        }
        catch (InvalidOperationException)
        {
            return false;
        }
        catch (System.ComponentModel.Win32Exception)
        {
            return false;
        }
    }

    private void SaveActiveRecordings()
    {
        try
        {
            var states = _songRecordings
                .Where(recording => !recording.IsCompleted && IsRecordingStillRunning(recording))
                .Select(recording => new ActiveRecordingState(
                    recording.TrackKey,
                    recording.Process.Id,
                    GetProcessStartTime(recording.Process),
                    recording.Title,
                    recording.Station,
                    recording.Description,
                    recording.StreamUrl,
                    recording.OutputPath,
                    recording.StartedAt,
                    recording.TimerStartedAt))
                .ToList();

            Directory.CreateDirectory(Path.GetDirectoryName(_recordingsPath)!);
            File.WriteAllText(_recordingsPath, JsonSerializer.Serialize(states, new JsonSerializerOptions { WriteIndented = true }));
        }
        catch (IOException)
        {
        }
        catch (UnauthorizedAccessException)
        {
        }
        catch (InvalidOperationException)
        {
        }
        catch (System.ComponentModel.Win32Exception)
        {
        }
    }

    private static bool IsRecordingStillRunning(ActiveSongRecording recording)
    {
        try
        {
            return !recording.Process.HasExited;
        }
        catch (InvalidOperationException)
        {
            return false;
        }
    }

    private static DateTime GetProcessStartTime(Process process)
    {
        try
        {
            return process.StartTime;
        }
        catch (InvalidOperationException)
        {
            return DateTime.MinValue;
        }
        catch (System.ComponentModel.Win32Exception)
        {
            return DateTime.MinValue;
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

    private void LoadUrlHistory()
    {
        try
        {
            if (File.Exists(_urlHistoryPath))
            {
                var stations = JsonSerializer.Deserialize<List<RadioStation>>(File.ReadAllText(_urlHistoryPath));
                if (stations is not null)
                    _urlHistory.AddRange(stations.Where(station => !string.IsNullOrWhiteSpace(station.StreamUrl)).TakeLast(100));
            }
            else if (_history.Count > 0)
            {
                _urlHistory.AddRange(_history.Reverse().TakeLast(100));
            }

            _historyNavigationIndex = _urlHistory.Count - 1;
        }
        catch (JsonException)
        {
        }
        catch (IOException)
        {
        }
        catch (UnauthorizedAccessException)
        {
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

public sealed record ActiveRecordingState(
    string TrackKey,
    int ProcessId,
    DateTime ProcessStartedAt,
    string Title,
    string Station,
    string Description,
    string StreamUrl,
    string OutputPath,
    DateTime StartedAt,
    DateTime TimerStartedAt);

public sealed class ActiveSongRecording(
    string trackKey,
    Process process,
    string title,
    string station,
    string description,
    string streamUrl,
    string outputPath,
    DateTime startedAt,
    DateTime timerStartedAt)
{
    public string TrackKey { get; } = trackKey;
    public Process Process { get; } = process;
    public string Title { get; } = title;
    public string Station { get; } = station;
    public string Description { get; } = description;
    public string StreamUrl { get; } = streamUrl;
    public string OutputPath { get; } = outputPath;
    public DateTime StartedAt { get; } = startedAt;
    public DateTime TimerStartedAt { get; } = timerStartedAt;
    public bool IsCompleted { get; set; }
    public DateTime? CompletedAt { get; set; }
    public TimeSpan? ProcessDuration { get; set; }
    public TimeSpan? CompletedDisplayDuration { get; set; }
}

public sealed record AppState(string? StreamUrl, bool WasPlaying);
