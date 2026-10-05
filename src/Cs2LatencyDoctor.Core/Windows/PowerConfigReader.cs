namespace Cs2LatencyDoctor.Core.Windows;

/// <summary>Активная схема электропитания и интересующие нас параметры.</summary>
public sealed class PowerState
{
    public string? SchemeName { get; init; }
    public string? SchemeGuid { get; init; }

    /// <summary>USB selective suspend: 0 = запрещено (хорошо), 1 = разрешено (плохо). null = настройки нет.</summary>
    public int? UsbSelectiveSuspendAc { get; init; }
    public int? UsbSelectiveSuspendDc { get; init; }
}

/// <summary>
/// Чтение параметров питания через powercfg — нужные настройки (USB selective suspend)
/// через реестр читаются ненадёжно, а powercfg отдаёт их однозначно.
/// </summary>
public static class PowerConfigReader
{
    private const string UsbSubGroup = "2a737441-1930-4402-8d77-b2bebba308a3";
    private const string UsbSuspendSetting = "48e6b7a6-50f5-4782-a5d4-53bb8f07e226";

    public static PowerState Read()
    {
        var (guid, name) = ReadActiveScheme();

        var query = LatencyProbe
            .RunProcessAsync("powercfg.exe", $"/q SCHEME_CURRENT {UsbSubGroup} {UsbSuspendSetting}",
                CancellationToken.None)
            .GetAwaiter().GetResult() ?? string.Empty;

        return new PowerState
        {
            SchemeGuid = guid,
            SchemeName = name,
            UsbSelectiveSuspendAc = ParseIndex(query, "сети") ?? ParseIndex(query, "AC"),
            UsbSelectiveSuspendDc = ParseIndex(query, "батаре") ?? ParseIndex(query, "DC")
        };
    }

    private static (string? Guid, string? Name) ReadActiveScheme()
    {
        var output = LatencyProbe
            .RunProcessAsync("powercfg.exe", "/getactivescheme", CancellationToken.None)
            .GetAwaiter().GetResult();

        if (string.IsNullOrWhiteSpace(output)) return (null, null);

        // Формат: "GUID схемы питания: <guid>  (<название>)".
        // Локализация отличается, поэтому вытаскиваем guid и текст в скобках.
        var guidMatch = System.Text.RegularExpressions.Regex.Match(
            output, @"([0-9a-fA-F]{8}-[0-9a-fA-F]{4}-[0-9a-fA-F]{4}-[0-9a-fA-F]{4}-[0-9a-fA-F]{12})");
        var nameMatch = System.Text.RegularExpressions.Regex.Match(output, @"\(([^)]+)\)");

        return (guidMatch.Success ? guidMatch.Groups[1].Value : null,
                nameMatch.Success ? nameMatch.Groups[1].Value.Trim() : null);
    }

    /// <summary>
    /// Достать "Текущий индекс настройки питания от сети: 0x00000001".
    /// Ищем строку с ключевым словом и первое hex-значение после него.
    /// </summary>
    private static int? ParseIndex(string output, string keyword)
    {
        foreach (var line in output.Split('\n'))
        {
            if (!line.Contains(keyword, StringComparison.OrdinalIgnoreCase)) continue;

            var match = System.Text.RegularExpressions.Regex.Match(line, @"0x([0-9a-fA-F]+)");
            if (match.Success && int.TryParse(match.Groups[1].Value,
                    System.Globalization.NumberStyles.HexNumber, null, out var value))
                return value;
        }

        return null;
    }
}
