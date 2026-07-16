using System.Collections.ObjectModel;
using System.ComponentModel;
using System.Diagnostics;
using System.IO;
using System.Text;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Input;
using System.Windows.Threading;

namespace WebStream;

public partial class RecordedTracksWindow : Window
{
    private enum TrimMarker
    {
        None,
        Start,
        End
    }

    private readonly ObservableCollection<RecordedTrackItem> _tracks = new();
    private readonly DispatcherTimer _positionTimer = new() { Interval = TimeSpan.FromMilliseconds(500) };
    private const double WheelStepSeconds = 0.1;
    private bool _isPlaying;
    private bool _isDraggingPosition;
    private TimeSpan _duration = TimeSpan.Zero;
    private double? _trimStartSeconds;
    private double? _trimEndSeconds;
    private TrimMarker _activeTrimMarker = TrimMarker.None;

    public RecordedTracksWindow()
    {
        InitializeComponent();
        TracksList.ItemsSource = _tracks;
        _positionTimer.Tick += (_, _) => UpdatePlaybackPosition();
        LoadTracks();
    }

    private static string L(string key) => LocalizationManager.Get(key);

    private static string LF(string key, params object[] args) => LocalizationManager.Format(key, args);

    private static string RecordingsFolder => Path.Combine(
        Environment.GetFolderPath(Environment.SpecialFolder.MyMusic),
        "WebStream");

    private RecordedTrackItem? SelectedTrack => TracksList.SelectedItem as RecordedTrackItem;

    private void RefreshButton_Click(object sender, RoutedEventArgs e) => LoadTracks();

    private void LoadTracks()
    {
        _tracks.Clear();
        Directory.CreateDirectory(RecordingsFolder);
        foreach (var file in Directory.EnumerateFiles(RecordingsFolder, "*.mp3")
                     .Where(file => !file.EndsWith(".bak.mp3", StringComparison.OrdinalIgnoreCase))
                     .OrderByDescending(File.GetLastWriteTime))
            _tracks.Add(new RecordedTrackItem(file));
        StatusText.Text = LF("RecordedLoaded", _tracks.Count);
    }

    private void TracksList_SelectionChanged(object sender, SelectionChangedEventArgs e)
    {
        StopPlayback();
        if (SelectedTrack is not { } track)
        {
            CurrentFileText.Text = L("SelectRecording");
            ClearEditor();
            return;
        }

        CurrentFileText.Text = track.DisplayName;
        FileNameBox.Text = Path.GetFileName(track.Path);
        LoadTags(track.Path);
        TrackPlayer.Source = new Uri(track.Path);
        _duration = TimeSpan.Zero;
        PositionSlider.Maximum = 1;
        PositionSlider.Value = 0;
        UpdatePositionText(0);
        ResetTrimMarks();
        StatusText.Text = track.Path;
    }

    private void PlayPauseButton_Click(object sender, RoutedEventArgs e)
    {
        if (SelectedTrack is null) return;
        if (_isPlaying)
        {
            TrackPlayer.Pause();
            _isPlaying = false;
            PlayPauseButton.Content = "▶";
            return;
        }

        TrackPlayer.Play();
        _isPlaying = true;
        PlayPauseButton.Content = "Ⅱ";
        _positionTimer.Start();
    }

    private void StopButton_Click(object sender, RoutedEventArgs e) => StopPlayback();

    private void StopPlayback()
    {
        TrackPlayer.Stop();
        _positionTimer.Stop();
        _isPlaying = false;
        PlayPauseButton.Content = "▶";
        PositionSlider.Value = 0;
        UpdatePositionText(0);
    }

    private void TrackPlayer_MediaOpened(object sender, RoutedEventArgs e)
    {
        if (TrackPlayer.NaturalDuration.HasTimeSpan)
        {
            _duration = TrackPlayer.NaturalDuration.TimeSpan;
            PositionSlider.Maximum = Math.Max(1, _duration.TotalSeconds);
            PresetFullTrimRange();
            UpdatePositionText(PositionSlider.Value);
            UpdateTrimRangeVisual();
        }
    }

    private void TrackPlayer_MediaEnded(object sender, RoutedEventArgs e)
    {
        TrackPlayer.Stop();
        _positionTimer.Stop();
        _isPlaying = false;
        PlayPauseButton.Content = "▶";
        PositionSlider.Value = PositionSlider.Maximum;
        UpdatePositionText(PositionSlider.Maximum);
    }

    private void PositionSlider_ValueChanged(object sender, RoutedPropertyChangedEventArgs<double> e)
    {
        if (!PositionSlider.IsMouseCaptureWithin || _duration <= TimeSpan.Zero) return;
        SeekToSliderValue(PositionSlider.Value);
    }

    private void PositionSlider_PreviewMouseWheel(object sender, MouseWheelEventArgs e)
    {
        if (_duration <= TimeSpan.Zero || _isPlaying) return;
        var direction = e.Delta > 0 ? 1 : -1;
        if (TryAdjustActiveTrimMarker(direction))
        {
            e.Handled = true;
            return;
        }

        var nextValue = Math.Clamp(PositionSlider.Value + direction * WheelStepSeconds, 0, PositionSlider.Maximum);
        PositionSlider.Value = nextValue;
        SeekToSliderValue(nextValue);
        e.Handled = true;
    }

    private void PositionSlider_SizeChanged(object sender, SizeChangedEventArgs e) => UpdateTrimRangeVisual();

    private void UpdatePlaybackPosition()
    {
        if (_isDraggingPosition || _duration <= TimeSpan.Zero) return;
        PositionSlider.Value = Math.Clamp(TrackPlayer.Position.TotalSeconds, 0, PositionSlider.Maximum);
        UpdatePositionText(TrackPlayer.Position.TotalSeconds);
    }

    private void SeekToSliderValue(double seconds)
    {
        _isDraggingPosition = true;
        TrackPlayer.Position = TimeSpan.FromSeconds(seconds);
        UpdatePositionText(seconds);
        _isDraggingPosition = false;
    }

    private void UpdatePositionText(double seconds)
    {
        PositionText.Text = _duration > TimeSpan.Zero
            ? $"{FormatTime(seconds)} / {FormatTime(_duration.TotalSeconds)}"
            : FormatTime(seconds);
    }

    private void LoadTags(string path)
    {
        var tags = Id3TagReader.Read(path);
        TitleBox.Text = tags.Title;
        ArtistBox.Text = tags.Artist;
        AlbumBox.Text = tags.Album;
        GenreBox.Text = tags.Genre;
    }

    private void ClearEditor()
    {
        TitleBox.Clear();
        ArtistBox.Clear();
        AlbumBox.Clear();
        GenreBox.Clear();
        FileNameBox.Clear();
        ResetTrimMarks();
    }

    private async void SaveTagsButton_Click(object sender, RoutedEventArgs e)
    {
        if (SelectedTrack is not { } track) return;
        var ffmpeg = FindFfmpegPath();
        if (ffmpeg is null)
        {
            StatusText.Text = L("FfmpegNotFound");
            return;
        }

        StopPlayback();
        var tempPath = BuildTempPath(track.Path, ".tags.mp3");
        try
        {
            await RunFfmpegAsync(ffmpeg, [
                "-hide_banner", "-loglevel", "error", "-y",
                "-i", track.Path,
                "-map", "0",
                "-codec", "copy",
                "-metadata", $"title={TitleBox.Text.Trim()}",
                "-metadata", $"artist={ArtistBox.Text.Trim()}",
                "-metadata", $"album={AlbumBox.Text.Trim()}",
                "-metadata", $"genre={GenreBox.Text.Trim()}",
                "-id3v2_version", "3",
                tempPath
            ]);
            ReplaceWithBackup(track.Path, tempPath);
            await Id3TagWriter.MarkEditedAsync(track.Path);
            track.MarkEdited();
            track.Refresh();
            StatusText.Text = L("TagsSaved");
        }
        catch (Exception ex) when (ex is IOException or InvalidOperationException)
        {
            TryDelete(tempPath);
            StatusText.Text = ex.Message;
        }
    }

    private async void ApplyTrimButton_Click(object sender, RoutedEventArgs e)
    {
        if (SelectedTrack is not { } track) return;
        var ffmpeg = FindFfmpegPath();
        if (ffmpeg is null)
        {
            StatusText.Text = L("FfmpegNotFound");
            return;
        }

        if (_trimStartSeconds is not { } start || _trimEndSeconds is not { } end || end <= start)
        {
            StatusText.Text = L("TrimBadRange");
            return;
        }

        StopPlayback();
        var tempPath = BuildTempPath(track.Path, ".trim.mp3");
        try
        {
            await RunFfmpegAsync(ffmpeg, [
                "-hide_banner", "-loglevel", "error", "-y",
                "-ss", start.ToString("0.###", System.Globalization.CultureInfo.InvariantCulture),
                "-i", track.Path,
                "-t", (end - start).ToString("0.###", System.Globalization.CultureInfo.InvariantCulture),
                "-map", "0",
                "-codec", "copy",
                "-id3v2_version", "3",
                tempPath
            ]);
            ReplaceWithBackup(track.Path, tempPath);
            await Id3TagWriter.MarkEditedAsync(track.Path);
            track.MarkEdited();
            track.Refresh();
            TrackPlayer.Source = new Uri(track.Path);
            StatusText.Text = L("TrimSaved");
        }
        catch (Exception ex) when (ex is IOException or InvalidOperationException)
        {
            TryDelete(tempPath);
            StatusText.Text = ex.Message;
        }
    }

    private async void RenameFileButton_Click(object sender, RoutedEventArgs e)
    {
        if (SelectedTrack is not { } track) return;
        StopPlayback();

        var requestedName = FileNameBox.Text.Trim();
        if (string.IsNullOrWhiteSpace(requestedName) || requestedName.IndexOfAny(Path.GetInvalidFileNameChars()) >= 0)
        {
            StatusText.Text = L("RenameInvalid");
            return;
        }

        if (!requestedName.EndsWith(".mp3", StringComparison.OrdinalIgnoreCase))
            requestedName += ".mp3";

        var targetPath = Path.Combine(Path.GetDirectoryName(track.Path)!, requestedName);
        if (string.Equals(track.Path, targetPath, StringComparison.OrdinalIgnoreCase))
            return;

        if (File.Exists(targetPath))
        {
            StatusText.Text = L("RenameExists");
            return;
        }

        try
        {
            File.Move(track.Path, targetPath);
            track.UpdatePath(targetPath);
            await Id3TagWriter.MarkEditedAsync(track.Path);
            track.MarkEdited();
            track.Refresh();
            CurrentFileText.Text = track.DisplayName;
            FileNameBox.Text = Path.GetFileName(track.Path);
            TrackPlayer.Source = new Uri(track.Path);
            StatusText.Text = L("RenameSaved");
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException)
        {
            StatusText.Text = ex.Message;
        }
    }

    private void TrimStartButton_Click(object sender, RoutedEventArgs e)
    {
        if (SelectedTrack is null) return;
        _activeTrimMarker = TrimMarker.Start;
        _trimStartSeconds = Math.Clamp(PositionSlider.Value, 0, PositionSlider.Maximum);
        UpdateTrimButtons();
        UpdateTrimRangeVisual();
        StatusText.Text = LF("TrimStartSet", FormatTime(_trimStartSeconds.Value));
    }

    private void TrimEndButton_Click(object sender, RoutedEventArgs e)
    {
        if (SelectedTrack is null) return;
        _activeTrimMarker = TrimMarker.End;
        _trimEndSeconds = Math.Clamp(PositionSlider.Value, 0, PositionSlider.Maximum);
        UpdateTrimButtons();
        UpdateTrimRangeVisual();
        StatusText.Text = LF("TrimEndSet", FormatTime(_trimEndSeconds.Value));
    }

    private void TrimMarkerButton_PreviewMouseWheel(object sender, MouseWheelEventArgs e)
    {
        if (_duration <= TimeSpan.Zero || _isPlaying) return;
        _activeTrimMarker = ReferenceEquals(sender, TrimStartButton) ? TrimMarker.Start : TrimMarker.End;
        if (_activeTrimMarker == TrimMarker.Start && _trimStartSeconds is null)
            _trimStartSeconds = Math.Clamp(PositionSlider.Value, 0, PositionSlider.Maximum);
        else if (_activeTrimMarker == TrimMarker.End && _trimEndSeconds is null)
            _trimEndSeconds = Math.Clamp(PositionSlider.Value, 0, PositionSlider.Maximum);

        TryAdjustActiveTrimMarker(e.Delta > 0 ? 1 : -1);
        e.Handled = true;
    }

    private void ResetTrimMarks()
    {
        _trimStartSeconds = null;
        _trimEndSeconds = null;
        _activeTrimMarker = TrimMarker.None;
        UpdateTrimButtons();
        UpdateTrimRangeVisual();
    }

    private void PresetFullTrimRange()
    {
        _trimStartSeconds = 0;
        _trimEndSeconds = PositionSlider.Maximum;
        _activeTrimMarker = TrimMarker.None;
        UpdateTrimButtons();
    }

    private bool TryAdjustActiveTrimMarker(int direction)
    {
        const double step = WheelStepSeconds;
        if (_activeTrimMarker == TrimMarker.Start && _trimStartSeconds is { } start)
        {
            var upperLimit = _trimEndSeconds is { } end ? Math.Max(0, end - step) : PositionSlider.Maximum;
            _trimStartSeconds = Math.Clamp(start + direction * step, 0, upperLimit);
            SeekToSliderValue(_trimStartSeconds.Value);
            UpdateTrimButtons();
            UpdateTrimRangeVisual();
            StatusText.Text = LF("TrimStartSet", FormatTime(_trimStartSeconds.Value));
            return true;
        }

        if (_activeTrimMarker == TrimMarker.End && _trimEndSeconds is { } endMarker)
        {
            var lowerLimit = _trimStartSeconds is { } startMarker ? Math.Min(PositionSlider.Maximum, startMarker + step) : 0;
            _trimEndSeconds = Math.Clamp(endMarker + direction * step, lowerLimit, PositionSlider.Maximum);
            SeekToSliderValue(_trimEndSeconds.Value);
            UpdateTrimButtons();
            UpdateTrimRangeVisual();
            StatusText.Text = LF("TrimEndSet", FormatTime(_trimEndSeconds.Value));
            return true;
        }

        return false;
    }

    private void UpdateTrimButtons()
    {
        TrimStartButton.Content = _trimStartSeconds is { } start
            ? $"{L("TrimStart")} {FormatTime(start)}"
            : L("TrimStart");
        TrimEndButton.Content = _trimEndSeconds is { } end
            ? $"{L("TrimEnd")} {FormatTime(end)}"
            : L("TrimEnd");
    }

    private void UpdateTrimRangeVisual()
    {
        if (_trimStartSeconds is not { } start ||
            _trimEndSeconds is not { } end ||
            end <= start ||
            PositionSlider.Maximum <= 0 ||
            PositionSlider.ActualWidth <= 0)
        {
            TrimRangeFill.Visibility = Visibility.Collapsed;
            return;
        }

        var trackWidth = PositionSlider.ActualWidth;
        var left = Math.Clamp(start / PositionSlider.Maximum * trackWidth, 0, trackWidth);
        var right = Math.Clamp(end / PositionSlider.Maximum * trackWidth, 0, trackWidth);
        Canvas.SetLeft(TrimRangeFill, left);
        TrimRangeFill.Width = Math.Max(2, right - left);
        TrimRangeFill.Visibility = Visibility.Visible;
    }

    private static string FormatTime(double seconds)
    {
        if (seconds < 0) seconds = 0;
        var totalHundredths = (long)Math.Round(seconds * 100, MidpointRounding.AwayFromZero);
        var minutes = totalHundredths / 6000;
        var remainingHundredths = totalHundredths % 6000;
        var wholeSeconds = remainingHundredths / 100;
        var hundredths = remainingHundredths % 100;
        return $"{minutes:00}:{wholeSeconds:00}:{hundredths:00}";
    }

    private static string BuildTempPath(string path, string suffix)
    {
        return Path.Combine(Path.GetDirectoryName(path)!, $"{Path.GetFileNameWithoutExtension(path)}.{Guid.NewGuid():N}{suffix}");
    }

    private static void ReplaceWithBackup(string path, string tempPath)
    {
        var backupPath = Path.Combine(Path.GetDirectoryName(path)!,
            $"{Path.GetFileNameWithoutExtension(path)}.{DateTime.Now:yyyyMMdd_HHmmss}.bak.mp3");
        File.Move(path, backupPath);
        File.Move(tempPath, path);
    }

    private static async Task RunFfmpegAsync(string ffmpegPath, IEnumerable<string> args)
    {
        var startInfo = new ProcessStartInfo(ffmpegPath)
        {
            UseShellExecute = false,
            CreateNoWindow = true,
            RedirectStandardError = true
        };
        foreach (var arg in args)
            startInfo.ArgumentList.Add(arg);

        using var process = Process.Start(startInfo) ?? throw new IOException(L("CouldNotStartFfmpeg"));
        var stderr = await process.StandardError.ReadToEndAsync();
        await process.WaitForExitAsync();
        if (process.ExitCode != 0)
            throw new InvalidOperationException(LF("FfmpegExitCode", process.ExitCode, stderr));
    }

    private static string? FindFfmpegPath()
    {
        var local = Path.Combine(AppContext.BaseDirectory, "ffmpeg.exe");
        if (File.Exists(local)) return local;

        var path = Environment.GetEnvironmentVariable("PATH") ?? string.Empty;
        foreach (var directory in path.Split(Path.PathSeparator, StringSplitOptions.RemoveEmptyEntries))
        {
            var candidate = Path.Combine(directory.Trim(), "ffmpeg.exe");
            if (File.Exists(candidate)) return candidate;
        }

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

public sealed class RecordedTrackItem : INotifyPropertyChanged
{
    public RecordedTrackItem(string path)
    {
        Path = path;
        Refresh();
    }

    public string Path { get; private set; }
    public bool IsEdited { get; private set; }
    public string DisplayName => System.IO.Path.GetFileNameWithoutExtension(Path);
    public string Info
    {
        get
        {
            var info = new FileInfo(Path);
            return $"{info.LastWriteTime:dd.MM.yyyy HH:mm} · {info.Length / 1024.0 / 1024.0:0.0} MB";
        }
    }

    public event PropertyChangedEventHandler? PropertyChanged;

    public void UpdatePath(string path)
    {
        Path = path;
        PropertyChanged?.Invoke(this, new PropertyChangedEventArgs(nameof(Path)));
    }

    public void MarkEdited()
    {
        IsEdited = true;
        PropertyChanged?.Invoke(this, new PropertyChangedEventArgs(nameof(IsEdited)));
    }

    public void Refresh()
    {
        IsEdited = Id3TagReader.Read(Path).IsEdited;
        PropertyChanged?.Invoke(this, new PropertyChangedEventArgs(nameof(IsEdited)));
        PropertyChanged?.Invoke(this, new PropertyChangedEventArgs(nameof(DisplayName)));
        PropertyChanged?.Invoke(this, new PropertyChangedEventArgs(nameof(Info)));
    }
}
