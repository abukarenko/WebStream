using System.Collections.ObjectModel;
using System.Collections.Specialized;
using System.ComponentModel;
using System.Diagnostics;
using System.IO;
using System.Net.Http;
using System.Text.Json;
using System.Text.Json.Serialization;
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

namespace WebStream;

public partial class MainWindow : Window
{
    private enum PlaybackOrigin { Manual, Search, History, Playlist, UrlNavigation, Recording, Restored }

    private readonly record struct SongRatingIdentity(string Key, string Artist, string Title, string Genre);

    private const int MaxConcurrentSongRecordings = 3;
    private const int SignalBarCount = SpectrumDisplay.BarCount;
    private const int SpectrumFftSize = 2048;
    private const int SpectrumHopSize = 512;
    private const int SpectrumSampleRate = 22050;
    private const double SpectrumMinFrequency = 45;
    private const double SpectrumMaxFrequency = 10000;
    private static readonly SolidColorBrush ArtworkFrameIdleBrush = new(System.Windows.Media.Color.FromRgb(43, 49, 58));
    private static readonly SolidColorBrush ArtworkFrameMetadataBrush = new(System.Windows.Media.Color.FromRgb(128, 136, 146));
    private static readonly SolidColorBrush ArtworkFrameReadyBrush = new(System.Windows.Media.Color.FromRgb(112, 224, 170));
    private static readonly SolidColorBrush UrlRatingOnBrush = new(System.Windows.Media.Color.FromRgb(255, 196, 0));
    private static readonly SolidColorBrush UrlRatingOffBrush = new(System.Windows.Media.Color.FromRgb(81, 71, 19));
    private static readonly SolidColorBrush SongRatingOnBrush = new(System.Windows.Media.Color.FromRgb(24, 215, 126));
    private static readonly SolidColorBrush SongRatingOffBrush = new(System.Windows.Media.Color.FromRgb(36, 69, 54));
    private const long AudioBufferBytes = 32L * 1024 * 1024;
    private static readonly TimeSpan MetadataTrackDelay = TimeSpan.FromSeconds(12);
    private static readonly HttpClient MetadataClient = new();
    private readonly AudioBackBuffer _audioBuffer = new(AudioBufferBytes);
    private readonly ObservableCollection<RadioStation> _stations = new();
    private readonly ObservableCollection<RadioStation> _history = new();
    private readonly Dictionary<string, int> _urlRatings = new(StringComparer.OrdinalIgnoreCase);
    private readonly Dictionary<string, SongRatingEntry> _songRatings = new(StringComparer.OrdinalIgnoreCase);
    private ICollectionView? _stationsView;
    private ICollectionView? _historyView;
    private readonly List<RadioStation> _urlHistory = new();
    private readonly List<ActiveSongRecording> _songRecordings = new();
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
    private readonly string _urlRatingsPath = Path.Combine(
        Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData), "WebStream", "url-ratings.json");
    private readonly string _songRatingsPath = Path.Combine(
        Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData), "WebStream", "song-ratings.json");
    private bool _isPlaying;
    private CancellationTokenSource? _metadataCancellation;
    private CancellationTokenSource? _pendingTrackCancellation;
    private CancellationTokenSource? _signalCancellation;
    private Process? _signalProcess;
    private int _historyNavigationIndex = -1;
    private bool _isNavigatingUrlHistory;
    private string? _historyWritableStreamUrl;
    private string? _listIndicatorUrl;
    private bool _positionIndicatorUsesHistory = true;
    private bool _isListFilterActive;
    private StationSearchWindow? _searchWindow;
    private Uri? _currentStreamUri;
    private long _currentTrackStartPosition;
    private DateTime _currentTrackStartedAt = DateTime.Now;
    private string? _currentTrackTitle;
    private string? _currentTrackKey;
    private string? _pendingTrackKey;
    private int? _currentMetadataSongRating;
    private int? _pendingTrackMetadataRating;
    private string? _currentArtworkUrl;
    private string? _lastArtworkQuery;
    private bool _hasStreamArtwork;
    private bool _isTrackStartLocked;
    private bool _hasObservedTrackMetadata;
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
        _stations.CollectionChanged += StationCollection_CollectionChanged;
        _history.CollectionChanged += StationCollection_CollectionChanged;
        _stationsView = CollectionViewSource.GetDefaultView(_stations);
        _historyView = CollectionViewSource.GetDefaultView(_history);
        _stationsView.Filter = FilterRadioStation;
        _historyView.Filter = FilterRadioStation;
        SetPlayerVolume(VolumeSlider.Value / 100);
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
        LoadUrlRatings();
        RefreshAllUrlRatingStars();
        LoadSongRatings();
        LoadAppState();
        UpdateUrlRatingDisplay();
        UpdateSongRatingDisplay();
        UpdateListPositionIndicator();
        UpdateListFilterButton();
    }

    private void AddBuiltInStations()
    {
        _stations.Add(new RadioStation("SomaFM Groove Salad", "Ambient · Demo", "https://ice1.somafm.com/groovesalad-128-mp3"));
        _stations.Add(new RadioStation("SomaFM Drone Zone", "Ambient · Demo", "https://ice1.somafm.com/dronezone-128-mp3"));
    }

    private void StationsList_PreviewMouseLeftButtonDown(object sender, System.Windows.Input.MouseButtonEventArgs e)
    {
        e.Handled = true;
        if (TryGetClickedStation(StationsList, e.OriginalSource) is not { } station) return;
        SetListIndicatorUrl(station.StreamUrl);
        if (e.ClickCount != 2) return;
        SelectStation(station, PlaybackOrigin.Playlist);
    }

    private void HistoryList_PreviewMouseLeftButtonDown(object sender, System.Windows.Input.MouseButtonEventArgs e)
    {
        e.Handled = true;
        if (TryGetClickedStation(HistoryList, e.OriginalSource) is not { } station) return;
        SetListIndicatorUrl(station.StreamUrl);
        if (e.ClickCount != 2) return;
        SelectStation(station, PlaybackOrigin.History);
    }

    private static RadioStation? TryGetClickedStation(System.Windows.Controls.ListBox listBox, object originalSource)
    {
        if (originalSource is not DependencyObject source) return null;
        return ItemsControl.ContainerFromElement(listBox, source) is System.Windows.Controls.ListBoxItem item
            ? item.DataContext as RadioStation
            : null;
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
        UpdateListPositionIndicator();
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
        _positionIndicatorUsesHistory = true;
        _listIndicatorUrl = StreamUrlBox.Text.Trim();
        HistoryPanel.Visibility = Visibility.Visible;
        PlaylistPanel.Visibility = Visibility.Collapsed;
        HistoryPanelButton.Background = (System.Windows.Media.Brush)FindResource("Accent");
        HistoryPanelButton.Foreground = System.Windows.Media.Brushes.Black;
        PlaylistPanelButton.ClearValue(BackgroundProperty);
        PlaylistPanelButton.ClearValue(ForegroundProperty);
        UpdateListPositionIndicator();
    }

    private void ShowPlaylistPanel_Click(object sender, RoutedEventArgs e)
    {
        _positionIndicatorUsesHistory = false;
        _listIndicatorUrl = StreamUrlBox.Text.Trim();
        HistoryPanel.Visibility = Visibility.Collapsed;
        PlaylistPanel.Visibility = Visibility.Visible;
        PlaylistPanelButton.Background = (System.Windows.Media.Brush)FindResource("Accent");
        PlaylistPanelButton.Foreground = System.Windows.Media.Brushes.Black;
        HistoryPanelButton.ClearValue(BackgroundProperty);
        HistoryPanelButton.ClearValue(ForegroundProperty);
        UpdateListPositionIndicator();
    }

    private void SearchButton_Click(object sender, RoutedEventArgs e)
    {
        if (_searchWindow is not null)
        {
            LogAppEvent("SEARCH WINDOW ACTIVATE", string.Empty);
            _searchWindow.Activate();
            return;
        }

        LogAppEvent("SEARCH WINDOW OPEN", string.Empty);
        _searchWindow = new StationSearchWindow(this)
        {
            Owner = this
        };
        _searchWindow.Closed += (_, _) =>
        {
            LogAppEvent("SEARCH WINDOW CLOSED", string.Empty);
            _searchWindow = null;
        };
        _searchWindow.Show();
    }

    private void RecordedTracksButton_Click(object sender, RoutedEventArgs e)
    {
        var volumeBeforeEditor = Player.Volume;
        var restoreVolume = volumeBeforeEditor > 0;
        LogAppEvent("RECORDED EDITOR OPEN",
            $"main window hidden\nstream continues\nvolume before editor: {volumeBeforeEditor:0.00}");
        if (restoreVolume) SetPlayerVolume(0);

        try
        {
            var window = new RecordedTracksWindow
            {
                WindowStartupLocation = WindowStartupLocation.Manual,
                Left = Left,
                Top = Top,
                Width = ActualWidth,
                Height = ActualHeight
            };
            Hide();
            window.ShowDialog();
        }
        finally
        {
            if (restoreVolume) SetPlayerVolume(volumeBeforeEditor);
            Show();
            Activate();
            LogAppEvent("RECORDED EDITOR CLOSED",
                $"main window restored\nstream kept running\nvolume restored: {Player.Volume:0.00}");
        }
    }

    private void LanguageButton_Click(object sender, RoutedEventArgs e)
    {
        LocalizationManager.ToggleLanguage();
        LogAppEvent("LANGUAGE TOGGLE", Thread.CurrentThread.CurrentUICulture.Name);
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
        StartPlayback(preserveStationIdentity: true, origin: PlaybackOrigin.Search);
    }

    internal bool AddSearchStationToPlaylist(SearchStationItem station)
    {
        ApplySearchStation(station);
        var radioStation = new RadioStation(station.Name, station.Description, station.StreamUrl, DateTime.Now);
        if (_stations.Any(item => string.Equals(item.StreamUrl, station.StreamUrl, StringComparison.OrdinalIgnoreCase)))
            return false;

        _stations.Add(radioStation);
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
        LogAppEvent("SEARCH STATION APPLIED", $"name: {station.Name}\nurl: {station.StreamUrl}");
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

        _stations.Add(station with { PlayedAt = DateTime.Now });
        SavePlaylist();
        LogAppEvent("PLAYLIST ADD FROM HISTORY", $"name: {station.Name}\nurl: {station.StreamUrl}");
    }

    private void ArtworkFrame_MouseLeftButtonDown(object sender, System.Windows.Input.MouseButtonEventArgs e)
    {
        if (e.ClickCount != 2) return;
        e.Handled = true;

        var streamUrl = StreamUrlBox.Text.Trim();
        if (!Uri.TryCreate(streamUrl, UriKind.Absolute, out var uri)
            || (uri.Scheme != Uri.UriSchemeHttp && uri.Scheme != Uri.UriSchemeHttps))
        {
            SetMetaStatusKey("NeedHttpStream");
            return;
        }

        var stationName = StationNameText.Text.Trim();
        if (string.IsNullOrWhiteSpace(stationName) || stationName == L("SelectStation"))
            stationName = L("MyStream");

        if (_stations.Any(item => string.Equals(item.StreamUrl, streamUrl, StringComparison.OrdinalIgnoreCase)))
        {
            SetMetaStatusText(LF("PlaylistAlreadyContains", stationName));
            return;
        }

        var description = MetaDescriptionText.Text.Trim();
        if (string.IsNullOrWhiteSpace(description) || description == "—")
            description = L("UserStream");

        _stations.Add(new RadioStation(stationName, description, streamUrl, DateTime.Now));
        SavePlaylist();
        SetMetaStatusText(LF("PlaylistAdded", stationName));
        LogAppEvent("PLAYLIST ADD FROM ARTWORK", $"name: {stationName}\nurl: {streamUrl}");
    }

    private void ClearHistory_Click(object sender, RoutedEventArgs e)
    {
        _history.Clear();
        SaveHistory();
        LogAppEvent("HISTORY CLEARED", string.Empty);
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

    private void SelectStation(RadioStation station, PlaybackOrigin origin)
    {
        StationNameText.Text = station.Name;
        SetTrackText(station.Description);
        StreamUrlBox.Text = station.StreamUrl;
        UpdateMetadata(station.Name, station.Description, station.StreamUrl, "ConnectToStation");
        StartPlayback(preserveStationIdentity: true, origin: origin);
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

    private void StartPlayback(bool preserveStationIdentity = false, PlaybackOrigin origin = PlaybackOrigin.Manual)
    {
        var value = StreamUrlBox.Text.Trim();
        if (!Uri.TryCreate(value, UriKind.Absolute, out var uri) || (uri.Scheme != Uri.UriSchemeHttp && uri.Scheme != Uri.UriSchemeHttps))
        {
            SetStatusKey("CheckUrl");
            SetTrackKey("InvalidUrl");
            return;
        }

        var isSameCurrentStream = _currentStreamUri is not null
            && Uri.Compare(_currentStreamUri, uri, UriComponents.HttpRequestUrl, UriFormat.UriEscaped,
                StringComparison.OrdinalIgnoreCase) == 0;
        if (!preserveStationIdentity && !isSameCurrentStream)
            StationNameText.Text = L("MyStream");
        var canAddToHistory = (origin == PlaybackOrigin.Search || (origin == PlaybackOrigin.Manual && !isSameCurrentStream))
            && !_history.Any(item => string.Equals(item.StreamUrl, value, StringComparison.OrdinalIgnoreCase));
        _historyWritableStreamUrl = canAddToHistory ? value : null;
        SetStatusKey("Connecting");
        SetTrackKey("ConnectingToRadio");
        LogAppEvent("PLAYBACK START", $"origin: {origin}\nurl: {value}");
        UpdateMetadata(StationNameText.Text, MetaDescriptionText.Text == "—" ? L("UserStream") : MetaDescriptionText.Text, value, "ConnectToStation");
        RegisterUrlHistory(StationNameText.Text, MetaDescriptionText.Text, value);
        _audioBuffer.Reset();
        _currentStreamUri = uri;
        _currentTrackStartPosition = 0;
        _currentTrackStartedAt = DateTime.Now;
        _currentTrackTitle = null;
        _currentTrackKey = null;
        _currentMetadataSongRating = null;
        UpdateSongRatingDisplay();
        _isTrackStartLocked = false;
        _hasObservedTrackMetadata = false;
        CancelPendingTrackUpdate();
        Player.Stop();
        ResetArtwork();
        _ = LoadStationArtworkAsync(uri, _metadataCancellation?.Token ?? CancellationToken.None);
        Player.Source = uri;
        Player.Play();
        LogAppEvent("PLAYER PLAY", $"station: {StationNameText.Text}\nurl: {uri}");
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
        LogAppEvent("PLAYBACK STOP", $"url: {StreamUrlBox.Text.Trim()}");
        SaveAppState();
    }

    private void Player_MediaOpened(object sender, RoutedEventArgs e)
    {
        SetStatusKey("OnAir");
        if (string.IsNullOrWhiteSpace(_currentTrackTitle))
            SetTrackKey("StreamPlaying");
        SetMetaStatusKey("OnAirStatus");
        LogAppEvent("PLAYER OPENED", $"url: {Player.Source}");
        SaveCurrentStationToHistory();
    }

    private void Player_MediaFailed(object sender, ExceptionRoutedEventArgs e)
    {
        _isPlaying = false;
        PlayButton.Content = "▶";
        SetStatusKey("PlaybackError");
        SetTrackKey("PlaybackErrorText");
        SetMetaStatusKey("PlaybackErrorStatus");
        LogAppEvent("PLAYER ERROR", $"url: {Player.Source}\n{e.ErrorException.Message}");
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

    private void CopyStreamUrl_Click(object sender, RoutedEventArgs e)
    {
        var url = StreamUrlBox.Text.Trim();
        if (url.Length == 0) return;
        System.Windows.Clipboard.SetText(url);
    }

    private void StreamUrlBox_TextChanged(object sender, TextChangedEventArgs e)
    {
        _listIndicatorUrl = StreamUrlBox.Text.Trim();
        UpdateUrlRatingDisplay();
        UpdateListPositionIndicator();
    }

    private void SetListIndicatorUrl(string streamUrl)
    {
        _listIndicatorUrl = streamUrl.Trim();
        UpdateListPositionIndicator();
    }

    private void UpdateListPositionIndicator()
    {
        var currentUrl = _listIndicatorUrl ?? StreamUrlBox.Text.Trim();
        var view = _positionIndicatorUsesHistory ? _historyView : _stationsView;
        var visibleItems = view is null
            ? (_positionIndicatorUsesHistory ? _history : _stations).ToList()
            : view.Cast<RadioStation>().ToList();
        var currentIndex = 0;
        if (!string.IsNullOrWhiteSpace(currentUrl))
        {
            for (var index = 0; index < visibleItems.Count; index++)
            {
                if (!string.Equals(visibleItems[index].StreamUrl, currentUrl, StringComparison.OrdinalIgnoreCase)) continue;
                currentIndex = index + 1;
                break;
            }
        }

        PlaylistPositionText.Text = $"{currentIndex} / {visibleItems.Count}";
    }

    private void UrlRatingStar_Click(object sender, RoutedEventArgs e)
    {
        if (sender is not System.Windows.Controls.Button { Tag: string tag }
            || !int.TryParse(tag, out var clickedRating)
            || clickedRating is < 1 or > 5
            || !TryGetCurrentUrlRatingKey(out var urlKey))
            return;

        var currentRating = _urlRatings.GetValueOrDefault(urlKey);
        var newRating = currentRating == clickedRating ? clickedRating - 1 : clickedRating;

        if (newRating == 0)
            _urlRatings.Remove(urlKey);
        else
            _urlRatings[urlKey] = newRating;

        SaveUrlRatings();
        UpdateUrlRatingDisplay();
        RefreshUrlRatingStars(urlKey);
        LogAppEvent("URL RATING CHANGED", $"rating: {newRating}\nurl: {urlKey}");
    }

    private bool TryGetCurrentUrlRatingKey(out string urlKey)
    {
        urlKey = string.Empty;
        return TryGetUrlRatingKey(StreamUrlBox.Text, out urlKey);
    }

    private static bool TryGetUrlRatingKey(string? streamUrl, out string urlKey)
    {
        urlKey = string.Empty;
        if (!Uri.TryCreate(streamUrl?.Trim(), UriKind.Absolute, out var uri)
            || (uri.Scheme != Uri.UriSchemeHttp && uri.Scheme != Uri.UriSchemeHttps))
            return false;

        urlKey = uri.GetComponents(UriComponents.HttpRequestUrl, UriFormat.UriEscaped);
        return true;
    }

    private void UpdateUrlRatingDisplay()
    {
        var hasUrl = TryGetCurrentUrlRatingKey(out var urlKey);
        var rating = hasUrl ? _urlRatings.GetValueOrDefault(urlKey) : 0;
        UrlRatingPanel.Opacity = hasUrl ? 1 : 0.4;
        UrlRatingPanel.IsEnabled = hasUrl;

        foreach (var button in UrlRatingPanel.Children.OfType<System.Windows.Controls.Button>())
        {
            var starNumber = button.Tag is string tag && int.TryParse(tag, out var value) ? value : 0;
            button.Foreground = starNumber > 0 && starNumber <= rating ? UrlRatingOnBrush : UrlRatingOffBrush;
        }
    }

    private void StationCollection_CollectionChanged(object? sender, NotifyCollectionChangedEventArgs e)
    {
        if (e.NewItems is not null)
        {
            foreach (var station in e.NewItems.OfType<RadioStation>())
                UpdateStationUrlRatingStars(station);
        }

        UpdateListPositionIndicator();
    }

    private void RefreshAllUrlRatingStars()
    {
        foreach (var station in _stations.Concat(_history))
            UpdateStationUrlRatingStars(station);
    }

    private void RefreshUrlRatingStars(string urlKey)
    {
        foreach (var station in _stations.Concat(_history))
        {
            if (TryGetUrlRatingKey(station.StreamUrl, out var stationKey)
                && string.Equals(stationKey, urlKey, StringComparison.OrdinalIgnoreCase))
                UpdateStationUrlRatingStars(station);
        }
    }

    private void UpdateStationUrlRatingStars(RadioStation station)
    {
        station.UrlRatingStars = TryGetUrlRatingKey(station.StreamUrl, out var urlKey)
            && _urlRatings.TryGetValue(urlKey, out var rating)
            ? new string('★', Math.Clamp(rating, 0, 5))
            : string.Empty;
    }

    private void SongRatingStar_Click(object sender, RoutedEventArgs e)
    {
        if (sender is not System.Windows.Controls.Button { Tag: string tag }
            || !int.TryParse(tag, out var clickedRating)
            || clickedRating is < 1 or > 5
            || !TryGetCurrentSongRatingIdentity(out var identity))
            return;

        var currentRating = GetEffectiveSongRating(identity.Key);
        var newRating = currentRating == clickedRating ? clickedRating - 1 : clickedRating;
        _songRatings[identity.Key] = new SongRatingEntry(
            identity.Artist, identity.Title, identity.Genre, newRating, DateTime.Now);

        SaveSongRatings();
        UpdateSongRatingDisplay();
        LogAppEvent("SONG RATING CHANGED",
            $"artist: {identity.Artist}\ntitle: {identity.Title}\ngenre: {identity.Genre}\nrating: {newRating}");
    }

    private void UpdateSongRatingDisplay()
    {
        var hasSong = TryGetCurrentSongRatingIdentity(out var identity);
        var rating = hasSong ? GetEffectiveSongRating(identity.Key) : 0;
        SongRatingPanel.Visibility = hasSong ? Visibility.Visible : Visibility.Collapsed;
        SongRatingPanel.Opacity = 1;
        SongRatingPanel.IsEnabled = hasSong;

        foreach (var button in SongRatingPanel.Children.OfType<System.Windows.Controls.Button>())
        {
            var starNumber = button.Tag is string tag && int.TryParse(tag, out var value) ? value : 0;
            button.Foreground = starNumber > 0 && starNumber <= rating ? SongRatingOnBrush : SongRatingOffBrush;
        }
    }

    private int GetEffectiveSongRating(string songKey)
    {
        return _songRatings.TryGetValue(songKey, out var localRating)
            ? localRating.Rating
            : _currentMetadataSongRating ?? 0;
    }

    private bool TryGetCurrentSongRatingIdentity(out SongRatingIdentity identity)
    {
        identity = default;
        if (string.IsNullOrWhiteSpace(_currentTrackTitle)) return false;

        var (artist, title) = SplitTrackArtistAndTitle(_currentTrackTitle);
        var genre = MetaDescriptionText.Text.Trim();
        if (genre == "—" || genre == L("UserStream")) genre = string.Empty;

        var artistKey = NormalizeSongRatingPart(artist);
        var titleKey = NormalizeSongRatingPart(title);
        var genreKey = NormalizeSongRatingPart(genre);
        if (string.IsNullOrWhiteSpace(artistKey) || string.IsNullOrWhiteSpace(titleKey)) return false;

        var key = $"ARTIST={artistKey}|TITLE={titleKey}|GENRE={genreKey}";
        identity = new SongRatingIdentity(key, artist.Trim(), title.Trim(), genre);
        return true;
    }

    private static (string Artist, string Title) SplitTrackArtistAndTitle(string value)
    {
        foreach (var separator in new[] { " - ", " – ", " — " })
        {
            var index = value.IndexOf(separator, StringComparison.Ordinal);
            if (index <= 0 || index + separator.Length >= value.Length) continue;
            return (value[..index].Trim(), value[(index + separator.Length)..].Trim());
        }

        return (string.Empty, value.Trim());
    }

    private static string NormalizeSongRatingPart(string value)
    {
        var normalized = value.Normalize(NormalizationForm.FormKC).ToUpperInvariant();
        normalized = Regex.Replace(normalized, @"[^\p{L}\p{N}]+", " ");
        return Regex.Replace(normalized, @"\s+", " ").Trim();
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
            SelectStation(_urlHistory[_historyNavigationIndex], PlaybackOrigin.UrlNavigation);
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
            LogAppEvent("RECORD BLOCKED", "reason: no valid HTTP(S) stream URL");
            return;
        }

        if (_currentStreamUri.AbsolutePath.EndsWith(".m3u8", StringComparison.OrdinalIgnoreCase))
        {
            SetStatusKey("NotMp3");
            SetMetaStatusKey("HlsCannotRecord");
            LogAppEvent("RECORD BLOCKED", $"reason: HLS stream\nurl: {_currentStreamUri}");
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
            LogAppEvent("RECORD BLOCKED", $"reason: duplicate active recording\nkey: {recordingKey}");
            _ = WindowsNotifier.ShowAsync(L("TrackAlreadyRecording"), BuildRecordingNotificationText());
            return;
        }

        if (_songRecordings.Count(recording => !recording.IsCompleted) >= MaxConcurrentSongRecordings)
        {
            SetStatusKey("Limit");
            SetMetaStatusKey("ThreeRecordingsLimit");
            LogAppEvent("RECORD BLOCKED", $"reason: active recording limit\nlimit: {MaxConcurrentSongRecordings}");
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

        var outputPath = GetAvailableFilePath(recordingsFolder, directUrlRecording ? BuildUrlRecordingFileName(_currentStreamUri) : BuildRecordingFileName(), ".mp3");
        var title = directUrlRecording ? BuildUrlRecordingTitle(_currentStreamUri) : _currentTrackTitle ?? string.Empty;
        var process = StartSongRecorder(_currentStreamUri, seedPath, outputPath, title, directUrlRecording ? null : _currentArtworkUrl, directUrlRecording);
        if (process is null)
        {
            TryDeleteFile(seedPath);
            SetStatusKey("PlaybackError");
            SetMetaStatusKey("PlaybackErrorStatus");
            LogAppEvent("RECORD START FAILED", $"output: {outputPath}");
            return;
        }
        LogAppEvent("RECORDER PROCESS STARTED", $"pid: {process.Id}\noutput: {outputPath}");

        var recordingTitle = directUrlRecording ? title : BuildRecordingNotificationText();
        var timerOffset = directUrlRecording ? TimeSpan.Zero : DateTime.Now - _currentTrackStartedAt;
        if (timerOffset < TimeSpan.Zero) timerOffset = TimeSpan.Zero;
        var now = DateTime.Now;
        var activeRecording = new ActiveSongRecording(
            recordingKey,
            process,
            recordingTitle,
            MetaStationText.Text,
            MetaDescriptionText.Text,
            _currentStreamUri.ToString(),
            outputPath,
            now,
            now - timerOffset);
        _songRecordings.Add(activeRecording);
        HookRecordingProcessExit(activeRecording);
        UpdateRecordingIndicator();
        SaveActiveRecordings();
        SetStatusKey("Recording");
        SetMetaStatusText(LF("SavingSongFile", Path.GetFileName(outputPath)));
        AppendMetadata($"{L("StartRecordingLog")}\n{recordingTitle}\n{Path.GetFileName(outputPath)}{(directUrlRecording ? $"\n{L("DirectUrlMode")}" : string.Empty)}");
        LogAppEvent("RECORD STARTED", $"title: {recordingTitle}\nfile: {outputPath}\nmode: {(directUrlRecording ? "direct-url" : "song-buffer")}\nseed bytes: {seedBytes.Length}\ntimer offset: {timerOffset:mm\\:ss}");
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
            CompleteRecording(recording);
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

    private void HookRecordingProcessExit(ActiveSongRecording recording)
    {
        try
        {
            recording.Process.EnableRaisingEvents = true;
            recording.Process.Exited += (_, _) =>
            {
                Dispatcher.Invoke(() =>
                {
                    if (recording.IsCompleted) return;
                    CompleteRecording(recording);
                    UpdateRecordingIndicator();
                    SaveActiveRecordings();
                });
            };
        }
        catch (InvalidOperationException)
        {
            CompleteRecording(recording);
        }
    }

    private void CompleteRecording(ActiveSongRecording recording)
    {
        MarkRecordingCompleted(recording);
        var duration = recording.ProcessDuration ?? DateTime.Now - recording.StartedAt;
        AppendMetadata($"{L("StopRecordingLog")}\n{recording.Title}\n{Path.GetFileName(recording.OutputPath)}\n{LF("ProcessDuration", duration.ToString(@"mm\:ss"))}");
        LogAppEvent("RECORD FINISHED", $"title: {recording.Title}\nfile: {recording.OutputPath}\npid: {SafeProcessId(recording.Process)}\nexit code: {SafeExitCode(recording.Process)}\nduration: {duration:mm\\:ss}");
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
        LogAppEvent("RECORD TIMER STOP CLICK", $"file: {recording.OutputPath}");
        StopRecording(recording, deleteFile: false);
    }

    private void DeleteRecordingTimerMenu_Click(object sender, RoutedEventArgs e)
    {
        if (!TryGetRecordingFromTimerMenu(sender, out var recording)) return;
        LogAppEvent("RECORD TIMER DELETE CLICK", $"file: {recording.OutputPath}");
        StopRecording(recording, deleteFile: true);
    }

    private void RecordingTimerSlot_LeftClick(object sender, System.Windows.Input.MouseButtonEventArgs e)
    {
        if (!TryGetRecordingFromTimerSlot(sender, out var recording)) return;
        LogAppEvent("RECORD TIMER SLOT OPEN", $"title: {recording.Title}\nurl: {recording.StreamUrl}");
        StationNameText.Text = recording.Station;
        SetTrackText(recording.Title);
        StreamUrlBox.Text = recording.StreamUrl;
        UpdateMetadata(recording.Station, recording.Description, recording.StreamUrl, "SwitchToRecordingChannel");
        StartPlayback(preserveStationIdentity: true, origin: PlaybackOrigin.Recording);
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
            LogAppEvent("RECORD DELETED", $"title: {recording.Title}\nfile: {recording.OutputPath}");
        }
        else
        {
            AppendMetadata($"{L("RecordingStoppedLog")}\n{recording.Title}\n{Path.GetFileName(recording.OutputPath)}");
            LogAppEvent("RECORD STOPPED BY USER", $"title: {recording.Title}\nfile: {recording.OutputPath}");
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
        return safeName;
    }

    private static string BuildUrlRecordingFileName(Uri streamUri)
    {
        var host = string.IsNullOrWhiteSpace(streamUri.Host) ? "stream" : streamUri.Host;
        var safeHost = string.Join("_", host.Split(Path.GetInvalidFileNameChars(), StringSplitOptions.RemoveEmptyEntries)).Trim();
        if (string.IsNullOrWhiteSpace(safeHost)) safeHost = "stream";
        return safeHost;
    }

    private static string GetAvailableFilePath(string folder, string baseName, string extension)
    {
        var safeBaseName = SanitizeFileName(baseName);
        var path = Path.Combine(folder, $"{safeBaseName}{extension}");
        if (!File.Exists(path)) return path;

        for (var index = 1; ; index++)
        {
            path = Path.Combine(folder, $"{safeBaseName}({index}){extension}");
            if (!File.Exists(path)) return path;
        }
    }

    private static string SanitizeFileName(string value)
    {
        var safeName = string.Join("_", value.Split(Path.GetInvalidFileNameChars(), StringSplitOptions.RemoveEmptyEntries)).Trim();
        safeName = Regex.Replace(safeName, @"\s+", " ");
        if (safeName.Length > 120) safeName = safeName[..120].Trim();
        return string.IsNullOrWhiteSpace(safeName) ? "WebStream" : safeName;
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

    private void OpenWindowsPanel(string target)
    {
        LogAppEvent("OPEN WINDOWS PANEL", target);
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
            startInfo.ArgumentList.Add(SpectrumSampleRate.ToString());
            startInfo.ArgumentList.Add("pipe:1");

            process = Process.Start(startInfo);
            if (process is null) return;
            _signalProcess = process;
            _ = process.StandardError.ReadToEndAsync(cancellationToken);

            var buffer = new byte[SpectrumFftSize * sizeof(short)];
            var hopBytes = SpectrumHopSize * sizeof(short);
            var bufferedBytes = 0;
            while (!cancellationToken.IsCancellationRequested)
            {
                var bytesRead = await process.StandardOutput.BaseStream.ReadAsync(
                    buffer.AsMemory(bufferedBytes, buffer.Length - bufferedBytes), cancellationToken);
                if (bytesRead <= 0) break;
                bufferedBytes += bytesRead;
                if (bufferedBytes < buffer.Length) continue;

                var spectrum = CalculateSpectrum(buffer);
                Buffer.BlockCopy(buffer, hopBytes, buffer, 0, buffer.Length - hopBytes);
                bufferedBytes = buffer.Length - hopBytes;
                await Dispatcher.InvokeAsync(() => PushSpectrum(spectrum), DispatcherPriority.Background, cancellationToken);
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

    private static double[] CalculateSpectrum(byte[] pcmBuffer)
    {
        var real = new double[SpectrumFftSize];
        var imaginary = new double[SpectrumFftSize];
        double sampleMean = 0;
        for (var i = 0; i < SpectrumFftSize; i++)
            sampleMean += BitConverter.ToInt16(pcmBuffer, i * sizeof(short)) / 32768.0;
        sampleMean /= SpectrumFftSize;

        for (var i = 0; i < SpectrumFftSize; i++)
        {
            var sample = (BitConverter.ToInt16(pcmBuffer, i * sizeof(short)) / 32768.0) - sampleMean;
            var window = 0.5 * (1 - Math.Cos(2 * Math.PI * i / (SpectrumFftSize - 1)));
            real[i] = sample * window;
        }

        FastFourierTransform(real, imaginary);

        var spectrum = new double[SignalBarCount];
        var frequencyRange = SpectrumMaxFrequency / SpectrumMinFrequency;
        for (var band = 0; band < SignalBarCount; band++)
        {
            var lowerFrequency = SpectrumMinFrequency * Math.Pow(frequencyRange, band / (double)SignalBarCount);
            var upperFrequency = SpectrumMinFrequency * Math.Pow(frequencyRange, (band + 1) / (double)SignalBarCount);
            var firstBin = Math.Max(1, (int)Math.Floor(lowerFrequency * SpectrumFftSize / SpectrumSampleRate));
            var lastBin = Math.Min((SpectrumFftSize / 2) - 1,
                Math.Max(firstBin, (int)Math.Ceiling(upperFrequency * SpectrumFftSize / SpectrumSampleRate)));

            double peakMagnitude = 0;
            for (var bin = firstBin; bin <= lastBin; bin++)
            {
                var magnitude = Math.Sqrt((real[bin] * real[bin]) + (imaginary[bin] * imaginary[bin]));
                peakMagnitude = Math.Max(peakMagnitude, magnitude * 2 / SpectrumFftSize);
            }

            var decibels = 20 * Math.Log10(Math.Max(peakMagnitude, 1e-9));
            var normalized = Math.Clamp((decibels + 78) / 66, 0, 1);
            spectrum[band] = Math.Pow(normalized, 0.82);
        }

        return spectrum;
    }

    private static void FastFourierTransform(double[] real, double[] imaginary)
    {
        var length = real.Length;
        for (int source = 1, target = 0; source < length; source++)
        {
            var bit = length >> 1;
            while ((target & bit) != 0)
            {
                target ^= bit;
                bit >>= 1;
            }
            target ^= bit;

            if (source >= target) continue;
            (real[source], real[target]) = (real[target], real[source]);
            (imaginary[source], imaginary[target]) = (imaginary[target], imaginary[source]);
        }

        for (var blockSize = 2; blockSize <= length; blockSize <<= 1)
        {
            var angle = -2 * Math.PI / blockSize;
            var phaseStepReal = Math.Cos(angle);
            var phaseStepImaginary = Math.Sin(angle);
            for (var blockStart = 0; blockStart < length; blockStart += blockSize)
            {
                double phaseReal = 1;
                double phaseImaginary = 0;
                var halfBlock = blockSize >> 1;
                for (var offset = 0; offset < halfBlock; offset++)
                {
                    var even = blockStart + offset;
                    var odd = even + halfBlock;
                    var oddReal = (phaseReal * real[odd]) - (phaseImaginary * imaginary[odd]);
                    var oddImaginary = (phaseReal * imaginary[odd]) + (phaseImaginary * real[odd]);

                    real[odd] = real[even] - oddReal;
                    imaginary[odd] = imaginary[even] - oddImaginary;
                    real[even] += oddReal;
                    imaginary[even] += oddImaginary;

                    var nextPhaseReal = (phaseReal * phaseStepReal) - (phaseImaginary * phaseStepImaginary);
                    phaseImaginary = (phaseReal * phaseStepImaginary) + (phaseImaginary * phaseStepReal);
                    phaseReal = nextPhaseReal;
                }
            }
        }
    }

    private void PushSpectrum(double[] spectrum)
    {
        SignalDisplay.SetSpectrum(spectrum);
    }

    private void ResetSignalBars()
    {
        SignalDisplay.Reset();
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
        LogAppEvent("METADATA READER START", $"url: {streamUri}");
        _ = streamUri.AbsolutePath.EndsWith(".m3u8", StringComparison.OrdinalIgnoreCase)
            ? ReadHlsMetadataAsync(streamUri, _metadataCancellation.Token)
            : ReadIcyMetadataAsync(streamUri, _metadataCancellation.Token);
    }

    private void StopMetadataReader()
    {
        if (_metadataCancellation is not null)
            LogAppEvent("METADATA READER STOP", string.Empty);
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
                var text = DecodeIcyMetadata(metadata);
                var title = ExtractStreamTitle(text);
                var rating = ExtractIcySongRating(text);
                await Dispatcher.InvokeAsync(() => AppendMetadata($"ICY metadata\n{text.Trim('\0', ' ')}"));
                if (!string.IsNullOrWhiteSpace(title))
                    await Dispatcher.InvokeAsync(() => ScheduleCurrentTrack(title, rating));
                var coverUrl = ExtractCoverArtUrl(text);
                if (!string.IsNullOrWhiteSpace(coverUrl))
                    await Dispatcher.InvokeAsync(() => SetArtwork(coverUrl));
            }
        }
        catch (OperationCanceledException)
        {
            // Switching or stopping a station intentionally ends the metadata stream.
        }
        catch (HttpRequestException ex)
        {
            await Dispatcher.InvokeAsync(() => LogAppEvent("ICY METADATA ERROR", ex.Message));
            await Dispatcher.InvokeAsync(() => SetMetaStatusKey("NoIcyMetadata"));
        }
        catch (EndOfStreamException)
        {
            await Dispatcher.InvokeAsync(() => LogAppEvent("ICY METADATA END", "stream ended"));
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
                    var rating = ExtractHlsSongRating(metadataLine);
                    if (!string.IsNullOrWhiteSpace(track))
                        await Dispatcher.InvokeAsync(() => ScheduleCurrentTrack(track, rating));

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
        catch (HttpRequestException ex)
        {
            await Dispatcher.InvokeAsync(() => LogAppEvent("HLS METADATA ERROR", ex.Message));
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

    private static int? ExtractHlsSongRating(string line)
    {
        foreach (var name in new[] { "songrating", "trackrating", "rating", "popularimeter", "popm" })
        {
            var value = ExtractHlsAttribute(line, name);
            if (string.IsNullOrWhiteSpace(value))
            {
                var match = Regex.Match(line,
                    $@"\b{Regex.Escape(name)}\s*=\s*(?<value>[^,\s]+)", RegexOptions.IgnoreCase);
                value = match.Success ? match.Groups["value"].Value.Trim(' ', '\'', '"') : null;
            }

            var rating = ParseMetadataSongRating(value);
            if (rating.HasValue) return rating;
        }

        return null;
    }

    private static int? ParseMetadataSongRating(string? value)
    {
        if (string.IsNullOrWhiteSpace(value)) return null;

        var filledStars = value.Count(character => character == '★');
        if (filledStars > 0) return Math.Clamp(filledStars, 0, 5);

        var match = Regex.Match(value, @"(?<rating>\d+(?:[.,]\d+)?)\s*(?:/\s*(?<maximum>\d+(?:[.,]\d+)?))?");
        if (!match.Success) return null;

        static bool TryReadNumber(string text, out double number) => double.TryParse(
            text.Replace(',', '.'), System.Globalization.NumberStyles.Float,
            System.Globalization.CultureInfo.InvariantCulture, out number);

        if (!TryReadNumber(match.Groups["rating"].Value, out var rawRating) || rawRating < 0)
            return null;

        double fiveStarRating;
        if (match.Groups["maximum"].Success
            && TryReadNumber(match.Groups["maximum"].Value, out var maximum)
            && maximum > 0)
            fiveStarRating = rawRating / maximum * 5;
        else if (rawRating <= 5)
            fiveStarRating = rawRating;
        else if (rawRating <= 100)
            fiveStarRating = rawRating / 20;
        else if (rawRating <= 255)
            fiveStarRating = rawRating / 51;
        else
            return null;

        return Math.Clamp((int)Math.Round(fiveStarRating, MidpointRounding.AwayFromZero), 0, 5);
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

    private static int? ExtractIcySongRating(string metadata)
    {
        foreach (var name in new[] { "SongRating", "TrackRating", "Rating", "Popularimeter", "POPM" })
        {
            var value = ExtractMetadataValue(metadata, name);
            if (string.IsNullOrWhiteSpace(value))
            {
                var match = Regex.Match(metadata,
                    $@"(?:^|;)\s*{Regex.Escape(name)}\s*=\s*(?<value>[^;\0]+)", RegexOptions.IgnoreCase);
                value = match.Success ? match.Groups["value"].Value.Trim(' ', '\'', '"') : null;
            }

            var rating = ParseMetadataSongRating(value);
            if (rating.HasValue) return rating;
        }

        return null;
    }

    private static string DecodeIcyMetadata(byte[] metadata)
    {
        try
        {
            return new UTF8Encoding(false, true).GetString(metadata);
        }
        catch (DecoderFallbackException)
        {
            return Encoding.Latin1.GetString(metadata);
        }
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
            var terminator = $"{quote};";
            var end = metadata.IndexOf(terminator, start, StringComparison.Ordinal);
            if (end < 0)
                end = metadata.IndexOf($";{name}=", start, StringComparison.OrdinalIgnoreCase);
            if (end < 0)
                end = metadata.IndexOf(quote, start);
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
            SetArtworkFrameMetadataState();
            SaveCurrentStationToHistory();
            UpdateCurrentUrlHistoryMetadata();
            UpdateSongRatingDisplay();
        }
    }

    private void SetCurrentTrack(string title, int? metadataRating = null)
    {
        var normalizedTitle = NormalizeTitle(title);
        var songKey = BuildSongCompareKey(title);
        var trackChanged = !string.IsNullOrWhiteSpace(normalizedTitle)
            && !string.Equals(_currentTrackKey, songKey, StringComparison.OrdinalIgnoreCase);
        if (trackChanged)
        {
            var canLockTrackStart = _hasObservedTrackMetadata;
            LogAppEvent("TRACK CHANGED", $"from: {_currentTrackTitle ?? "—"}\nto: {normalizedTitle}\nkey: {songKey}\nbuffer position: {_audioBuffer.CurrentPosition}");
            _currentTrackTitle = normalizedTitle;
            _currentTrackKey = songKey;
            _currentMetadataSongRating = metadataRating;
            _currentTrackStartPosition = _audioBuffer.CurrentPosition;
            _currentTrackStartedAt = DateTime.Now;
            _hasObservedTrackMetadata = true;
            _isTrackStartLocked = canLockTrackStart;
            if (_isTrackStartLocked)
                SetArtworkFrameState(ArtworkFrameReadyBrush);
            else
                SetArtworkFrameMetadataState();
        }
        else if (metadataRating.HasValue)
        {
            _currentMetadataSongRating = metadataRating;
        }

        SetTrackText(title);
        SetMetaStatusKey("MetadataUpdated");
        UpdateSongRatingDisplay();
        if (string.Equals(_lastArtworkQuery, title, StringComparison.Ordinal)) return;
        _lastArtworkQuery = title;
        _ = FindArtworkAsync(title, _metadataCancellation?.Token ?? CancellationToken.None);
    }

    private void ScheduleCurrentTrack(string title, int? metadataRating = null)
    {
        var normalizedTitle = NormalizeTitle(title);
        var songKey = BuildSongCompareKey(normalizedTitle);
        if (string.IsNullOrWhiteSpace(normalizedTitle) || string.IsNullOrWhiteSpace(songKey)) return;

        if (string.Equals(_currentTrackKey, songKey, StringComparison.OrdinalIgnoreCase))
        {
            if (metadataRating.HasValue)
            {
                _currentMetadataSongRating = metadataRating;
                UpdateSongRatingDisplay();
            }
            return;
        }

        if (string.Equals(_pendingTrackKey, songKey, StringComparison.OrdinalIgnoreCase))
        {
            if (metadataRating.HasValue) _pendingTrackMetadataRating = metadataRating;
            return;
        }

        _isTrackStartLocked = false;
        SetArtworkFrameMetadataState();
        CancelPendingTrackUpdate();
        _pendingTrackKey = songKey;
        _pendingTrackMetadataRating = metadataRating;
        _pendingTrackCancellation = new CancellationTokenSource();
        var token = _pendingTrackCancellation.Token;
        LogAppEvent("TRACK UPDATE SCHEDULED", $"title: {normalizedTitle}\nkey: {songKey}\ndelay: {MetadataTrackDelay.TotalSeconds:0}s");
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
                SetCurrentTrack(title, _pendingTrackMetadataRating);
                _pendingTrackKey = null;
                _pendingTrackMetadataRating = null;
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
        _pendingTrackMetadataRating = null;
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
        return TrackMetadataNormalizer.NormalizeTitle(title);
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
        if (MetadataLogText.Text.Length > 80_000)
            MetadataLogText.Text = MetadataLogText.Text[^60_000..];
        MetadataLogText.ScrollToEnd();
    }

    private void LogAppEvent(string action, string details)
    {
        AppendMetadata(string.IsNullOrWhiteSpace(details)
            ? $"APP\n{action}"
            : $"APP\n{action}\n{details}");
    }

    private static string SafeProcessId(Process process)
    {
        try
        {
            return process.Id.ToString();
        }
        catch (InvalidOperationException)
        {
            return "n/a";
        }
    }

    private static string SafeExitCode(Process process)
    {
        try
        {
            return process.HasExited ? process.ExitCode.ToString() : "running";
        }
        catch (InvalidOperationException)
        {
            return "n/a";
        }
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

    private void SetArtworkFrameState(SolidColorBrush brush)
    {
        ArtworkFrame.BorderBrush = brush;
    }

    private void SetArtworkFrameMetadataState()
    {
        if (!_isTrackStartLocked)
            SetArtworkFrameState(ArtworkFrameMetadataBrush);
    }

    private void ArtworkClipHost_SizeChanged(object sender, SizeChangedEventArgs e)
    {
        ArtworkClipHost.Clip = new RectangleGeometry(new Rect(0, 0, e.NewSize.Width, e.NewSize.Height), 10, 10);
    }

    private void ResetArtwork()
    {
        ArtworkImage.Source = null;
        ArtworkImage.Visibility = Visibility.Collapsed;
        ArtworkPlaceholder.Visibility = Visibility.Visible;
        SetArtworkFrameState(ArtworkFrameIdleBrush);
        _isTrackStartLocked = false;
        _hasObservedTrackMetadata = false;
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
        SetMetaStatusKey(statusKey);
        ArtworkCaptionText.Text = station.ToUpperInvariant();
    }

    private void SaveCurrentStationToHistory()
    {
        var streamUrl = StreamUrlBox.Text.Trim();
        if (string.IsNullOrWhiteSpace(streamUrl)
            || !string.Equals(_historyWritableStreamUrl, streamUrl, StringComparison.OrdinalIgnoreCase))
            return;

        var station = new RadioStation(StationNameText.Text, MetaDescriptionText.Text, streamUrl, DateTime.Now);
        var existingIndex = -1;
        for (var index = 0; index < _history.Count; index++)
        {
            if (!string.Equals(_history[index].StreamUrl, streamUrl, StringComparison.OrdinalIgnoreCase)) continue;
            existingIndex = index;
            break;
        }

        if (existingIndex < 0)
            _history.Add(station);
        else
            _history[existingIndex] = station;
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

    private void LoadUrlRatings()
    {
        try
        {
            if (!File.Exists(_urlRatingsPath)) return;
            var ratings = JsonSerializer.Deserialize<Dictionary<string, int>>(File.ReadAllText(_urlRatingsPath));
            if (ratings is null) return;

            foreach (var (url, rating) in ratings)
            {
                if (!string.IsNullOrWhiteSpace(url) && rating is >= 1 and <= 5)
                    _urlRatings[url] = rating;
            }
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

    private void SaveUrlRatings()
    {
        try
        {
            Directory.CreateDirectory(Path.GetDirectoryName(_urlRatingsPath)!);
            var orderedRatings = new SortedDictionary<string, int>(_urlRatings, StringComparer.OrdinalIgnoreCase);
            File.WriteAllText(_urlRatingsPath,
                JsonSerializer.Serialize(orderedRatings, new JsonSerializerOptions { WriteIndented = true }));
        }
        catch (IOException)
        {
        }
        catch (UnauthorizedAccessException)
        {
        }
    }

    private void LoadSongRatings()
    {
        try
        {
            if (!File.Exists(_songRatingsPath)) return;
            var ratings = JsonSerializer.Deserialize<Dictionary<string, SongRatingEntry>>(
                File.ReadAllText(_songRatingsPath));
            if (ratings is null) return;

            foreach (var (key, entry) in ratings)
            {
                if (!string.IsNullOrWhiteSpace(key) && entry.Rating is >= 0 and <= 5)
                    _songRatings[key] = entry;
            }
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

    private void SaveSongRatings()
    {
        try
        {
            Directory.CreateDirectory(Path.GetDirectoryName(_songRatingsPath)!);
            var orderedRatings = new SortedDictionary<string, SongRatingEntry>(
                _songRatings, StringComparer.OrdinalIgnoreCase);
            File.WriteAllText(_songRatingsPath,
                JsonSerializer.Serialize(orderedRatings, new JsonSerializerOptions { WriteIndented = true }));
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
                StartPlayback(origin: PlaybackOrigin.Restored);
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
                var recording = new ActiveSongRecording(
                    state.TrackKey,
                    process,
                    state.Title,
                    state.Station,
                    state.Description,
                    state.StreamUrl,
                    state.OutputPath,
                    state.StartedAt,
                    state.TimerStartedAt);
                _songRecordings.Add(recording);
                HookRecordingProcessExit(recording);
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
            foreach (var station in stations
                         .Where(station => !string.IsNullOrWhiteSpace(station.StreamUrl))
                         .OrderBy(station => station.PlayedAt ?? DateTime.MinValue))
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
                    foreach (var station in stations
                                 .Where(station => !string.IsNullOrWhiteSpace(station.StreamUrl))
                                 .OrderBy(station => station.PlayedAt ?? DateTime.MinValue))
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

public sealed record RadioStation(string Name, string Description, string StreamUrl, DateTime? PlayedAt = null) : INotifyPropertyChanged
{
    private string _urlRatingStars = string.Empty;

    [JsonIgnore]
    public string UrlRatingStars
    {
        get => _urlRatingStars;
        set
        {
            if (_urlRatingStars == value) return;
            _urlRatingStars = value;
            PropertyChanged?.Invoke(this, new PropertyChangedEventArgs(nameof(UrlRatingStars)));
        }
    }

    public event PropertyChangedEventHandler? PropertyChanged;
}

public sealed record SongRatingEntry(
    string Artist,
    string Title,
    string Genre,
    int Rating,
    DateTime UpdatedAt);

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
