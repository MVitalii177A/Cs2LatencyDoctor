using System.Text.RegularExpressions;
using Microsoft.Win32;

namespace Cs2LatencyDoctor.Core.Windows;

/// <summary>Настройки видео CS2, вычитанные из cs2_video.txt.</summary>
public sealed class Cs2VideoSettings
{
    public int Fullscreen { get; init; }
    public int NoWindowBorder { get; init; }
    public int VSync { get; init; }
    public int LowLatency { get; init; }
    public int Msaa { get; init; }
    public int RefreshNumerator { get; init; }
    public int RefreshDenominator { get; init; }
    public int ResolutionWidth { get; init; }
    public int ResolutionHeight { get; init; }
    public required string FilePath { get; init; }
}

/// <summary>Найденная установка CS2 и её конфиги.</summary>
public sealed class Cs2Installation
{
    public required string GameFolder { get; init; }
    public required string SteamRoot { get; init; }
    public string? SteamUserId { get; init; }
    public Cs2VideoSettings? VideoSettings { get; init; }
    public string? ConfigFolder { get; init; }

    /// <summary>Есть ли наш конфиг cs2_latency.cfg в папке игры.</summary>
    public bool HasLatencyConfig =>
        ConfigFolder is not null && File.Exists(Path.Combine(ConfigFolder, "cs2_latency.cfg"));
}

/// <summary>
/// Поиск CS2 на диске. Не полагаемся на один путь: Steam может стоять где угодно,
/// а библиотеки игр — на других дисках.
/// </summary>
public static class Cs2Locator
{
    private const string SteamRegistryPath = @"SOFTWARE\Valve\Steam";
    private const string SteamRegistryPathWow = @"SOFTWARE\WOW6432Node\Valve\Steam";
    private const string GameRelative = @"steamapps\common\Counter-Strike Global Offensive";

    public static Cs2Installation? Find()
    {
        foreach (var steamRoot in FindSteamRoots())
        {
            foreach (var library in FindLibraryFolders(steamRoot))
            {
                var gameFolder = Path.Combine(library, GameRelative);
                if (!Directory.Exists(gameFolder)) continue;

                var userId = FindMostRecentSteamUserId(steamRoot);
                var configFolder = Path.Combine(gameFolder, "game", "csgo", "cfg");

                return new Cs2Installation
                {
                    GameFolder = gameFolder,
                    SteamRoot = steamRoot,
                    SteamUserId = userId,
                    VideoSettings = userId is null ? null : ReadVideoSettings(steamRoot, userId),
                    ConfigFolder = Directory.Exists(configFolder) ? configFolder : null
                };
            }
        }

        return null;
    }

    private static IEnumerable<string> FindSteamRoots()
    {
        var candidates = new List<string>();

        foreach (var path in new[] { SteamRegistryPath, SteamRegistryPathWow })
        {
            var value = RegistryValueReader.ReadString(RegistryHive.LocalMachine, path, "InstallPath")
                     ?? RegistryValueReader.ReadString(RegistryHive.CurrentUser, path, "SteamPath");
            if (!string.IsNullOrWhiteSpace(value))
                candidates.Add(value.Replace('/', '\\'));
        }

        candidates.Add(@"C:\Program Files (x86)\Steam");
        candidates.Add(@"C:\Program Files\Steam");

        return candidates.Where(Directory.Exists).Distinct(StringComparer.OrdinalIgnoreCase);
    }

    /// <summary>Библиотеки Steam: основная папка + всё из libraryfolders.vdf.</summary>
    private static IEnumerable<string> FindLibraryFolders(string steamRoot)
    {
        yield return steamRoot;

        var vdf = Path.Combine(steamRoot, "steamapps", "libraryfolders.vdf");
        if (!File.Exists(vdf)) yield break;

        string content;
        try { content = File.ReadAllText(vdf); }
        catch { yield break; }

        // В vdf пути лежат в поле "path", с двойными обратными слэшами.
        foreach (Match match in Regex.Matches(content, "\"path\"\\s+\"([^\"]+)\""))
        {
            var path = match.Groups[1].Value.Replace(@"\\", @"\");
            if (Directory.Exists(path)) yield return path;
        }
    }

    /// <summary>
    /// SteamID берём по самому свежему localconfig.vdf: у кого недавно играли, тот и активный.
    /// Это надёжнее, чем угадывать по номеру папки.
    /// </summary>
    private static string? FindMostRecentSteamUserId(string steamRoot)
    {
        var userData = Path.Combine(steamRoot, "userdata");
        if (!Directory.Exists(userData)) return null;

        return Directory.GetDirectories(userData)
            .Select(dir => new
            {
                Name = Path.GetFileName(dir),
                Config = Path.Combine(dir, "config", "localconfig.vdf")
            })
            .Where(x => x.Name.All(char.IsDigit) && File.Exists(x.Config))
            .OrderByDescending(x => File.GetLastWriteTimeUtc(x.Config))
            .Select(x => x.Name)
            .FirstOrDefault();
    }

    public static Cs2VideoSettings? ReadVideoSettings(string steamRoot, string userId)
    {
        var path = Path.Combine(steamRoot, "userdata", userId, "730", "local", "cfg", "cs2_video.txt");
        if (!File.Exists(path)) return null;

        string content;
        try { content = File.ReadAllText(path); }
        catch { return null; }

        int Get(string key, int fallback = 0)
        {
            var match = Regex.Match(content, "\"" + Regex.Escape(key) + "\"\\s+\"(-?\\d+)\"");
            return match.Success && int.TryParse(match.Groups[1].Value, out var v) ? v : fallback;
        }

        return new Cs2VideoSettings
        {
            FilePath = path,
            Fullscreen = Get("setting.fullscreen"),
            NoWindowBorder = Get("setting.nowindowborder", 1),
            VSync = Get("setting.mat_vsync"),
            LowLatency = Get("setting.r_low_latency"),
            Msaa = Get("setting.msaa_samples"),
            RefreshNumerator = Get("setting.refreshrate_numerator"),
            RefreshDenominator = Get("setting.refreshrate_denominator", 1),
            ResolutionWidth = Get("setting.defaultres"),
            ResolutionHeight = Get("setting.defaultresheight")
        };
    }
}
