using System.Text.RegularExpressions;

namespace Cs2LatencyDoctor.Core.Windows;

/// <summary>Параметры запуска CS2 из Steam.</summary>
public sealed class Cs2LaunchOptions
{
    public required string Raw { get; init; }

    /// <summary>Отдельные параметры, как их видит Steam.</summary>
    public IReadOnlyList<string> Tokens { get; init; } = Array.Empty<string>();

    public bool Contains(string token) =>
        Tokens.Any(t => string.Equals(t, token, StringComparison.OrdinalIgnoreCase));

    /// <summary>Искать по началу: например, +fps_max ловит и «+fps_max 0», и «+fps_max=0».</summary>
    public bool ContainsPrefix(string prefix) =>
        Tokens.Any(t => t.StartsWith(prefix, StringComparison.OrdinalIgnoreCase));

    public bool IsEmpty => string.IsNullOrWhiteSpace(Raw);
}

/// <summary>
/// Чтение параметров запуска игры из настроек Steam.
///
/// Ключ в файле локализован: на русской Windows это «Параметры запуска», на английской
/// «LaunchOptions». Поэтому ищем не по имени ключа, а по структуре блока игры — иначе
/// проверка молча не работала бы на половине систем.
/// </summary>
public static class Cs2LaunchOptionsReader
{
    /// <summary>Идентификатор Counter-Strike 2 в Steam.</summary>
    public const string Cs2AppId = "730";

    public static Cs2LaunchOptions? Read(string steamRoot, string steamUserId)
    {
        var path = Path.Combine(steamRoot, "userdata", steamUserId, "config", "localconfig.vdf");
        if (!File.Exists(path)) return null;

        string content;
        try { content = File.ReadAllText(path); }
        catch { return null; }

        var block = FindAppBlock(content, Cs2AppId);
        if (block is null) return null;

        // Ищем «"<любое имя>" "<значение>"» там, где значение похоже на параметры запуска:
        // содержит ключ с дефисом или плюсом. Это устойчиво к локализации имени ключа.
        foreach (Match match in Regex.Matches(block, "\"([^\"]{1,40})\"\\s+\"([^\"]*)\""))
        {
            var key = match.Groups[1].Value;
            var value = match.Groups[2].Value;

            var looksLikeLaunchOptions =
                value.Contains(" -", StringComparison.Ordinal) ||
                value.StartsWith('-') ||
                value.Contains(" +", StringComparison.Ordinal) ||
                value.StartsWith('+') ||
                key.Contains("Launch", StringComparison.OrdinalIgnoreCase);

            if (!looksLikeLaunchOptions) continue;

            return new Cs2LaunchOptions
            {
                Raw = value,
                Tokens = value.Split(' ', StringSplitOptions.RemoveEmptyEntries)
            };
        }

        // Ключ есть, но значение пустое — это тоже ответ: параметров нет.
        if (Regex.IsMatch(block, "\"[^\"]*Launch[^\"]*\"\\s+\"\\s*\""))
            return new Cs2LaunchOptions { Raw = string.Empty };

        return new Cs2LaunchOptions { Raw = string.Empty };
    }

    /// <summary>
    /// Вырезать блок нужной игры из localconfig.vdf. Файл в формате Valve Data,
    /// поэтому ищем «"730"» и берём следующую забалансированную пару скобок.
    /// </summary>
    private static string? FindAppBlock(string content, string appId)
    {
        var marker = "\"" + appId + "\"";
        var index = content.IndexOf(marker, StringComparison.Ordinal);

        while (index >= 0)
        {
            var braceStart = content.IndexOf('{', index);
            if (braceStart < 0) return null;

            var depth = 0;
            for (var i = braceStart; i < content.Length; i++)
            {
                if (content[i] == '{') depth++;
                else if (content[i] == '}')
                {
                    depth--;
                    if (depth == 0)
                    {
                        var block = content.Substring(braceStart, i - braceStart + 1);

                        // Блок игры должен содержать признаки именно игры, а не вложенной папки.
                        if (block.Contains("Playtime", StringComparison.OrdinalIgnoreCase) ||
                            block.Contains("LastPlayed", StringComparison.OrdinalIgnoreCase) ||
                            block.Contains("Launch", StringComparison.OrdinalIgnoreCase))
                            return block;

                        break;
                    }
                }
            }

            index = content.IndexOf(marker, index + 1, StringComparison.Ordinal);
        }

        return null;
    }
}
