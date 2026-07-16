using System.Collections.ObjectModel;
using System.IO;
using System.Net.Http;
using System.Text;
using System.Text.Json;
using System.Text.Json.Serialization;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Input;

namespace WebStream;

public partial class StationSearchWindow : Window
{
    private static readonly HttpClient Client = new();
    private readonly MainWindow _mainWindow;
    private readonly ObservableCollection<SearchStationItem> _stations = new();
    private readonly string _searchStatePath = Path.Combine(
        Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData), "WebStream", "search.ini");
    private double _lastPreviewVolume = 0.4;

    public StationSearchWindow(MainWindow mainWindow)
    {
        _mainWindow = mainWindow;
        InitializeComponent();
        LocalizationManager.LanguageChanged += LocalizationManager_LanguageChanged;
        StationsListBox.ItemsSource = _stations;
        PreviewVolumeSlider.Value = _mainWindow.CurrentPlaybackVolumePercent;
        LoadSearchState();
        SearchTextBox.Focus();
    }

    private static string L(string key) => LocalizationManager.Get(key);

    private static string LF(string key, params object[] args) => LocalizationManager.Format(key, args);

    private void LocalizationManager_LanguageChanged(object? sender, EventArgs e)
    {
        UpdatePreviewMuteButton();
        if (_stations.Count == 0)
            SearchStatusText.Text = L("SearchPrompt");
    }

    private async void SearchButton_Click(object sender, RoutedEventArgs e)
    {
        await SearchAsync();
    }

    private async void SearchTextBox_KeyDown(object sender, System.Windows.Input.KeyEventArgs e)
    {
        if (e.Key != Key.Enter) return;
        e.Handled = true;
        await SearchAsync();
    }

    private async Task SearchAsync()
    {
        var query = SearchTextBox.Text.Trim();
        if (string.IsNullOrWhiteSpace(query)) return;

        _stations.Clear();
        SelectedUrlTextBox.Clear();
        SearchStatusText.Text = L("SearchLooking");
        SearchStatusText.Visibility = Visibility.Visible;
        SearchButton.IsEnabled = false;

        try
        {
            var results = await SearchRadioBrowserAsync(query);
            foreach (var station in results)
                _stations.Add(station);

            SaveSearchState();
            SearchStatusText.Text = _stations.Count == 0
                ? L("SearchNothingFound")
                : string.Empty;
            SearchStatusText.Visibility = _stations.Count == 0 ? Visibility.Visible : Visibility.Collapsed;
        }
        catch (HttpRequestException)
        {
            SearchStatusText.Text = L("SearchConnectionError");
            SearchStatusText.Visibility = Visibility.Visible;
        }
        catch (JsonException)
        {
            SearchStatusText.Text = L("SearchBadResponse");
            SearchStatusText.Visibility = Visibility.Visible;
        }
        finally
        {
            SearchButton.IsEnabled = true;
        }
    }

    private static async Task<List<SearchStationItem>> SearchRadioBrowserAsync(string query)
    {
        var variants = BuildSearchUrls(query);
        var found = new Dictionary<string, SearchStationItem>(StringComparer.OrdinalIgnoreCase);

        foreach (var url in variants)
        {
            using var request = new HttpRequestMessage(HttpMethod.Get, url);
            request.Headers.TryAddWithoutValidation("User-Agent", "WebStream/1.0");
            using var response = await Client.SendAsync(request);
            response.EnsureSuccessStatusCode();

            var stream = await response.Content.ReadAsStreamAsync();
            var stations = await JsonSerializer.DeserializeAsync<List<RadioBrowserStation>>(stream);
            if (stations is null) continue;

            foreach (var station in stations)
            {
                var item = SearchStationItem.From(station);
                if (string.IsNullOrWhiteSpace(item.StreamUrl)) continue;
                found.TryAdd(item.Key, item);
            }
        }

        return found.Values
            .OrderByDescending(station => station.Votes)
            .ThenBy(station => station.Name, StringComparer.CurrentCultureIgnoreCase)
            .Take(80)
            .ToList();
    }

    private static IEnumerable<string> BuildSearchUrls(string query)
    {
        var escaped = Uri.EscapeDataString(query);
        var baseUrl = "https://de1.api.radio-browser.info/json/stations/search";
        var common = "hidebroken=true&limit=40&order=votes&reverse=true";

        yield return $"{baseUrl}?name={escaped}&{common}";
        yield return $"{baseUrl}?tag={escaped}&{common}";
        yield return $"{baseUrl}?country={escaped}&{common}";
        if (query.Length == 2 && query.All(char.IsLetter))
            yield return $"{baseUrl}?countrycode={Uri.EscapeDataString(query.ToUpperInvariant())}&{common}";
    }

    private void StationsListBox_SelectionChanged(object sender, SelectionChangedEventArgs e)
    {
        if (StationsListBox.SelectedItem is not SearchStationItem station) return;
        SelectedUrlTextBox.Text = station.StreamUrl;
    }

    private void StationsListBox_MouseDoubleClick(object sender, MouseButtonEventArgs e)
    {
        PlaySelectedStation();
    }

    private void PlaySelectedButton_Click(object sender, RoutedEventArgs e)
    {
        PlaySelectedStation();
    }

    private void PlaySelectedStation()
    {
        if (StationsListBox.SelectedItem is not SearchStationItem station
            || !Uri.TryCreate(station.StreamUrl, UriKind.Absolute, out _))
            return;

        _mainWindow.PreviewSearchStation(station);
        SearchStatusText.Text = LF("SearchListening", station.Name);
        SearchStatusText.Visibility = Visibility.Visible;
    }

    private void PauseButton_Click(object sender, RoutedEventArgs e)
    {
        _mainWindow.PausePlaybackFromSearch();
    }

    private void StopPreviewButton_Click(object sender, RoutedEventArgs e)
    {
        _mainWindow.StopPlaybackFromSearch();
    }

    private void PreviewVolumeSlider_ValueChanged(object sender, RoutedPropertyChangedEventArgs<double> e)
    {
        if (!IsLoaded) return;
        var volume = Math.Clamp(e.NewValue / 100, 0, 1);
        _mainWindow.SetSearchPlaybackVolume(e.NewValue);
        UpdatePreviewMuteButton();
        if (volume > 0) _lastPreviewVolume = volume;
    }

    private void PreviewMuteButton_Click(object sender, RoutedEventArgs e)
    {
        if (_mainWindow.CurrentPlaybackVolumePercent > 0)
        {
            _lastPreviewVolume = _mainWindow.CurrentPlaybackVolumePercent / 100;
            PreviewVolumeSlider.Value = 0;
            return;
        }

        PreviewVolumeSlider.Value = Math.Max(_lastPreviewVolume, 0.4) * 100;
    }

    private void AddButton_Click(object sender, RoutedEventArgs e)
    {
        if (StationsListBox.SelectedItem is not SearchStationItem station || string.IsNullOrWhiteSpace(station.StreamUrl)) return;

        var added = _mainWindow.AddSearchStationToPlaylist(station);
        SearchStatusText.Text = added
            ? LF("SearchAdded", station.Name)
            : LF("SearchAlreadyInPlaylist", station.Name);
        SearchStatusText.Visibility = Visibility.Visible;
    }

    private void CloseButton_Click(object sender, RoutedEventArgs e)
    {
        Close();
    }

    private void LoadSearchState()
    {
        try
        {
            if (!File.Exists(_searchStatePath)) return;

            var values = ReadIniValues(File.ReadAllLines(_searchStatePath, Encoding.UTF8));
            if (values.TryGetValue("Query", out var query))
                SearchTextBox.Text = DecodeValue(query);

            if (!values.TryGetValue("Results", out var resultsValue)) return;
            var resultsJson = DecodeValue(resultsValue);
            var stations = JsonSerializer.Deserialize<List<SearchStationItem>>(resultsJson);
            if (stations is null) return;

            foreach (var station in stations.Where(station => !string.IsNullOrWhiteSpace(station.StreamUrl)))
                _stations.Add(station);

            SearchStatusText.Text = _stations.Count == 0 ? L("SearchPrompt") : string.Empty;
            SearchStatusText.Visibility = _stations.Count == 0 ? Visibility.Visible : Visibility.Collapsed;
        }
        catch (IOException)
        {
        }
        catch (UnauthorizedAccessException)
        {
        }
        catch (JsonException)
        {
        }
        catch (FormatException)
        {
        }
    }

    private void SaveSearchState()
    {
        try
        {
            Directory.CreateDirectory(Path.GetDirectoryName(_searchStatePath)!);
            var resultsJson = JsonSerializer.Serialize(_stations.ToList());
            var lines = new[]
            {
                "[Search]",
                $"Query={EncodeValue(SearchTextBox.Text)}",
                $"Results={EncodeValue(resultsJson)}"
            };
            File.WriteAllLines(_searchStatePath, lines, Encoding.UTF8);
        }
        catch (IOException)
        {
        }
        catch (UnauthorizedAccessException)
        {
        }
    }

    private void UpdatePreviewMuteButton()
    {
        PreviewMuteButton.Content = PreviewVolumeSlider.Value <= 0 ? L("MuteOff") : L("MuteOn");
    }

    protected override void OnClosed(EventArgs e)
    {
        LocalizationManager.LanguageChanged -= LocalizationManager_LanguageChanged;
        SaveSearchState();
        base.OnClosed(e);
    }

    private static Dictionary<string, string> ReadIniValues(IEnumerable<string> lines)
    {
        var values = new Dictionary<string, string>(StringComparer.OrdinalIgnoreCase);
        foreach (var line in lines)
        {
            var trimmed = line.Trim();
            if (string.IsNullOrWhiteSpace(trimmed) || trimmed.StartsWith('[')) continue;

            var separator = trimmed.IndexOf('=');
            if (separator <= 0) continue;
            values[trimmed[..separator].Trim()] = trimmed[(separator + 1)..].Trim();
        }

        return values;
    }

    private static string EncodeValue(string? value)
    {
        return Convert.ToBase64String(Encoding.UTF8.GetBytes(value ?? string.Empty));
    }

    private static string DecodeValue(string value)
    {
        return Encoding.UTF8.GetString(Convert.FromBase64String(value));
    }
}

public sealed record SearchStationItem(
    string Key,
    string Name,
    string StreamUrl,
    string Description,
    string Details,
    int Votes)
{
    public string VotesText => $"★ {Votes}";

    public static SearchStationItem From(RadioBrowserStation station)
    {
        var streamUrl = (!string.IsNullOrWhiteSpace(station.UrlResolved) ? station.UrlResolved : station.Url) ?? string.Empty;
        var tags = NormalizeList(station.Tags);
        var codec = string.IsNullOrWhiteSpace(station.Codec) ? "?" : station.Codec.ToUpperInvariant();
        var bitrate = station.Bitrate > 0 ? $"{station.Bitrate}kbps" : "?";
        var country = string.IsNullOrWhiteSpace(station.Country) ? station.CountryCode : station.Country;
        var description = string.IsNullOrWhiteSpace(tags) ? country ?? string.Empty : tags;
        var details = $"{country} | {codec} {bitrate}";
        if (!string.IsNullOrWhiteSpace(tags))
            details += $" | {tags}";

        return new SearchStationItem(
            string.IsNullOrWhiteSpace(station.StationUuid) ? streamUrl : station.StationUuid,
            string.IsNullOrWhiteSpace(station.Name) ? LocalizationManager.Get("Untitled") : station.Name,
            streamUrl,
            description,
            details,
            station.Votes);
    }

    private static string NormalizeList(string? value)
    {
        if (string.IsNullOrWhiteSpace(value)) return string.Empty;
        var parts = value.Split(',', StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries)
            .Distinct(StringComparer.OrdinalIgnoreCase)
            .Take(8);
        return string.Join(", ", parts);
    }
}

public sealed record RadioBrowserStation
{
    [JsonPropertyName("stationuuid")]
    public string? StationUuid { get; init; }

    [JsonPropertyName("name")]
    public string? Name { get; init; }

    [JsonPropertyName("url")]
    public string? Url { get; init; }

    [JsonPropertyName("url_resolved")]
    public string? UrlResolved { get; init; }

    [JsonPropertyName("tags")]
    public string? Tags { get; init; }

    [JsonPropertyName("country")]
    public string? Country { get; init; }

    [JsonPropertyName("countrycode")]
    public string? CountryCode { get; init; }

    [JsonPropertyName("codec")]
    public string? Codec { get; init; }

    [JsonPropertyName("bitrate")]
    public int Bitrate { get; init; }

    [JsonPropertyName("votes")]
    public int Votes { get; init; }
}
