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

    /// <summary>
    /// Поддерживает ли монитор переменную частоту обновления (VRR).
    ///
    /// Это важно для G-Sync: NVIDIA включает её только на мониторах,
    /// которые заявляют такую поддержку. Мы смотрим это в самих данных
    /// монитора (EDID), а не в базе драйвера: база зашифрована и прочитать
    /// её нельзя, а EDID лежит в открытом виде.
    ///
    /// null означает «не удалось определить» — это не то же самое, что «нет».
    /// </summary>
    public bool? SupportsVariableRefreshRate { get; init; }

    /// <summary>Диапазон частот, внутри которого работает синхронизация.</summary>
    public int? VariableRefreshMinHz { get; init; }

    /// <summary>Верхняя граница диапазона синхронизации.</summary>
    public int? VariableRefreshMaxHz { get; init; }

    /// <summary>Как определили поддержку — для отчёта, чтобы не было «на веру».</summary>
    public string? VariableRefreshSource { get; init; }

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

                var vrr = ReadVariableRefreshRate(instanceName);

                monitors.Add(new MonitorCapability
                {
                    InstanceName = instanceName,
                    RefreshRates = rates.Distinct().OrderBy(r => r).ToList(),
                    MaxRefreshRate = rates.Max(),
                    CurrentDesktopRefreshRate = currentRefresh,
                    SupportsVariableRefreshRate = vrr.Supported,
                    VariableRefreshMinHz = vrr.MinHz,
                    VariableRefreshMaxHz = vrr.MaxHz,
                    VariableRefreshSource = vrr.Source
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

    // ------------------------------------------------- поддержка переменной частоты

    /// <summary>Что удалось узнать о переменной частоте конкретного монитора.</summary>
    private readonly record struct VariableRefreshInfo(
        bool? Supported, int? MinHz, int? MaxHz, string? Source);

    /// <summary>
    /// Поддерживает ли монитор переменную частоту обновления.
    ///
    /// Откуда берём. Данные монитора (EDID) лежат в реестре в открытом виде —
    /// в отличие от настроек драйвера видеокарты, которые зашифрованы. Поэтому
    /// посмотреть возможности монитора можно, а вот включена ли синхронизация
    /// в панели управления — нет. Об этом честно скажем в отчёте.
    ///
    /// Где искать. Производители заявляют поддержку в «расширенном блоке»
    /// данных монитора. Мы ищем два признака:
    ///   1. Блок AMD FreeSync — по нему работают почти все мониторы с G-Sync
    ///      Compatible, включая LG UltraGear. Внутри блока указан диапазон частот.
    ///   2. Отметку Adaptive-Sync в тексте блока.
    ///
    /// Важное ограничение, проверенное на практике. Windows хранит в реестре
    /// только ПЕРВЫЙ блок данных — 128 байт. Расширенный блок, где как раз
    /// и лежат сведения о синхронизации, туда не попадает, хотя монитор его
    /// заявляет (в байте 0x7E указано их число). Прочитать его можно только
    /// через системный интерфейс, требующий прав администратора, — а проверка
    /// должна работать и без них.
    ///
    /// Поэтому часто получается null, то есть «определить не удалось».
    /// Это не ошибка и не «не поддерживает»: мы честно говорим, что не знаем,
    /// и даём человеку способ проверить самому.
    /// </summary>
    private static VariableRefreshInfo ReadVariableRefreshRate(string instanceName)
    {
        try
        {
            var edid = ReadEdid(instanceName);
            if (edid is null || edid.Length < 128)
                return new VariableRefreshInfo(null, null, null, null);

            var extensions = edid[0x7E];

            // Разбираем расширенные блоки, если они всё-таки есть в реестре.
            for (var block = 1; block <= extensions && block * 128 + 127 < edid.Length; block++)
            {
                var offset = block * 128;

                // Первый байт блока — его тип. 0x02 означает стандарт CEA-861:
                // именно там производители размещают сведения о синхронизации.
                if (edid[offset] != 0x02) continue;

                // Внутри блока идёт набор «блоков данных»: у каждого свой тег
                // и длина. Идём по ним по порядку, начиная с 4-го байта.
                var position = offset + 4;

                while (position < offset + 127)
                {
                    var header = edid[position];
                    var length = header & 0x1F;

                    if (length == 0) break;

                    var tag = header >> 5;

                    // Тег 3 — «блок данных поставщика»: здесь производители
                    // размещают свои расширения, в том числе FreeSync.
                    if (tag == 3 && position + 5 < edid.Length)
                    {
                        // Код 0x00001A закреплён за AMD в блоке FreeSync.
                        // Внутри: минимальная и максимальная частота кадров.
                        if (edid[position + 1] == 0x00 &&
                            edid[position + 2] == 0x00 &&
                            edid[position + 3] == 0x00)
                        {
                            // Байты 4 и 5 блока — нижняя и верхняя граница диапазона.
                            var minHz = edid[position + 4];
                            var maxHz = edid[position + 5];

                            // У AMD младший бит верхней границы зарезервирован,
                            // поэтому старшие семь бит берём отдельно.
                            var maxClean = maxHz & 0xFE;

                            if (minHz > 0 && maxClean > minHz)
                            {
                                return new VariableRefreshInfo(
                                    true, minHz, maxClean,
                                    "блок FreeSync в данных монитора: диапазон " +
                                    minHz + "–" + maxClean + " Гц");
                            }
                        }
                    }

                    position += length + 1;
                }

                var text = System.Text.Encoding.ASCII.GetString(edid, offset, 128);
                if (text.Contains("FreeSync", StringComparison.OrdinalIgnoreCase) ||
                    text.Contains("Adaptive-Sync", StringComparison.OrdinalIgnoreCase))
                {
                    return new VariableRefreshInfo(true, null, null,
                        "в данных монитора указана поддержка Adaptive-Sync");
                }
            }

            return new VariableRefreshInfo(null, null, null, null);
        }
        catch
        {
            // Реестр недоступен или данных нет — это не «нет поддержки»,
            // а «не удалось узнать». Так и скажем в отчёте.
            return new VariableRefreshInfo(null, null, null, null);
        }
    }

    /// <summary>
    /// Читает данные монитора (EDID) из реестра.
    ///
    /// Имя экземпляра выглядит так: DISPLAY\GSM5C8A\5&amp;18789717&amp;0&amp;UID4353_0.
    /// Из него берём производителя и код модели, а сам блок данных лежит рядом,
    /// в разделе «Device Parameters».
    /// </summary>
    private static byte[]? ReadEdid(string instanceName)
    {
        try
        {
            var parts = instanceName.Split('\\');
            if (parts.Length < 2) return null;

            var hardwareId = parts[0] + "\\" + parts[1];

            using var baseKey = Microsoft.Win32.Registry.LocalMachine.OpenSubKey(
                @"SYSTEM\CurrentControlSet\Enum\" + hardwareId);

            if (baseKey is null) return null;

            foreach (var instance in baseKey.GetSubKeyNames())
            {
                using var parameters = baseKey.OpenSubKey(instance + @"\Device Parameters");
                if (parameters?.GetValue("EDID") is byte[] edid && edid.Length >= 128)
                    return edid;
            }
        }
        catch
        {
            // Нет доступа или раздел отсутствует — вернём null.
        }

        return null;
    }
}
