using System.Management;

namespace Cs2LatencyDoctor.Core.Windows;

/// <summary>Физический диск: что это за накопитель и как он подключён.</summary>
public sealed class DiskInfo
{
    public required string Model { get; init; }
    public long SizeBytes { get; init; }

    /// <summary>Твёрдотельный или механический. null — определить не удалось.</summary>
    public bool? IsSolidState { get; init; }

    public string? InterfaceType { get; init; }

    public string SizeText => SizeBytes <= 0
        ? "размер неизвестен"
        : SizeBytes >= 1_000_000_000_000
            ? $"{SizeBytes / 1_000_000_000_000.0:0.#} ТБ"
            : $"{SizeBytes / 1_000_000_000.0:0} ГБ";

    public string KindText => IsSolidState switch
    {
        true => "твердотельный (SSD)",
        false => "механический (HDD)",
        _ => "тип неизвестен"
    };
}

/// <summary>Логический диск: сколько места и на каком физическом диске он лежит.</summary>
public sealed class VolumeInfo
{
    public required char Letter { get; init; }
    public long TotalBytes { get; init; }
    public long FreeBytes { get; init; }

    /// <summary>Физический диск, на котором лежит том. Пусто, если сопоставить не удалось.</summary>
    public string? PhysicalModel { get; init; }

    public double FreePercent => TotalBytes <= 0 ? 0 : FreeBytes * 100.0 / TotalBytes;

    public string FreeText =>
        $"{FreeBytes / 1_000_000_000.0:0} ГБ из {TotalBytes / 1_000_000_000.0:0} ГБ " +
        $"({FreePercent:0}% свободно)";
}

/// <summary>Планка оперативной памяти.</summary>
public sealed class MemoryModuleInfo
{
    public required string Slot { get; init; }
    public long CapacityBytes { get; init; }

    /// <summary>Заявленная производителем частота.</summary>
    public int RatedSpeedMhz { get; init; }

    /// <summary>Частота, на которой память работает сейчас.</summary>
    public int ActualSpeedMhz { get; init; }

    public string? Manufacturer { get; init; }

    public string CapacityText => $"{CapacityBytes / 1_073_741_824.0:0} ГБ";
}

/// <summary>Сведения о процессоре, важные для стабильности кадров.</summary>
public sealed class ProcessorInfo
{
    public required string Name { get; init; }
    public int MaxClockMhz { get; init; }
    public int CurrentClockMhz { get; init; }
    public int Cores { get; init; }
    public int LogicalProcessors { get; init; }

    /// <summary>Процессор сообщает о снижении частоты прямо сейчас.</summary>
    public bool Throttling { get; init; }

    /// <summary>Отношение текущей частоты к максимальной, в процентах.</summary>
    public int LoadPercent => MaxClockMhz <= 0 ? 0 : (int)Math.Round(CurrentClockMhz * 100.0 / MaxClockMhz);
}

/// <summary>
/// Чтение сведений об оборудовании.
///
/// Всё через WMI, потому что других способов узнать модель диска, режим памяти
/// и ограничения процессора без сторонних библиотек нет. Вызовы ограничены по
/// времени: служба WMI на некоторых машинах отвечает десятками секунд, и подвешивать
/// из-за неё всю проверку нельзя.
/// </summary>
public static class HardwareReader
{
    /// <summary>Сколько ждать ответа WMI на один запрос.</summary>
    public static int TimeoutSeconds { get; set; } = 10;

    /// <summary>Физические диски: модель, размер, тип.</summary>
    public static IReadOnlyList<DiskInfo> ReadDisks()
    {
        return Query("SELECT Model, Size, InterfaceType, MediaType FROM Win32_DiskDrive",
            obj => new DiskInfo
            {
                Model = (obj["Model"] as string)?.Trim() ?? "неизвестный диск",
                SizeBytes = ToLong(obj["Size"]),
                InterfaceType = (obj["InterfaceType"] as string)?.Trim(),
                IsSolidState = GuessSolidState(obj)
            });
    }

    /// <summary>Логические диски с сопоставлением физическому диску.</summary>
    public static IReadOnlyList<VolumeInfo> ReadVolumes()
    {
        var volumes = new List<VolumeInfo>();
        var diskByLetter = MapVolumesToDisks();

        foreach (var obj in QueryObjects("SELECT DeviceID, Size, FreeSpace FROM Win32_LogicalDisk WHERE DriveType = 3"))
        {
            using (obj)
            {
                var id = obj["DeviceID"] as string;
                if (string.IsNullOrEmpty(id)) continue;

                var letter = id[0];

                volumes.Add(new VolumeInfo
                {
                    Letter = letter,
                    TotalBytes = ToLong(obj["Size"]),
                    FreeBytes = ToLong(obj["FreeSpace"]),
                    PhysicalModel = diskByLetter.TryGetValue(letter, out var model) ? model : null
                });
            }
        }

        return volumes;
    }

    /// <summary>Планки памяти: слот, объём, частоты.</summary>
    public static IReadOnlyList<MemoryModuleInfo> ReadMemoryModules()
    {
        return Query(
            "SELECT DeviceLocator, Capacity, Speed, ConfiguredClockSpeed, Manufacturer FROM Win32_PhysicalMemory",
            obj => new MemoryModuleInfo
            {
                Slot = (obj["DeviceLocator"] as string)?.Trim() ?? "?",
                CapacityBytes = ToLong(obj["Capacity"]),
                RatedSpeedMhz = ToInt(obj["Speed"]),
                ActualSpeedMhz = ToInt(obj["ConfiguredClockSpeed"]),
                Manufacturer = (obj["Manufacturer"] as string)?.Trim()
            });
    }

    /// <summary>Сколько всего слотов памяти на плате.</summary>
    public static int ReadMemorySlotCount()
    {
        var values = Query("SELECT MemoryDevices FROM Win32_PhysicalMemoryArray", obj => ToInt(obj["MemoryDevices"]));
        return values.Count > 0 ? values[0] : 0;
    }

    /// <summary>
    /// Текущая загрузка процессора в процентах. null — прочитать не удалось.
    ///
    /// Через WMI, а не через PerformanceCounter: тот требует отдельной сборки,
    /// а значение даёт то же. Берётся строка «_Total», то есть по всем ядрам сразу.
    /// </summary>
    public static int? ReadCpuLoadPercent()
    {
        var values = Query(
            "SELECT PercentProcessorTime FROM Win32_PerfFormattedData_PerfOS_Processor",
            obj => (obj["PercentProcessorTime"] as string) ?? string.Empty);

        // Первая запись этого класса — «_Total».
        return values.Count == 0 || !int.TryParse(values[0], out var percent) ? null : percent;
    }

    /// <summary>Сведения о процессоре.</summary>
    public static ProcessorInfo? ReadProcessor()
    {
        var list = Query(
            "SELECT Name, MaxClockSpeed, CurrentClockSpeed, NumberOfCores, NumberOfLogicalProcessors, " +
            "CurrentVoltage FROM Win32_Processor",
            obj => new ProcessorInfo
            {
                Name = (obj["Name"] as string)?.Trim() ?? "неизвестный процессор",
                MaxClockMhz = ToInt(obj["MaxClockSpeed"]),
                CurrentClockMhz = ToInt(obj["CurrentClockSpeed"]),
                Cores = ToInt(obj["NumberOfCores"]),
                LogicalProcessors = ToInt(obj["NumberOfLogicalProcessors"])
            });

        return list.Count > 0 ? list[0] : null;
    }

    /// <summary>
    /// Текущий таймаут остановки диска в секундах. 0 — остановка запрещена.
    /// null — параметр не задан (действует значение по умолчанию).
    /// </summary>
    public static int? ReadDiskTimeoutSeconds()
    {
        try
        {
            using var key = Microsoft.Win32.Registry.LocalMachine.OpenSubKey(
                @"SYSTEM\CurrentControlSet\Services\disk\TimeoutValue");

            var raw = key?.GetValue("TimeoutValue");
            if (raw is null) return null;

            return Convert.ToInt32(raw);
        }
        catch
        {
            return null;
        }
    }

    // ------------------------------------------------------------------ вспомогательное

    /// <summary>Есть ли у диска признаки твердотельного.</summary>
    private static bool? GuessSolidState(ManagementBaseObject obj)
    {
        try
        {
            var media = (obj["MediaType"] as string)?.Trim() ?? string.Empty;
            var model = (obj["Model"] as string)?.Trim() ?? string.Empty;

            if (media.Contains("SSD", StringComparison.OrdinalIgnoreCase)) return true;
            if (media.Contains("Fixed hard disk", StringComparison.OrdinalIgnoreCase))
            {
                // «Fixed hard disk media» бывает и у SSD: тип не различает.
                // Дальше смотрим модель: у NVMe и типовых SSD есть узнаваемые слова.
                if (model.Contains("NVMe", StringComparison.OrdinalIgnoreCase) ||
                    model.Contains("SSD", StringComparison.OrdinalIgnoreCase) ||
                    model.Contains("XPG", StringComparison.OrdinalIgnoreCase) ||
                    model.Contains("Samsung", StringComparison.OrdinalIgnoreCase) && model.Contains("EVO", StringComparison.OrdinalIgnoreCase))
                    return true;

                // У механических дисков модели обычно начинаются с букв производителя
                // и содержат объём: ST1000DM003, WD10EZEX, TOSHIBA DT01ACA.
                if (System.Text.RegularExpressions.Regex.IsMatch(model, @"^(ST|WD|TOSHIBA|HGST|Hitachi)",
                        System.Text.RegularExpressions.RegexOptions.IgnoreCase))
                    return false;

                return null;
            }

            return null;
        }
        catch
        {
            return null;
        }
    }

    /// <summary>Сопоставить буквы логических дисков физическим дискам.</summary>
    private static Dictionary<char, string> MapVolumesToDisks()
    {
        var result = new Dictionary<char, string>();

        try
        {
            // Цепочка: LogicalDisk → Partition → DiskDrive. WMI-связи работают
            // не на всех системах, поэтому ошибки глушим и возвращаем что есть.
            var partitionToDisk = new Dictionary<string, string>();

            foreach (var disk in QueryObjects("SELECT DeviceID, Model FROM Win32_DiskDrive"))
            {
                using (disk)
                {
                    var diskId = disk["DeviceID"] as string;
                    var model = (disk["Model"] as string)?.Trim();
                    if (diskId is null || model is null) continue;

                    foreach (var partition in QueryObjects(
                                 $"ASSOCIATORS OF {{Win32_DiskDrive.DeviceID='{diskId}'}} WHERE AssocClass=Win32_DiskDriveToDiskPartition"))
                    {
                        using (partition)
                        {
                            var partitionId = partition["DeviceID"] as string;
                            if (partitionId is not null) partitionToDisk[partitionId] = model;
                        }
                    }
                }
            }

            foreach (var (partitionId, model) in partitionToDisk)
            {
                var logicals = QueryObjects(
                    $"ASSOCIATORS OF {{Win32_DiskPartition.DeviceID='{partitionId}'}} WHERE AssocClass=Win32_LogicalDiskToPartition");

                foreach (var logical in logicals)
                {
                    using (logical)
                    {
                        var id = logical["DeviceID"] as string;
                        if (!string.IsNullOrEmpty(id)) result[id[0]] = model;
                    }
                }
            }
        }
        catch
        {
            // Сопоставление не удалось: проверка скажет об этом честно.
        }

        return result;
    }

    private static List<T> Query<T>(string wql, Func<ManagementBaseObject, T> map)
    {
        var result = new List<T>();

        try
        {
            var task = Task.Run(() =>
            {
                var list = new List<T>();

                foreach (var obj in QueryObjects(wql))
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
            // WMI недоступна: возвращаем пустой список, проверка объяснит причину.
        }

        return result;
    }

    private static List<ManagementBaseObject> QueryObjects(string wql)
    {
        var list = new List<ManagementBaseObject>();

        try
        {
            using var searcher = new ManagementObjectSearcher(wql);
            foreach (var obj in searcher.Get()) list.Add(obj);
        }
        catch
        {
            // Пустой результат: вызывающий код решает, что с этим делать.
        }

        return list;
    }

    private static long ToLong(object? value) => value is null ? 0 : Convert.ToInt64(value);
    private static int ToInt(object? value) => value is null ? 0 : Convert.ToInt32(value);
}
