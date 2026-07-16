using System.IO;
using System.Text;
using System.Windows;

namespace WebStream;

public static class LocalizationManager
{
    private const string DefaultLanguage = "RU";
    private static readonly string StringsPath = Path.Combine(AppContext.BaseDirectory, "strings.ini");
    private static readonly string LanguageStatePath = Path.Combine(
        Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData), "WebStream", "language.ini");
    private static Dictionary<string, Dictionary<string, string>> _sections = new(StringComparer.OrdinalIgnoreCase);

    public static event EventHandler? LanguageChanged;

    public static string CurrentLanguage { get; private set; } = DefaultLanguage;

    public static void Initialize()
    {
        _sections = LoadSections(StringsPath);
        CurrentLanguage = LoadSavedLanguage();
        if (!_sections.ContainsKey(CurrentLanguage)) CurrentLanguage = DefaultLanguage;
        Apply(CurrentLanguage);
    }

    public static void ToggleLanguage()
    {
        SetLanguage(string.Equals(CurrentLanguage, "RU", StringComparison.OrdinalIgnoreCase) ? "EN" : "RU");
    }

    public static void SetLanguage(string language)
    {
        if (!_sections.ContainsKey(language)) return;
        CurrentLanguage = language;
        Apply(language);
        SaveLanguage(language);
        LanguageChanged?.Invoke(null, EventArgs.Empty);
    }

    public static string Get(string key)
    {
        if (_sections.TryGetValue(CurrentLanguage, out var current) && current.TryGetValue(key, out var value))
            return value;
        if (_sections.TryGetValue(DefaultLanguage, out var fallback) && fallback.TryGetValue(key, out value))
            return value;
        return key;
    }

    public static string Format(string key, params object[] args)
    {
        return string.Format(Get(key), args);
    }

    private static void Apply(string language)
    {
        if (!_sections.TryGetValue(language, out var values)) return;
        foreach (var (key, value) in values)
            System.Windows.Application.Current.Resources[key] = value;
    }

    private static Dictionary<string, Dictionary<string, string>> LoadSections(string path)
    {
        var sections = new Dictionary<string, Dictionary<string, string>>(StringComparer.OrdinalIgnoreCase);
        if (!File.Exists(path)) return sections;

        Dictionary<string, string>? current = null;
        foreach (var rawLine in File.ReadLines(path, Encoding.UTF8))
        {
            var line = rawLine.Trim();
            if (string.IsNullOrWhiteSpace(line) || line.StartsWith(';') || line.StartsWith('#')) continue;

            if (line.StartsWith('[') && line.EndsWith(']'))
            {
                var name = line[1..^1].Trim();
                current = new Dictionary<string, string>(StringComparer.OrdinalIgnoreCase);
                sections[name] = current;
                continue;
            }

            if (current is null) continue;
            var separator = line.IndexOf('=');
            if (separator <= 0) continue;

            var key = line[..separator].Trim();
            var value = line[(separator + 1)..].Trim().Replace(@"\n", "\n", StringComparison.Ordinal);
            current[key] = value;
        }

        return sections;
    }

    private static string LoadSavedLanguage()
    {
        try
        {
            if (!File.Exists(LanguageStatePath)) return DefaultLanguage;
            var value = File.ReadAllText(LanguageStatePath, Encoding.UTF8).Trim();
            return string.IsNullOrWhiteSpace(value) ? DefaultLanguage : value;
        }
        catch (IOException)
        {
            return DefaultLanguage;
        }
        catch (UnauthorizedAccessException)
        {
            return DefaultLanguage;
        }
    }

    private static void SaveLanguage(string language)
    {
        try
        {
            Directory.CreateDirectory(Path.GetDirectoryName(LanguageStatePath)!);
            File.WriteAllText(LanguageStatePath, language, Encoding.UTF8);
        }
        catch (IOException)
        {
        }
        catch (UnauthorizedAccessException)
        {
        }
    }
}
