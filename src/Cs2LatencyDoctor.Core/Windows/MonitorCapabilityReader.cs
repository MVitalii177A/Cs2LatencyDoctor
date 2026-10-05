namespace Cs2LatencyDoctor.Core.Windows;

/// <summary>Что умеет монитор по частоте обновления.</summary>
public sealed class MonitorCapability
{
    public required string InstanceName { get; init; }

    /// <summary>Все поддерживаемые частоты, по возрастанию.</summary>
    public IReadOnlyList<double> RefreshRates { get; init; } = Array.Empty<double>();

    /// <summary>Максимальная частота, которую поддерживает монитор.</summary>
    public double MaxRefreshRate { get; init; }

    /// <summary>Частота, выставленная в Windows прямо сейчас.</summary>
    public int CurrentDesktopRefreshRate { get; init; }

    public bool HasData => RefreshRates.Count > 0;

    /// <summary>
    /// Работает ли рабочий стол не на максимальной частоте монитора.
    ///
    /// Сравниваем с допуском: мониторы сообщают паспортную частоту вроде 179.96 Гц,
    /// а Windows округляет её до 179 или 180 — это норма, а не проблема.
    /// </summary>
    public bool DesktopBelowMaximum =>
        HasData && CurrentDesktopRefreshRate > 0 &&
        MaxRefreshRate - CurrentDesktopRefreshRate > 1.5;

    public string Text => HasData
        ? $"максимум {MaxRefreshRate:0.##} Гц, в Windows выставлено {CurrentDesktopRefreshRate} Гц"
        : "данные о поддерживаемых частотах недоступны";
}

/// <summary>
/// Чтение возможностей монитора. Список частот берётся из EDID через WMI,
/// поэтому вызов ограничен по времени: та же служба, что тормозит с адаптерами,
/// не должна подвешивать проверку.
/// </summary>
public static class MonitorCapabilityReader
{
    /// <summary>Сколько ждать ответа WMI.</summary>
    public static int TimeoutSeconds { get; set; } = 8;

    public static MonitorCapability? Read()
    {
        try
        {
            var task = Task.Run(Query);
            return task.Wait(TimeSpan.FromSeconds(TimeoutSeconds)) ? task.Result : null;
        }
        catch
        {
            return null;
        }
    }

    private static MonitorCapability? Query()
    {
        try
        {
            var currentRefresh = ReadCurrentDesktopRefreshRate();
            var monitors = new List<MonitorCapability>();

            // Перечисление мониторов и их режимов через WMI: это единственный способ
            // узнать паспортные частоты, не залезая в панель драйвера видеокарты.
            var searcher = new System.Management.ManagementObjectSearcher(
                new System.Management.ManagementScope(@"root\wmi"),
                new System.Management.ObjectQuery("SELECT * FROM WmiMonitorListedSupportedSourceModes"));

            foreach (var item in searcher.Get())
            {
                using var _ = item;

                var instanceName = item["InstanceName"] as string ?? "?";

                var modes = item["MonitorSourceModes"] as System.Management.ManagementBaseObject[];
                if (modes is null) continue;

                var rates = new List<double>();

                foreach (var mode in modes)
                {
                    using (mode)
                    {
                        var numerator = Convert.ToDouble(mode["VerticalRefreshRateNumerator"] ?? 0);
                        var denominator = Convert.ToDouble(mode["VerticalRefreshRateDenominator"] ?? 0);

                        if (denominator <= 0) continue;

                        var rate = numerator / denominator;
                        if (rate > 1) rates.Add(Math.Round(rate, 2));
                    }
                }

                if (rates.Count == 0) continue;

                monitors.Add(new MonitorCapability
                {
                    InstanceName = instanceName,
                    RefreshRates = rates.Distinct().OrderBy(r => r).ToList(),
                    MaxRefreshRate = rates.Max(),
                    CurrentDesktopRefreshRate = currentRefresh
                });
            }

            // Если мониторов несколько, берём тот, у которого выше частота:
            // в игре почти всегда используется основной, а он у геймеров самый быстрый.
            return monitors.OrderByDescending(m => m.MaxRefreshRate).FirstOrDefault();
        }
        catch
        {
            return null;
        }
    }

    private static int ReadCurrentDesktopRefreshRate()
    {
        try
        {
            foreach (var controller in new System.Management.ManagementObjectSearcher(
                         "SELECT CurrentRefreshRate FROM Win32_VideoController").Get())
            {
                using (controller)
                {
                    var rate = Convert.ToInt32(controller["CurrentRefreshRate"] ?? 0);
                    if (rate > 1) return rate;
                }
            }
        }
        catch
        {
            // не критично: тогда просто не сможем сравнить с текущей частотой
        }

        return 0;
    }
}
