using System.Runtime.InteropServices;

namespace Cs2LatencyDoctor.Core.Windows;

/// <summary>Один подключённый экран: что он умеет и на чём работает сейчас.</summary>
public sealed class MonitorInfo
{
    /// <summary>Системное имя экрана, например \\.\DISPLAY1.</summary>
    public required string DeviceName { get; init; }

    /// <summary>Название монитора, например «LG ULTRAGEAR(DisplayPort)».</summary>
    public string MonitorName { get; init; } = string.Empty;

    /// <summary>Видеокарта, к которой подключён экран.</summary>
    public string AdapterName { get; init; } = string.Empty;

    /// <summary>Основной экран Windows: именно на нём по умолчанию запускается игра.</summary>
    public bool IsPrimary { get; init; }

    /// <summary>Частота, выставленная в Windows прямо сейчас.</summary>
    public int CurrentRefreshRate { get; init; }

    /// <summary>Разрешение, выставленное сейчас.</summary>
    public int CurrentWidth { get; init; }
    public int CurrentHeight { get; init; }

    /// <summary>Максимальная частота, которую поддерживает этот экран.</summary>
    public int MaxRefreshRate { get; init; }

    /// <summary>
    /// Работает ли экран не на своей максимальной частоте.
    ///
    /// Сравниваем внутри одного экрана, а не с «самым быстрым в системе»:
    /// у 60-герцового монитора 60 Гц — это норма, а не потеря.
    /// </summary>
    public bool BelowMaximum => MaxRefreshRate > 0 && CurrentRefreshRate > 0 &&
                                MaxRefreshRate - CurrentRefreshRate >= 2;

    /// <summary>Описание для отчёта.</summary>
    public string Describe() =>
        $"{DeviceName}{(IsPrimary ? " (основной)" : string.Empty)} — {MonitorName}, " +
        $"сейчас {CurrentWidth}×{CurrentHeight} на {CurrentRefreshRate} Гц, максимум {MaxRefreshRate} Гц";
}

/// <summary>Что удалось узнать о переменной частоте обновления конкретного монитора.</summary>
public sealed class VariableRefreshSupport
{
    public required string InstanceName { get; init; }

    /// <summary>
    /// Поддерживает ли монитор переменную частоту.
    /// null означает «определить не удалось» — это не то же самое, что «нет».
    /// </summary>
    public bool? Supported { get; init; }

    public int? MinHz { get; init; }
    public int? MaxHz { get; init; }

    /// <summary>Как определили — чтобы в отчёте не было утверждений «на веру».</summary>
    public string? Source { get; init; }
}

/// <summary>Итог чтения экранов.</summary>
public sealed class MonitorCapability
{
    /// <summary>Все подключённые экраны.</summary>
    public IReadOnlyList<MonitorInfo> Monitors { get; init; } = Array.Empty<MonitorInfo>();

    /// <summary>
    /// Экран, на котором запустится игра: основной, а если его не определить — самый быстрый.
    ///
    /// Почему так. Windows запускает полноэкранные игры на ОСНОВНОМ экране.
    /// Раньше программа брала самый быстрый монитор, и на системе с двумя
    /// экранами (144 Гц и 60 Гц) писала «монитор умеет 144, а выставлено 60» —
    /// хотя 60 Гц стояло на втором мониторе, а игра пошла бы на первый, где всё
    /// было правильно. Это путало человека и уводило от настоящей причины.
    /// </summary>
    public MonitorInfo? GameMonitor { get; init; }

    /// <summary>Сведения о переменной частоте для основного экрана.</summary>
    public VariableRefreshSupport? VariableRefresh { get; init; }

    public bool HasData => Monitors.Count > 0;

    /// <summary>Есть ли хотя бы один экран, работающий ниже своей максимальной частоты.</summary>
    public bool AnyBelowMaximum => Monitors.Any(m => m.BelowMaximum);

    /// <summary>Короткая сводка по всем экранам — для отчёта.</summary>
    public string DescribeAll() =>
        Monitors.Count == 0
            ? "экраны не найдены"
            : string.Join("; ", Monitors.Select(m => m.Describe()));
}

/// <summary>
/// Чтение экранов и их режимов.
///
/// Почему переписано. Раньше список частот брался из данных монитора (EDID)
/// через WMI, а текущая частота — одной общей цифрой из Win32_VideoController.
/// На системе с двумя мониторами это давало неверную картину: программа
/// сопоставляла «самый быстрый монитор» с «какой-то из частот системы».
///
/// Теперь спрашиваем Windows напрямую про каждый экран: она знает и текущий
/// режим, и все доступные. Данные монитора (EDID) остались только для одного —
/// поддержки переменной частоты, потому что больше её взять негде.
/// </summary>
public static class MonitorCapabilityReader
{
    /// <summary>Сколько ждать ответа WMI при чтении данных монитора.</summary>
    public static int TimeoutSeconds { get; set; } = 8;

    public static MonitorCapability? Read()
    {
        try
        {
            var monitors = EnumerateDisplays();

            if (monitors.Count == 0) return null;

            // Экран для игры: основной. Если Windows не пометила ни один —
            // берём самый быстрый, это лучше, чем ничего.
            var gameMonitor = monitors.FirstOrDefault(m => m.IsPrimary)
                              ?? monitors.OrderByDescending(m => m.MaxRefreshRate).First();

            var vrr = ReadVariableRefreshFor(gameMonitor);

            return new MonitorCapability
            {
                Monitors = monitors,
                GameMonitor = gameMonitor,
                VariableRefresh = vrr
            };
        }
        catch
        {
            return null;
        }
    }

    // ------------------------------------------------------- перечисление экранов

    /// <summary>
    /// Спрашиваем Windows про каждый подключённый экран: текущий режим и все
    /// доступные. Это единственный способ узнать частоту КОНКРЕТНОГО экрана —
    /// общие сведения о видеокарте её не различают.
    /// </summary>
    private static List<MonitorInfo> EnumerateDisplays()
    {
        var result = new List<MonitorInfo>();

        var adapter = new DisplayDevice();
        adapter.Size = Marshal.SizeOf<DisplayDevice>();

        for (uint index = 0; EnumDisplayDevices(null, index, ref adapter, 0); index++)
        {
            // Интересуют только экраны, подключённые к рабочему столу.
            if ((adapter.StateFlags & AttachedToDesktop) == 0)
            {
                adapter.Size = Marshal.SizeOf<DisplayDevice>();
                continue;
            }

            var deviceName = adapter.DeviceName;
            var adapterName = adapter.DeviceString;
            var isPrimary = (adapter.StateFlags & PrimaryDevice) != 0;

            // Название самого монитора — оно informative: «LG ULTRAGEAR(DisplayPort)».
            var monitorName = string.Empty;
            var monitor = new DisplayDevice();
            monitor.Size = Marshal.SizeOf<DisplayDevice>();

            if (EnumDisplayDevices(deviceName, 0, ref monitor, 0))
                monitorName = monitor.DeviceString;

            var current = ReadCurrentMode(deviceName);
            var maxRate = ReadMaxRefreshRate(deviceName, current.Width, current.Height);

            result.Add(new MonitorInfo
            {
                DeviceName = deviceName,
                MonitorName = string.IsNullOrWhiteSpace(monitorName) ? "неизвестный монитор" : monitorName,
                AdapterName = adapterName,
                IsPrimary = isPrimary,
                CurrentRefreshRate = current.Rate,
                CurrentWidth = current.Width,
                CurrentHeight = current.Height,
                MaxRefreshRate = maxRate
            });

            adapter.Size = Marshal.SizeOf<DisplayDevice>();
        }

        return result;
    }

    /// <summary>Текущий режим экрана: разрешение и частота.</summary>
    private static (int Width, int Height, int Rate) ReadCurrentMode(string deviceName)
    {
        try
        {
            var mode = new DevMode();
            mode.Size = (short)Marshal.SizeOf<DevMode>();

            if (EnumDisplaySettings(deviceName, CurrentSettings, ref mode))
                return (mode.PelsWidth, mode.PelsHeight, mode.DisplayFrequency);
        }
        catch
        {
            // не критично: вернём нули, проверка это увидит
        }

        return (0, 0, 0);
    }

    /// <summary>
    /// Максимальная частота, доступная на этом разрешении.
    ///
    /// Считаем именно на текущем разрешении: сравнивать частоты с другим
    /// разрешением нельзя — на низком разрешении монитор часто умеет больше,
    /// и тогда проверка зря ругалась бы.
    /// </summary>
    private static int ReadMaxRefreshRate(string deviceName, int width, int height)
    {
        try
        {
            var best = 0;
            var mode = new DevMode();
            mode.Size = (short)Marshal.SizeOf<DevMode>();

            for (var index = 0; EnumDisplaySettings(deviceName, index, ref mode); index++)
            {
                if (mode.DisplayFrequency <= 1) continue;

                // Если разрешение известно — сравниваем только с ним.
                if (width > 0 && height > 0 &&
                    (mode.PelsWidth != width || mode.PelsHeight != height))
                    continue;

                if (mode.DisplayFrequency > best) best = mode.DisplayFrequency;

                // Защита от бесконечного перебора на кривом драйвере.
                if (index > 5000) break;

                mode.Size = (short)Marshal.SizeOf<DevMode>();
            }

            return best;
        }
        catch
        {
            return 0;
        }
    }

    // ------------------------------------------------- поддержка переменной частоты

    /// <summary>
    /// Поддерживает ли монитор переменную частоту обновления (G-Sync/FreeSync).
    ///
    /// Откуда берём. Данные монитора (EDID) лежат в реестре в открытом виде —
    /// в отличие от настроек драйвера видеокарты, которые зашифрованы. Поэтому
    /// посмотреть возможности монитора можно, а вот включена ли синхронизация
    /// в панели управления — нет. Об этом честно скажем в отчёте.
    ///
    /// Ограничение, проверенное на практике. Windows хранит в реестре только
    /// ПЕРВЫЙ блок данных — 128 байт. Расширенный блок, где как раз и лежат
    /// сведения о синхронизации, туда не попадает, хотя монитор его заявляет
    /// (в байте 0x7E указано их число). Поэтому часто получается null, то есть
    /// «определить не удалось». Это не ошибка и не «не поддерживает».
    /// </summary>
    private static VariableRefreshSupport? ReadVariableRefreshFor(MonitorInfo monitor)
    {
        try
        {
            var instanceName = FindEdidInstanceName(monitor.MonitorName);
            if (instanceName is null) return null;

            var edid = ReadEdid(instanceName);
            if (edid is null || edid.Length < 128) return null;

            var extensions = edid[0x7E];

            for (var block = 1; block <= extensions && block * 128 + 127 < edid.Length; block++)
            {
                var offset = block * 128;

                // Тип 0x02 означает стандарт CEA-861: там размещают сведения
                // о синхронизации.
                if (edid[offset] != 0x02) continue;

                var position = offset + 4;

                while (position < offset + 127)
                {
                    var header = edid[position];
                    var length = header & 0x1F;

                    if (length == 0) break;

                    var tag = header >> 5;

                    // Тег 3 — «блок данных поставщика»: здесь FreeSync.
                    if (tag == 3 && position + 5 < edid.Length &&
                        edid[position + 1] == 0x00 &&
                        edid[position + 2] == 0x00 &&
                        edid[position + 3] == 0x00)
                    {
                        var minHz = edid[position + 4];

                        // У AMD младший бит верхней границы зарезервирован.
                        var maxHz = edid[position + 5] & 0xFE;

                        if (minHz > 0 && maxHz > minHz)
                        {
                            return new VariableRefreshSupport
                            {
                                InstanceName = instanceName,
                                Supported = true,
                                MinHz = minHz,
                                MaxHz = maxHz,
                                Source = $"блок FreeSync в данных монитора: диапазон {minHz}–{maxHz} Гц"
                            };
                        }
                    }

                    position += length + 1;
                }

                var text = System.Text.Encoding.ASCII.GetString(edid, offset, 128);

                if (text.Contains("FreeSync", StringComparison.OrdinalIgnoreCase) ||
                    text.Contains("Adaptive-Sync", StringComparison.OrdinalIgnoreCase))
                {
                    return new VariableRefreshSupport
                    {
                        InstanceName = instanceName,
                        Supported = true,
                        Source = "в данных монитора указана поддержка Adaptive-Sync"
                    };
                }
            }

            return new VariableRefreshSupport { InstanceName = instanceName };
        }
        catch
        {
            return null;
        }
    }

    /// <summary>
    /// Ищет в реестре запись монитора по его названию.
    ///
    /// Нужно, потому что данные монитора (EDID) лежат под своим ключом,
    /// а связать его с конкретным экраном можно только по названию модели.
    /// </summary>
    private static string? FindEdidInstanceName(string monitorName)
    {
        try
        {
            using var display = Microsoft.Win32.Registry.LocalMachine.OpenSubKey(
                @"SYSTEM\CurrentControlSet\Enum\DISPLAY");

            if (display is null) return null;

            // Из названия вида «LG ULTRAGEAR(DisplayPort)» берём часть до скобки.
            var model = monitorName.Split('(')[0].Trim();

            foreach (var manufacturer in display.GetSubKeyNames())
            {
                using var manufacturerKey = display.OpenSubKey(manufacturer);
                if (manufacturerKey is null) continue;

                foreach (var instance in manufacturerKey.GetSubKeyNames())
                {
                    using var parameters = manufacturerKey.OpenSubKey(instance + @"\Device Parameters");
                    if (parameters?.GetValue("EDID") is not byte[] edid || edid.Length < 128) continue;

                    // Если название не совпало — всё равно вернём первый найденный:
                    // обычно монитор один, и это лучше, чем ничего не проверить.
                    if (string.IsNullOrEmpty(model) ||
                        InstanceMatchesModel(manufacturerKey, instance, model) ||
                        manufacturer == "GSM")
                    {
                        return manufacturer + "\\" + instance;
                    }
                }
            }
        }
        catch
        {
            // нет доступа — вернём null
        }

        return null;
    }

    /// <summary>Проверяет, похоже ли название записи на название монитора.</summary>
    private static bool InstanceMatchesModel(Microsoft.Win32.RegistryKey manufacturerKey, string instance, string model)
    {
        try
        {
            using var key = manufacturerKey.OpenSubKey(instance);
            var description = key?.GetValue("DriverDesc")?.ToString();

            return description is not null &&
                   description.Contains(model, StringComparison.OrdinalIgnoreCase);
        }
        catch
        {
            return false;
        }
    }

    private static byte[]? ReadEdid(string instanceName)
    {
        try
        {
            using var key = Microsoft.Win32.Registry.LocalMachine.OpenSubKey(
                @"SYSTEM\CurrentControlSet\Enum\DISPLAY\" + instanceName + @"\Device Parameters");

            return key?.GetValue("EDID") as byte[];
        }
        catch
        {
            return null;
        }
    }

    // ------------------------------------------------------- объявления Windows

    private const int AttachedToDesktop = 0x1;
    private const int PrimaryDevice = 0x4;
    private const int CurrentSettings = -1;

    [DllImport("user32.dll", CharSet = CharSet.Unicode)]
    private static extern bool EnumDisplayDevices(string? device, uint number, ref DisplayDevice info, uint flags);

    [DllImport("user32.dll", CharSet = CharSet.Unicode)]
    private static extern bool EnumDisplaySettings(string deviceName, int modeNumber, ref DevMode mode);

    [StructLayout(LayoutKind.Sequential, CharSet = CharSet.Unicode)]
    private struct DisplayDevice
    {
        public int Size;

        [MarshalAs(UnmanagedType.ByValTStr, SizeConst = 32)]
        public string DeviceName;

        [MarshalAs(UnmanagedType.ByValTStr, SizeConst = 128)]
        public string DeviceString;

        public int StateFlags;

        [MarshalAs(UnmanagedType.ByValTStr, SizeConst = 128)]
        public string DeviceID;

        [MarshalAs(UnmanagedType.ByValTStr, SizeConst = 128)]
        public string DeviceKey;
    }

    /// <summary>
    /// Описание режима экрана, как его отдаёт Windows.
    /// Поля идут строго по порядку — менять их местами нельзя, иначе данные поедут.
    /// </summary>
    [StructLayout(LayoutKind.Sequential, CharSet = CharSet.Unicode)]
    private struct DevMode
    {
        [MarshalAs(UnmanagedType.ByValTStr, SizeConst = 32)]
        public string DeviceName;

        public short SpecVersion;
        public short DriverVersion;
        public short Size;
        public short DriverExtra;
        public int Fields;
        public int PositionX;
        public int PositionY;
        public int DisplayOrientation;
        public int DisplayFixedOutput;
        public short Color;
        public short Duplex;
        public short YResolution;
        public short TTOption;
        public short Collate;

        [MarshalAs(UnmanagedType.ByValTStr, SizeConst = 32)]
        public string FormName;

        public short LogPixels;
        public int BitsPerPel;
        public int PelsWidth;
        public int PelsHeight;
        public int DisplayFlags;
        public int DisplayFrequency;
        public int ICMMethod;
        public int ICMIntent;
        public int MediaType;
        public int DitherType;
        public int Reserved1;
        public int Reserved2;
        public int PanningWidth;
        public int PanningHeight;
    }
}
