using Microsoft.Win32;

namespace Cs2LatencyDoctor.Core.Windows;

/// <summary>Найденный экземпляр сетевого адаптера в реестре драйвера.</summary>
public sealed record AdapterRegistryInstance(string KeyPath, string Index, string Description);

/// <summary>
/// Безопасный доступ к реестру. Только чтение и запись конкретных значений,
/// всегда с проверкой существования ключа. Никаких «умных» правок вслепую.
/// </summary>
public static class RegistryValueReader
{
    private const string NetworkClassPath =
        @"SYSTEM\CurrentControlSet\Control\Class\{4d36e972-e325-11ce-bfc1-08002be10318}";

    // ------------------------------------------------------------ обычные ветки
    /// <summary>Прочитать DWORD. null, если ключа или значения нет.</summary>
    public static int? ReadDword(RegistryHive hive, string subKey, string name)
    {
        using var baseKey = RegistryKey.OpenBaseKey(hive, RegistryView.Default);
        using var key = baseKey.OpenSubKey(subKey, writable: false);
        var value = key?.GetValue(name);
        return value switch
        {
            int i => i,
            long l => (int)l,
            _ => null
        };
    }

    /// <summary>Прочитать строку.</summary>
    public static string? ReadString(RegistryHive hive, string subKey, string name)
    {
        using var baseKey = RegistryKey.OpenBaseKey(hive, RegistryView.Default);
        using var key = baseKey.OpenSubKey(subKey, writable: false);
        return key?.GetValue(name) as string;
    }

    /// <summary>Существует ли ключ.</summary>
    public static bool KeyExists(RegistryHive hive, string subKey)
    {
        using var baseKey = RegistryKey.OpenBaseKey(hive, RegistryView.Default);
        using var key = baseKey.OpenSubKey(subKey, writable: false);
        return key is not null;
    }

    /// <summary>
    /// Прочитать значение в виде строки независимо от типа. Нужно для журнала отката:
    /// важно вернуть ровно то, что было.
    /// </summary>
    public static string? ReadRawString(RegistryHive hive, string subKey, string name)
    {
        using var baseKey = RegistryKey.OpenBaseKey(hive, RegistryView.Default);
        using var key = baseKey.OpenSubKey(subKey, writable: false);
        var value = key?.GetValue(name);
        return value?.ToString();
    }

    /// <summary>
    /// Записать значение, сохранив прежний тип (строка/DWORD).
    ///
    /// Если ключа нет — создаём его. Это важно для отката: настройка могла
    /// быть удалена другим приложением, и тогда молчаливый отказ означал бы,
    /// что вернуть её уже нельзя.
    /// </summary>
    public static bool WriteValue(RegistryHive hive, string subKey, string name, string value)
    {
        try
        {
            using var baseKey = RegistryKey.OpenBaseKey(hive, RegistryView.Default);

            var key = baseKey.OpenSubKey(subKey, writable: true);

            if (key is null)
            {
                key = baseKey.CreateSubKey(subKey, writable: true);
                if (key is null) return false;
            }

            using (key)
            {
                // Числовые значения в этих ветках обычно хранятся строками. Если исходный
                // тип был DWORD — пишем DWORD, иначе строка.
                var existing = key.GetValue(name);
                if (existing is int)
                {
                    if (!int.TryParse(value, out var number)) return false;
                    key.SetValue(name, number, RegistryValueKind.DWord);
                }
                else
                {
                    key.SetValue(name, value, RegistryValueKind.String);
                }

                return true;
            }
        }
        catch
        {
            return false;
        }
    }

    /// <summary>Удалить значение. Нужно для отката: если значения не было изначально, его надо убрать, а не затирать.</summary>
    public static bool DeleteValue(RegistryHive hive, string subKey, string name)
    {
        try
        {
            using var baseKey = RegistryKey.OpenBaseKey(hive, RegistryView.Default);
            using var key = baseKey.OpenSubKey(subKey, writable: true);
            if (key is null) return false;

            key.DeleteValue(name, throwOnMissingValue: false);
            return true;
        }
        catch
        {
            return false;
        }
    }

    // ------------------------------------------------ сетевые адаптеры (keywords)    /// <summary>
    /// Найти все экземпляры сетевых адаптеров в ветке класса. Нужно для чтения
    /// и правки ключевых слов драйвера (*InterruptModeration и подобных).
    /// </summary>
    public static IReadOnlyList<AdapterRegistryInstance> EnumerateAdapters()
    {
        var result = new List<AdapterRegistryInstance>();

        using var baseKey = RegistryKey.OpenBaseKey(RegistryHive.LocalMachine, RegistryView.Default);
        using var classKey = baseKey.OpenSubKey(NetworkClassPath, writable: false);
        if (classKey is null) return result;

        foreach (var index in classKey.GetSubKeyNames())
        {
            if (index.Length != 4 || !int.TryParse(index, out _)) continue;

            using var instance = classKey.OpenSubKey(index, writable: false);
            var description = instance?.GetValue("DriverDesc") as string;
            if (string.IsNullOrWhiteSpace(description)) continue;

            result.Add(new AdapterRegistryInstance(
                $@"{NetworkClassPath}\{index}", index, description));
        }

        return result;
    }

    /// <summary>Прочитать ключевое слово драйвера у адаптера с указанным описанием.</summary>
    public static int? ReadAdapterKeyword(string adapterDescription, string keyword)
    {
        var value = ReadAdapterKeywordRaw(adapterDescription, keyword);
        return int.TryParse(value, out var parsed) ? parsed : null;
    }

    /// <summary>Прочитать ключевое слово драйвера как строку — для журнала отката.</summary>
    public static string? ReadAdapterKeywordRaw(string adapterDescription, string keyword)
    {
        foreach (var adapter in EnumerateAdapters())
        {
            if (!string.Equals(adapter.Description, adapterDescription, StringComparison.OrdinalIgnoreCase))
                continue;

            using var baseKey = RegistryKey.OpenBaseKey(RegistryHive.LocalMachine, RegistryView.Default);
            using var key = baseKey.OpenSubKey(adapter.KeyPath, writable: false);
            return key?.GetValue(keyword)?.ToString();
        }

        return null;
    }

    /// <summary>Записать ключевое слово драйвера у адаптера. Возвращает путь ключа для журнала.</summary>
    public static bool WriteAdapterKeyword(string adapterDescription, string keyword, string value,
        out string keyPath)
    {
        keyPath = string.Empty;

        foreach (var adapter in EnumerateAdapters())
        {
            if (!string.Equals(adapter.Description, adapterDescription, StringComparison.OrdinalIgnoreCase))
                continue;

            keyPath = adapter.KeyPath;
            return WriteValue(RegistryHive.LocalMachine, adapter.KeyPath, keyword, value);
        }

        return false;
    }
}
