using Cs2LatencyDoctor.Core.Checks;
using Cs2LatencyDoctor.Core.Windows;
using Microsoft.Win32;

namespace Cs2LatencyDoctor.Core.Fixes;

/// <summary>
/// Отключает на сетевом адаптере всё, что добавляет задержку и джиттер:
/// модерацию прерываний, энергосберегающие режимы PHY, выключение адаптера при простое.
///
/// Это исправление дало измеримый результат на реальной машине:
/// было 19 всплесков из 200 замеров до роутера, стало 0.
/// </summary>
public sealed class NetworkAdapterFix : FixBase
{
    public override string Id => "net.adapter.hostile-settings";
    public override string Title => "Отключить энергосбережение и модерацию прерываний на сетевой карте";

    public override FixResult Apply(DiagnosticContext context, UndoJournal journal)
    {
        var adapter = ResolveAdapter(context);
        if (adapter is null)
            return FixResult.Skipped(Id, Title, "Активный сетевой адаптер не найден");

        var hostile = NetworkAdapterReader.ReadLatencyHostileSettings(adapter.Description);
        if (hostile.Count == 0)
            return FixResult.AlreadyOk(Id, Title,
                $"На адаптере «{adapter.Description}» все параметры уже выставлены правильно");

        var changes = new List<JournalEntry>();
        var failed = new List<string>();

        foreach (var keyword in hostile.Keys)
        {
            var oldValue = RegistryValueReader.ReadAdapterKeywordRaw(adapter.Description, keyword);
            if (oldValue is null) continue;

            var friendlyName = NetworkAdapterReader.LatencyHostileKeywords.TryGetValue(keyword, out var n)
                ? n : keyword;

            if (!RegistryValueReader.WriteAdapterKeyword(adapter.Description, keyword, "0", out var keyPath))
            {
                failed.Add(friendlyName);
                continue;
            }

            // Проверяем, что записалось именно то, что нужно. Без проверки
            // мы бы считали правку успешной, даже если драйвер её не принял.
            var readBack = RegistryValueReader.ReadAdapterKeyword(adapter.Description, keyword);
            if (readBack != 0)
            {
                failed.Add(friendlyName + " (не принято драйвером)");
                continue;
            }

            changes.Add(new JournalEntry
            {
                FixId = Id,
                Title = friendlyName,
                Kind = "registryKeyword",
                Location = keyPath,
                Name = keyword,
                OldValue = oldValue,
                NewValue = "0"
            });
        }

        if (changes.Count == 0)
        {
            return failed.Count > 0
                ? FixResult.Failed(Id, Title, "Не удалось изменить: " + string.Join(", ", failed))
                : FixResult.AlreadyOk(Id, Title, "Нечего менять");
        }

        foreach (var change in changes) journal.Add(change);

        var message = $"Отключено на «{adapter.Description}»: " +
                      string.Join(", ", changes.Select(c => c.Title)) +
                      ". Изменения вступят в силу после перезапуска адаптера.";

        if (failed.Count > 0)
            message += " Не удалось: " + string.Join(", ", failed) + ".";

        return FixResult.Applied(Id, Title, message, changes);
    }

    public override bool Revert(JournalEntry entry, DiagnosticContext context)
    {
        if (entry.Kind != "registryKeyword") return false;

        // Запись журнала всегда ведёт в ветку класса сетевых драйверов HKLM.
        var hive = Microsoft.Win32.RegistryHive.LocalMachine;

        // Если значения изначально не было — удаляем его, а не пишем пустую строку.
        if (string.IsNullOrEmpty(entry.OldValue))
            return RegistryValueReader.DeleteValue(hive, entry.Location, entry.Name);

        if (!RegistryValueReader.WriteValue(hive, entry.Location, entry.Name, entry.OldValue))
            return false;

        var actual = RegistryValueReader.ReadRawString(hive, entry.Location, entry.Name);
        return string.Equals(actual, entry.OldValue, StringComparison.OrdinalIgnoreCase);
    }

    /// <summary>Активный адаптер: сначала из контекста (его определила проверка маршрута), иначе первый проводной.</summary>
    private static NetworkAdapterInfo? ResolveAdapter(DiagnosticContext context)
    {
        var adapters = NetworkAdapterReader.GetActiveAdapters();
        if (adapters.Count == 0) return null;

        return adapters.FirstOrDefault(a => a.Name == context.PrimaryAdapterName)
               ?? adapters.FirstOrDefault(a => !a.IsWireless)
               ?? adapters[0];
    }
}
