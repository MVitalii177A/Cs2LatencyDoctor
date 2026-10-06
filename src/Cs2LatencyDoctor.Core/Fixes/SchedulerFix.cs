using Cs2LatencyDoctor.Core.Windows;
using Microsoft.Win32;

namespace Cs2LatencyDoctor.Core.Fixes;

/// <summary>
/// Настройки планировщика Multimedia Class Scheduler Service: приоритет игровых
/// потоков и снятие сетевого троттлинга для мультимедиа.
///
/// Честно про эффект: это не главный источник лага. Влияние умеренное —
/// убирает лишнюю конкуренцию за процессор, но чуда не делает.
/// </summary>
public sealed class SchedulerFix : FixBase
{
    private const string GamesTaskPath =
        @"SOFTWARE\Microsoft\Windows NT\CurrentVersion\Multimedia\SystemProfile\Tasks\Games";
    private const string SystemProfilePath =
        @"SOFTWARE\Microsoft\Windows NT\CurrentVersion\Multimedia\SystemProfile";

    public override string Id => "scheduler.mmcss";
    public override string Title => "Поднять приоритет игр в планировщике Windows";

    /// <summary>Целевые значения. Категории строковые, приоритеты числовые.</summary>
    private static readonly (string Name, string Value, bool IsString)[] Targets =
    {
        ("GPU Priority", "8", false),
        ("Priority", "6", false),
        ("Scheduling Category", "High", true),
        ("SFIO Priority", "High", true),
        ("Clock Rate", "10000", false)
    };

    public override FixResult Apply(DiagnosticContext context, UndoJournal journal, FixPlan plan)
    {
        if (!RegistryValueReader.KeyExists(RegistryHive.LocalMachine, GamesTaskPath))
            return FixResult.Skipped(Id, Title, "Ветка планировщика игр не найдена в системе");

        var changes = new List<JournalEntry>();
        var alreadyOk = new List<string>();

        foreach (var (name, target, _) in Targets)
        {
            // Меняем только те значения, на которые указала проверка: одно исправление
            // закрывает пять параметров, а находка может касаться одного.
            if (!plan.WantsSubAction(Id, Id + "." + name)) continue;

            var oldValue = RegistryValueReader.ReadRawString(RegistryHive.LocalMachine, GamesTaskPath, name);

            if (string.Equals(oldValue, target, StringComparison.OrdinalIgnoreCase))
            {
                alreadyOk.Add(name);
                continue;
            }

            if (!RegistryValueReader.WriteValue(RegistryHive.LocalMachine, GamesTaskPath, name, target))
                return FixResult.Failed(Id, Title, $"Не удалось записать «{name}»");

            var readBack = RegistryValueReader.ReadRawString(RegistryHive.LocalMachine, GamesTaskPath, name);
            if (!string.Equals(readBack, target, StringComparison.OrdinalIgnoreCase))
                return FixResult.Failed(Id, Title, $"Значение «{name}» не принялось");

            changes.Add(new JournalEntry
            {
                FixId = Id,
                Title = name,
                Kind = "registryValue",
                Location = GamesTaskPath,
                Name = name,
                OldValue = oldValue ?? string.Empty,
                NewValue = target
            });
        }

        // Сетевой троттлинг: 10 пакетов/мс по умолчанию, отключаем полностью.
        const string throttleName = "NetworkThrottlingIndex";
        var throttleOld = RegistryValueReader.ReadDword(RegistryHive.LocalMachine, SystemProfilePath, throttleName);
        const string throttleTarget = "4294967295";

        var throttleWanted = plan.WantsSubAction(Id, Id + "." + throttleName);

        if (throttleWanted && throttleOld is not null && throttleOld.Value != unchecked((int)0xFFFFFFFF))
        {
            if (RegistryValueReader.WriteValue(RegistryHive.LocalMachine, SystemProfilePath,
                    throttleName, throttleTarget))
            {
                changes.Add(new JournalEntry
                {
                    FixId = Id,
                    Title = "NetworkThrottlingIndex",
                    Kind = "registryValue",
                    Location = SystemProfilePath,
                    Name = throttleName,
                    OldValue = throttleOld.Value.ToString(),
                    NewValue = throttleTarget
                });
            }
        }
        else if (throttleOld is not null)
        {
            alreadyOk.Add(throttleName);
        }

        if (changes.Count == 0)
            return FixResult.AlreadyOk(Id, Title, "Планировщик уже настроен: " + string.Join(", ", alreadyOk));

        foreach (var change in changes) journal.Add(change);

        return FixResult.Applied(Id, Title,
            $"Изменено: {string.Join(", ", changes.Select(c => c.Title))}",
            changes);
    }

    public override bool Revert(JournalEntry entry, DiagnosticContext context)
    {
        if (entry.Kind != "registryValue") return false;

        // Пустое старое значение означает, что параметра не было — тогда удаляем.
        if (string.IsNullOrEmpty(entry.OldValue))
        {
            return RegistryValueReader.DeleteValue(
                RegistryHive.LocalMachine, entry.Location, entry.Name);
        }

        if (!RegistryValueReader.WriteValue(
                RegistryHive.LocalMachine, entry.Location, entry.Name, entry.OldValue))
            return false;

        var actual = RegistryValueReader.ReadRawString(
            RegistryHive.LocalMachine, entry.Location, entry.Name);

        return string.Equals(actual, entry.OldValue, StringComparison.OrdinalIgnoreCase);
    }
}
