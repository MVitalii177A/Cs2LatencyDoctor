using System.Management;

namespace Cs2LatencyDoctor.Core.Windows;

/// <summary>
/// Сведения о системе, на которой выполнялась проверка.
///
/// Зачем это в отчёте. Когда человек присылает отчёт, первым делом нужно понять,
/// на чём он запускался: от материнской платы зависит, какие бывают сетевые карты
/// и какие у них настройки, от версии Windows — какие проверки применимы.
/// Без этих сведений разбор превращается в переписку с вопросами «а какой у вас
/// процессор», и человек теряет интерес отвечать.
///
/// <b>Имени компьютера здесь намеренно нет.</b> Для поиска ошибок оно не нужно,
/// а отчёт человек может выложить публично, не подумав. Всё, что собрано ниже,
/// описывает конфигурацию, а не владельца.
/// </summary>
public sealed class SystemFingerprint
{
    public string? Motherboard { get; init; }
    public string? BiosVersion { get; init; }
    public string? Cpu { get; init; }
    public string? Gpu { get; init; }
    public string? GpuDriver { get; init; }
    public string? WindowsEdition { get; init; }
    public string? WindowsVersion { get; init; }
    public string? WindowsBuild { get; init; }
    public string? Architecture { get; init; }
    public string? MemoryTotal { get; init; }
    public string? MemorySpeed { get; init; }

    /// <summary>Дата сборки программы: по ней видно, на какой версии делался замер.</summary>
    public string ToolVersion => AppVersion.Short;

    /// <summary>Короткая строка для заголовка отчёта: система и версия Windows.</summary>
    public string ShortLine
    {
        get
        {
            var parts = new List<string>();

            if (!string.IsNullOrWhiteSpace(WindowsEdition)) parts.Add(WindowsEdition);
            if (!string.IsNullOrWhiteSpace(WindowsVersion)) parts.Add($"версия {WindowsVersion}");
            if (!string.IsNullOrWhiteSpace(WindowsBuild)) parts.Add($"сборка {WindowsBuild}");

            return string.Join(", ", parts);
        }
    }

    /// <summary>Понятное ли это описание системы: есть ли хоть что-то, кроме версии Windows.</summary>
    public bool HasHardware =>
        !string.IsNullOrWhiteSpace(Motherboard) ||
        !string.IsNullOrWhiteSpace(Cpu) ||
        !string.IsNullOrWhiteSpace(Gpu);

    /// <summary>
    /// Строки для отчёта: подпись и значение. Пустые поля пропускаем,
    /// чтобы в отчёт не попадали строки вида «Материнская плата: неизвестно».
    /// </summary>
    public IReadOnlyList<(string Label, string Value)> ToLines()
    {
        var lines = new List<(string, string)>();

        void Add(string label, string? value)
        {
            if (!string.IsNullOrWhiteSpace(value)) lines.Add((label, value.Trim()));
        }

        Add("Windows", ShortLine);
        Add("Архитектура", Architecture);
        Add("Материнская плата", Motherboard);
        Add("BIOS", BiosVersion);
        Add("Процессор", Cpu);
        Add("Видеокарта", Gpu);
        Add("Драйвер видеокарты", GpuDriver);
        Add("Память", MemoryTotal);
        Add("Частота памяти", MemorySpeed);

        return lines;
    }
}

/// <summary>
/// Чтение сведений о системе для отчёта.
///
/// Всё через WMI: других способов узнать модель платы и версию BIOS без сторонних
/// библиотек нет. Прав администратора не требует — эти сведения доступны любому
/// пользователю. Ограничение по времени обязательно: служба WMI на части машин
/// отвечает десятками секунд, и ждать её весь отчёт нельзя.
/// </summary>
public static class SystemFingerprintReader
{
    /// <summary>Сколько ждать ответа WMI на один запрос.</summary>
    public static int TimeoutSeconds { get; set; } = 10;

    public static SystemFingerprint Read()
    {
        var board = ReadBoard();
        var cpu = ReadCpu();
        var (gpu, driver) = ReadGpu();
        var windows = ReadWindows();
        var (total, speed) = ReadMemory();

        return new SystemFingerprint
        {
            Motherboard = board,
            BiosVersion = ReadSingle("SELECT SMBIOSBIOSVersion FROM Win32_BIOS", "SMBIOSBIOSVersion"),
            Cpu = cpu,
            Gpu = gpu,
            GpuDriver = driver,
            WindowsEdition = windows.Edition,
            WindowsVersion = windows.Version,
            WindowsBuild = windows.Build,
            Architecture = windows.Architecture,
            MemoryTotal = total,
            MemorySpeed = speed
        };
    }

    /// <summary>Материнская плата: производитель и модель.</summary>
    private static string? ReadBoard()
    {
        var values = Query("SELECT Manufacturer, Product FROM Win32_BaseBoard",
            obj => (Manufacturer: Text(obj, "Manufacturer"), Product: Text(obj, "Product")));

        if (values.Count == 0) return null;

        var (manufacturer, product) = values[0];
        if (string.IsNullOrWhiteSpace(product)) return null;

        // «Micro-Star International Co., Ltd.» в отчёте только мешает: оставляем
        // узнаваемое короткое имя производителя, а не юридическое название.
        var shortName = manufacturer switch
        {
            null => null,
            var m when m.Contains("Micro-Star", StringComparison.OrdinalIgnoreCase) => "MSI",
            var m when m.Contains("ASUSTeK", StringComparison.OrdinalIgnoreCase) => "ASUS",
            var m when m.Contains("Gigabyte", StringComparison.OrdinalIgnoreCase) => "Gigabyte",
            var m when m.Contains("ASRock", StringComparison.OrdinalIgnoreCase) => "ASRock",
            var m when m.Contains("Biostar", StringComparison.OrdinalIgnoreCase) => "Biostar",
            var m => m.Trim()
        };

        return string.IsNullOrWhiteSpace(shortName) ? product.Trim() : $"{shortName} {product.Trim()}";
    }

    private static string? ReadCpu()
    {
        // Маркетинговые приставки в названии процессора не нужны и только удлиняют строку.
        var name = ReadSingle("SELECT Name FROM Win32_Processor", "Name");
        if (name is null) return null;

        return name
            .Replace("(R)", string.Empty)
            .Replace("(TM)", string.Empty)
            .Replace("CPU @", "@")
            .Replace("  ", " ")
            .Trim();
    }

    /// <summary>
    /// Видеокарта. Берём не первую попавшуюся, а самую мощную по объёму памяти:
    /// в системе с процессором Intel первой в списке идёт встроенная графика,
    /// а играет человек на дискретной.
    /// </summary>
    private static (string? Name, string? Driver) ReadGpu()
    {
        var adapters = Query(
            "SELECT Name, DriverVersion, AdapterRAM FROM Win32_VideoController",
            obj => (
                Name: Text(obj, "Name"),
                Driver: Text(obj, "DriverVersion"),
                Memory: Number(obj, "AdapterRAM")));

        if (adapters.Count == 0) return (null, null);

        var best = adapters
            .Where(a => !string.IsNullOrWhiteSpace(a.Name))
            .OrderByDescending(a => a.Memory)
            .FirstOrDefault();

        if (best.Name is null) return (null, null);

        var name = best.Name
            .Replace("(R)", string.Empty)
            .Replace("(TM)", string.Empty)
            .Replace("  ", " ")
            .Trim();

        // Драйвер приводим к читаемому виду: 32.0.15.9186 → 32.0.15.9186 понятнее,
        // чем сырое значение, но у части драйверов оно совпадает с отображаемым.
        var driver = best.Driver?.Trim();

        return (name, string.IsNullOrWhiteSpace(driver) ? null : driver);
    }

    /// <summary>
    /// Версия Windows. Из WMI берём редакцию и разрядность, а точный номер сборки —
    /// из реестра: в WMI нет номера обновления (UBR), а именно он отличает
    /// одну сборку Windows 10 от другой.
    /// </summary>
    private static (string? Edition, string? Version, string? Build, string? Architecture) ReadWindows()
    {
        string? edition = null;
        string? architecture = null;

        var os = Query("SELECT Caption, Version, OSArchitecture FROM Win32_OperatingSystem",
            obj => (Caption: Text(obj, "Caption"), Version: Text(obj, "Version"), Arch: Text(obj, "OSArchitecture")));

        if (os.Count > 0)
        {
            edition = os[0].Caption?
                .Replace("Майкрософт ", string.Empty)
                .Replace("Microsoft ", string.Empty)
                .Trim();
            architecture = os[0].Arch;
        }

        string? version = null;
        string? build = null;

        try
        {
            using var key = Microsoft.Win32.Registry.LocalMachine.OpenSubKey(
                @"SOFTWARE\Microsoft\Windows NT\CurrentVersion");

            if (key is not null)
            {
                // DisplayVersion — это «22H2» или «23H2»: понятнее, чем номер.
                version = key.GetValue("DisplayVersion") as string
                          ?? key.GetValue("ReleaseId") as string;

                var current = key.GetValue("CurrentBuild") as string;
                var ubr = key.GetValue("UBR");

                if (current is not null)
                    build = ubr is null ? current : $"{current}.{ubr}";

                // ProductName в реестре врёт на Windows 11: там остаётся «Windows 10».
                // Поэтому сверяем с номером сборки, а не верим названию.
                if (current is not null && int.TryParse(current, out var buildNumber) && buildNumber >= 22000)
                    edition = edition?.Replace("Windows 10", "Windows 11");
            }
        }
        catch
        {
            // Реестр недоступен: останутся данные из WMI.
        }

        if (string.IsNullOrWhiteSpace(version) && os.Count > 0) version = os[0].Version;

        return (edition, version, build, architecture);
    }

    /// <summary>Объём и частота памяти: одна строка вместо перечисления всех планок.</summary>
    private static (string? Total, string? Speed) ReadMemory()
    {
        var modules = Query(
            "SELECT Capacity, ConfiguredClockSpeed, Speed FROM Win32_PhysicalMemory",
            obj => (
                Capacity: Number(obj, "Capacity"),
                Actual: Number(obj, "ConfiguredClockSpeed"),
                Rated: Number(obj, "Speed")));

        if (modules.Count == 0) return (null, null);

        var totalBytes = modules.Sum(m => m.Capacity);
        var total = totalBytes > 0
            ? $"{totalBytes / 1_073_741_824.0:0} ГБ в {modules.Count} " +
              (modules.Count == 1 ? "планке" : "планках")
            : null;

        // Если память работает медленнее заявленной, показываем оба числа:
        // это признак выключенного профиля XMP, и по отчёту это должно быть видно.
        var actual = modules.Max(m => m.Actual);
        var rated = modules.Max(m => m.Rated);

        string? speed = actual > 0 && rated > 0 && actual < rated - 200
            ? $"{actual} МГц (заявлено {rated} — профиль разгона выключен)"
            : actual > 0
                ? $"{actual} МГц"
                : rated > 0 ? $"{rated} МГц" : null;

        return (total, speed);
    }

    // ------------------------------------------------------------------ вспомогательное

    private static string? ReadSingle(string wql, string property)
    {
        var values = Query(wql, obj => Text(obj, property));
        return values.Count > 0 ? values[0] : null;
    }

    private static string? Text(ManagementBaseObject obj, string property)
    {
        try
        {
            var value = obj[property] as string;
            return string.IsNullOrWhiteSpace(value) ? null : value.Trim();
        }
        catch
        {
            return null;
        }
    }

    private static long Number(ManagementBaseObject obj, string property)
    {
        try
        {
            var value = obj[property];
            return value is null ? 0 : Convert.ToInt64(value);
        }
        catch
        {
            return 0;
        }
    }

    private static List<T> Query<T>(string wql, Func<ManagementBaseObject, T> map)
    {
        var result = new List<T>();

        try
        {
            var task = Task.Run(() =>
            {
                var list = new List<T>();

                using var searcher = new ManagementObjectSearcher(wql);
                searcher.Options.Timeout = TimeSpan.FromSeconds(TimeoutSeconds);

                foreach (var obj in searcher.Get())
                {
                    using (obj)
                    {
                        try { list.Add(map(obj)); }
                        catch { /* пропускаем непонятную запись */ }
                    }
                }

                return list;
            });

            if (task.Wait(TimeSpan.FromSeconds(TimeoutSeconds))) result = task.Result;
        }
        catch
        {
            // WMI недоступна: отчёт будет без этих сведений, и это лучше, чем падение.
        }

        return result;
    }
}
