using Microsoft.Win32;

namespace Cs2LatencyDoctor.Core.Windows;

/// <summary>
/// Безопасный доступ к реестру. Только чтение и запись конкретных значений,
/// всегда с проверкой существования ключа. Никаких "умных" правок вслепую.
/// </summary>
public static class RegistryValueReader
{
    /// <summary>Прочитать DWORD. Возвращает null, если ключа или значения нет.</summary>
    public static int? ReadDword(RegistryHive hive, string subKey, string name)
    {
        using var baseKey = RegistryKey.OpenBaseKey(hive, RegistryView.Default);
        using var key = baseKey.OpenSubKey(subKey, writable: false);
        if (key is null) return null;
        var value = key.GetValue(name);
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
    /// Прочитать значение из ветки конкретного сетевого адаптера по ключевому слову драйвера
    /// (например *InterruptModeration). Ищем по DriverDesc, чтобы не зависеть от номера ключа.
    /// </summary>
    public static int? ReadAdapterKeyword(string adapterDescription, string keyword)
    {
        const string classPath =
            @"SYSTEM\CurrentControlSet\Control\Class\{4d36e972-e325-11ce-bfc1-08002be10318}";

        using var baseKey = RegistryKey.OpenBaseKey(RegistryHive.LocalMachine, RegistryView.Default);
        using var classKey = baseKey.OpenSubKey(classPath, writable: false);
        if (classKey is null) return null;

        foreach (var index in classKey.GetSubKeyNames())
        {
            if (index.Length != 4 || !int.TryParse(index, out _)) continue;

            using var instance = classKey.OpenSubKey(index, writable: false);
            if (instance is null) continue;

            var desc = instance.GetValue("DriverDesc") as string;
            if (desc is null || !string.Equals(desc, adapterDescription, StringComparison.OrdinalIgnoreCase))
                continue;

            var value = instance.GetValue(keyword);
            return value switch
            {
                int i => i,
                long l => (int)l,
                string s when int.TryParse(s, out var parsed) => parsed,
                _ => null
            };
        }

        return null;
    }
}
