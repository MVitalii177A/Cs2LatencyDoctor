using System.Net.NetworkInformation;

namespace Cs2LatencyDoctor.Core.Windows;

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
///
/// Список адаптеров берётся из System.Net.NetworkInformation — это встроенный .NET,
/// он не зависит ни от службы WMI, ни от сторонних сборок. Раньше здесь использовался
/// WMI, и там, где служба WMI тормозит или недоступна, проверка вообще не выполнялась.
/// Ключевые слова драйвера читаются из реестра: они одинаковы у Realtek и части Intel.
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

    /// <summary>
    /// Активные сетевые адаптеры. Никакого WMI: только встроенный .NET.
    /// </summary>
    public static IReadOnlyList<NetworkAdapterInfo> GetActiveAdapters()
    {
        var result = new List<NetworkAdapterInfo>();

        try
        {
            var registryAdapters = RegistryValueReader.EnumerateAdapters();

            foreach (var nic in NetworkInterface.GetAllNetworkInterfaces())
            {
                if (nic.OperationalStatus != OperationalStatus.Up) continue;
                if (nic.NetworkInterfaceType is NetworkInterfaceType.Loopback
                    or NetworkInterfaceType.Tunnel) continue;

                var description = nic.Description;

                // Приводим описание к тому, как его видит драйвер в реестре: иначе
                // не найти ключевые слова адаптера. Ищем самое похожее совпадение.
                var registryMatch = FindAdapterDescription(registryAdapters, description, nic.Name);

                ulong speed = 0;
                try { speed = (ulong)nic.Speed; } catch { /* не все адаптеры отдают скорость */ }

                var wireless = nic.NetworkInterfaceType == NetworkInterfaceType.Wireless80211
                               || description.Contains("Wi-Fi", StringComparison.OrdinalIgnoreCase)
                               || description.Contains("Wireless", StringComparison.OrdinalIgnoreCase)
                               || description.Contains("802.11", StringComparison.OrdinalIgnoreCase);

                var (discarded, errors) = ReadStatistics(nic);

                result.Add(new NetworkAdapterInfo
                {
                    Name = nic.Name,
                    Description = registryMatch ?? description,
                    IsUp = true,
                    IsWireless = wireless,
                    LinkSpeedBps = speed,
                    MacAddress = FormatMac(nic.GetPhysicalAddress()),
                    ReceivedDiscarded = discarded,
                    ReceivedErrors = errors
                });
            }
        }
        catch
        {
            // вернём то, что успели собрать
        }

        return result;
    }

    /// <summary>
    /// Найти описание адаптера так, как оно записано у драйвера. От этого зависит,
    /// найдём ли мы его настройки в реестре.
    /// </summary>
    private static string? FindAdapterDescription(
        IReadOnlyList<AdapterRegistryInstance> adapters, string description, string name)
    {
        // Точное совпадение — самый частый случай.
        foreach (var adapter in adapters)
        {
            if (string.Equals(adapter.Description, description, StringComparison.OrdinalIgnoreCase))
                return adapter.Description;
        }

        // Иначе ищем по вхождению: описания у .NET и у драйвера иногда отличаются
        // приписками вида "(2)" или "(R)".
        foreach (var adapter in adapters)
        {
            if (adapter.Description.Contains(description, StringComparison.OrdinalIgnoreCase)
                || description.Contains(adapter.Description, StringComparison.OrdinalIgnoreCase))
                return adapter.Description;
        }

        foreach (var adapter in adapters)
        {
            if (adapter.Description.Contains(name, StringComparison.OrdinalIgnoreCase)
                || name.Contains(adapter.Description, StringComparison.OrdinalIgnoreCase))
                return adapter.Description;
        }

        // Не нашли — оставляем как есть: у адаптера просто не будет знакомых параметров,
        // и проверка честно об этом скажет.
        return null;
    }

    private static (long Discarded, long Errors) ReadStatistics(NetworkInterface nic)
    {
        try
        {
            var stats = nic.GetIPv4Statistics();
            var errors = stats.IncomingPacketsWithErrors;
            var discarded = stats.IncomingPacketsDiscarded;
            return (discarded, errors);
        }
        catch
        {
            return (0, 0);
        }
    }

    private static string? FormatMac(PhysicalAddress address)
    {
        var bytes = address.GetAddressBytes();
        if (bytes.Length == 0) return null;

        return string.Join("-", bytes.Select(b => b.ToString("X2")));
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

    /// <summary>
    /// Сколько известных нам параметров задержки вообще есть у этого адаптера.
    ///
    /// Ноль означает, что драйвер другого производителя и наши правки к нему
    /// неприменимы. Это важно отличать от «всё уже настроено правильно»:
    /// в первом случае мы бессильны, во втором — работа сделана.
    /// </summary>
    public static int KnownKeywordCount(string adapterDescription) =>
        LatencyHostileKeywords.Keys.Count(k => ReadKeyword(adapterDescription, k).HasValue);
}
