using System.Management;
using Cs2LatencyDoctor.Core.Windows;

namespace Cs2LatencyDoctor.Core.Checks;

/// <summary>Сведения о физическом сетевом адаптере, которые важны для задержки.</summary>
public sealed class NetworkAdapterInfo
{
    public required string Name { get; init; }
    public required string Description { get; init; }
    public bool IsUp { get; init; }
    public bool IsWireless { get; init; }
    public ulong LinkSpeedBps { get; init; }
    public string? MacAddress { get; init; }
    public long ReceivedDiscarded { get; init; }
    public long ReceivedErrors { get; init; }

    public bool IsGigabitOrFaster => LinkSpeedBps >= 1_000_000_000;
    public string LinkSpeedText => LinkSpeedBps >= 1_000_000_000
        ? $"{LinkSpeedBps / 1_000_000_000.0:0.#} Гбит/с"
        : $"{LinkSpeedBps / 1_000_000.0:0} Мбит/с";
}

/// <summary>
/// Чтение настроек сетевого адаптера, влияющих на задержку.
/// Ключевые слова одинаковы у Realtek/Intel, поэтому читаем напрямую из реестра драйвера.
/// </summary>
public static class NetworkAdapterReader
{
    /// <summary>Настройки, которые добавляют задержку или джиттер. true = плохо для игры.</summary>
    public static readonly IReadOnlyDictionary<string, string> LatencyHostileKeywords =
        new Dictionary<string, string>(StringComparer.OrdinalIgnoreCase)
        {
            ["*InterruptModeration"] = "Модерация прерываний",
            ["*SelectiveSuspend"] = "Энергосбережение адаптера (Selective Suspend)",
            ["EnableGreenEthernet"] = "Green Ethernet",
            ["GigaLite"] = "Gigabit Lite",
            ["AdvancedEEE"] = "Advanced EEE",
            ["*EEE"] = "Energy Efficient Ethernet",
            ["PowerSavingMode"] = "Режим энергосбережения"
        };

    public static IReadOnlyList<NetworkAdapterInfo> GetActiveAdapters()
    {
        var list = new List<NetworkAdapterInfo>();

        using var searcher = new ManagementObjectSearcher(
            "SELECT Name, Description, NetConnectionStatus, NetConnectionID, Speed, MACAddress, AdapterType " +
            "FROM Win32_NetworkAdapter WHERE NetConnectionStatus = 2");

        foreach (var item in searcher.Get().Cast<ManagementObject>())
        {
            using var _ = item;
            var name = item["NetConnectionID"] as string ?? item["Name"] as string ?? "?";
            var description = item["Description"] as string ?? "?";
            var adapterType = item["AdapterType"] as string ?? string.Empty;
            var speed = item["Speed"] is null ? 0UL : Convert.ToUInt64(item["Speed"]);
            var wireless = adapterType.Contains("Wireless", StringComparison.OrdinalIgnoreCase)
                           || description.Contains("Wi-Fi", StringComparison.OrdinalIgnoreCase)
                           || description.Contains("Wireless", StringComparison.OrdinalIgnoreCase);

            list.Add(new NetworkAdapterInfo
            {
                Name = name,
                Description = description,
                IsUp = true,
                IsWireless = wireless,
                LinkSpeedBps = speed,
                MacAddress = item["MACAddress"] as string
            });
        }

        return list;
    }

    /// <summary>Прочитать настройку адаптера. null — драйвер такого параметра не имеет.</summary>
    public static int? ReadKeyword(string adapterDescription, string keyword) =>
        RegistryValueReader.ReadAdapterKeyword(adapterDescription, keyword);

    /// <summary>Все найденные "плохие" настройки адаптера: keyword -> текущее значение (1).</summary>
    public static IReadOnlyDictionary<string, int> ReadLatencyHostileSettings(string adapterDescription)
    {
        var found = new Dictionary<string, int>(StringComparer.OrdinalIgnoreCase);

        foreach (var keyword in LatencyHostileKeywords.Keys)
        {
            var value = ReadKeyword(adapterDescription, keyword);
            if (value.HasValue && value.Value != 0)
                found[keyword] = value.Value;
        }

        return found;
    }
}
