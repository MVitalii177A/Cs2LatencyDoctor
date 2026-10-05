using System.Text.RegularExpressions;

namespace Cs2LatencyDoctor.Core.Windows;

/// <summary>
/// Точечная правка cs2_video.txt. Файл небольшой и имеет простой формат
/// "ключ" "значение", поэтому меняем только нужные строки и ничего больше не трогаем.
/// </summary>
public static class Cs2VideoFileEditor
{
    /// <summary>Выставить exclusive fullscreen: fullscreen=1 и nowindowborder=0.</summary>
    public static string SetFullscreenExclusive(string content) => SetValues(content, "1", "0");

    /// <summary>Выставить произвольные значения режима экрана.</summary>
    public static string SetValues(string content, string fullscreen, string noWindowBorder)
    {
        var result = SetValue(content, "setting.fullscreen", fullscreen);
        result = SetValue(result, "setting.nowindowborder", noWindowBorder);
        return result;
    }

    /// <summary>
    /// Заменить значение конкретного ключа. Если ключа нет — добавляем перед закрывающей скобкой,
    /// чтобы не сломать структуру файла.
    /// </summary>
    public static string SetValue(string content, string key, string value)
    {
        var pattern = "(\"" + Regex.Escape(key) + "\"\\s+)\"(-?\\d+)\"";
        var regex = new Regex(pattern);

        if (regex.IsMatch(content))
            return regex.Replace(content, "${1}\"" + value + "\"", 1);

        // Ключа нет: вставляем строку перед последней "}".
        var closing = content.LastIndexOf('}');
        if (closing < 0) return content;

        var line = $"\t\"{key}\"\t\t\"{value}\"\r\n";
        return content.Insert(closing, line);
    }

    /// <summary>Прочитать значение ключа как число. null, если ключа нет.</summary>
    public static int? ReadValue(string content, string key)
    {
        var match = Regex.Match(content, "\"" + Regex.Escape(key) + "\"\\s+\"(-?\\d+)\"");
        return match.Success && int.TryParse(match.Groups[1].Value, out var value) ? value : null;
    }
}
