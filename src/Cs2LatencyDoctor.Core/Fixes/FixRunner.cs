using Cs2LatencyDoctor.Core.Windows;

namespace Cs2LatencyDoctor.Core.Fixes;

/// <summary>Итог применения или отката исправлений.</summary>
public sealed class FixReport
{
    public required IReadOnlyList<FixResult> Results { get; init; }

    /// <summary>Где лежит журнал отката. null — записать журнал не удалось (тогда откат только вручную).</summary>
    public string? JournalPath { get; init; }

    public bool JournalAvailable => !string.IsNullOrEmpty(JournalPath);

    public string JournalPathText => JournalPath ?? "недоступен (не удалось записать журнал!)";

    public int AppliedCount => Results.Count(r => r.Outcome == FixOutcome.Applied);
    public int SkippedCount => Results.Count(r => r.Outcome == FixOutcome.Skipped);
    public int FailedCount => Results.Count(r => r.Outcome == FixOutcome.Failed);
    public int AlreadyOkCount => Results.Count(r => r.Outcome == FixOutcome.AlreadyOk);

    public string Summary =>
        $"Применено: {AppliedCount}, уже было в порядке: {AlreadyOkCount}, " +
        $"пропущено: {SkippedCount}, ошибок: {FailedCount}";
}

/// <summary>Какой набор исправлений применять.</summary>
public enum FixSelection
{
    /// <summary>Только безопасные и обратимые. Ничего не ломает.</summary>
    Safe = 0,

    /// <summary>Безопасные плюс те, у которых есть компромисс (например отключение гипервизора).</summary>
    WithTradeoffs = 1
}

/// <summary>
/// Запускает исправления по очереди и ведёт журнал отката.
/// Порядок важен: сначала самое результативное.
/// </summary>
public sealed class FixRunner
{
    private readonly List<IFix> _fixes = new();

    public FixRunner Add(IFix fix)
    {
        _fixes.Add(fix);
        return this;
    }

    /// <summary>
    /// Стандартный набор. Tradeoff-исправлений (вроде отключения гипервизора) здесь нет:
    /// они ломают WSL2/Docker и должны быть отдельным осознанным выбором.
    /// </summary>
    public static FixRunner CreateDefault(FixSelection selection = FixSelection.Safe)
    {
        var runner = new FixRunner()
            .Add(new NetworkAdapterFix())
            .Add(new UsbPowerFix())
            .Add(new SchedulerFix())
            .Add(new Cs2DisplayModeFix());

        // Место для будущих исправлений с компромиссом:
        // if (selection == FixSelection.WithTradeoffs) runner.Add(new HypervisorFix());

        return runner;
    }

    /// <summary>
    /// Применить исправления из плана. План собирается из находок диагностики, поэтому
    /// программа делает ровно то, что нашла. Пустой план означает «применить всё,
    /// что программа умеет» — так работает консоль без предварительной проверки.
    /// </summary>
    public FixReport ApplyAll(DiagnosticContext context, UndoJournal journal, FixPlan? plan = null)
    {
        plan ??= FixPlan.Everything;

        var results = new List<FixResult>();

        // Если журнал записать некуда, менять настройки НЕЛЬЗЯ: откат станет невозможен.
        if (!journal.IsFileReady)
        {
            results.Add(FixResult.Failed("journal", "Журнал отката",
                "Не удалось создать файл журнала ни в одной из доступных папок. " +
                "Изменения не применялись: без журнала откат невозможен."));
            return new FixReport { Results = results, JournalPath = journal.FilePath };
        }

        foreach (var fix in _fixes)
        {
            // Исправление, которого нет в плане, пропускаем: находки его не просили.
            if (!plan.WantsFix(fix.Id)) continue;

            context.Progress($"Применяю: {fix.Title}…");

            try
            {
                if (!fix.CanApply(context))
                {
                    results.Add(FixResult.Skipped(fix.Id, fix.Title,
                        fix.SkipReason(context) ?? "Не выполнены условия применения"));
                    continue;
                }

                results.Add(fix.Apply(context, journal, plan));
            }
            catch (Exception ex)
            {
                results.Add(FixResult.Failed(fix.Id, fix.Title, "Исключение: " + ex.Message));
            }
        }

        if (results.Count == 0)
        {
            results.Add(FixResult.AlreadyOk("plan", "Исправления",
                "По результатам проверки применять нечего: всё уже настроено правильно"));
        }

        return new FixReport { Results = results, JournalPath = journal.FilePath };
    }

    /// <summary>
    /// Вернуть ОДНО изменение по записи журнала.
    ///
    /// Зачем по одной: раньше кнопка отката возвращала весь журнал целиком, и если
    /// человек хотел отменить только сетевые правки, оставив настройки игры, выбора
    /// у него не было. Возвращаем через общий путь, чтобы поведение совпадало
    /// с полным откатом.
    /// </summary>
    public static FixReport RevertOne(DiagnosticContext context, UndoJournal journal, JournalEntry entry)
    {
        var results = new List<FixResult>();

        var known = journal.Entries.Contains(entry);
        if (!known)
        {
            results.Add(FixResult.Failed(entry.FixId, entry.Title,
                "Этой записи уже нет в журнале — возможно, её вернули раньше"));
            return new FixReport { Results = results, JournalPath = journal.FilePath };
        }

        context.Progress($"Возвращаю: {entry.Title}…");

        var reverter = CreateDefault();
        var fix = reverter._fixes.FirstOrDefault(f => f.Id == entry.FixId);

        try
        {
            var ok = fix is not null ? fix.Revert(entry, context) : RevertFromJournalData(entry);

            if (!ok)
            {
                results.Add(FixResult.Failed(entry.FixId, entry.Title,
                    fix is null
                        ? "Не удалось вернуть по данным журнала — проверьте параметр вручную"
                        : "Откат не подтвердился — проверьте параметр вручную"));
                return new FixReport { Results = results, JournalPath = journal.FilePath };
            }

            journal.Remove(entry);

            results.Add(FixResult.Applied(entry.FixId, entry.Title,
                $"Возвращено значение {entry.OldValue}" +
                (fix is null ? " (по данным журнала)" : string.Empty),
                new[] { entry }));
        }
        catch (Exception ex)
        {
            results.Add(FixResult.Failed(entry.FixId, entry.Title, "Исключение: " + ex.Message));
        }

        return new FixReport { Results = results, JournalPath = journal.FilePath };
    }

    /// <summary>
    /// Вернуть всё, что записано в журнале. Идём в обратном порядке:
    /// так откат повторяет историю в обратную сторону.
    /// </summary>
    public static FixReport RevertAll(DiagnosticContext context, UndoJournal journal)
    {
        var results = new List<FixResult>();
        var entries = journal.Entries.Reverse().ToList();

        if (entries.Count == 0)
        {
            results.Add(FixResult.AlreadyOk("revert", "Откат изменений",
                "Журнал пуст — программа ничего не меняла"));
            return new FixReport { Results = results, JournalPath = journal.FilePath };
        }

        var reverted = new List<JournalEntry>();
        var reverter = CreateDefault();

        foreach (var entry in entries)
        {
            context.Progress($"Возвращаю: {entry.Title}…");

            var fix = reverter._fixes.FirstOrDefault(f => f.Id == entry.FixId);

            try
            {
                // Сначала пробуем обработчик исправления: он знает свои тонкости.
                // Если обработчика нет (журнал от другой версии программы) —
                // откатываем по данным самой записи, чтобы правка не осталась навсегда.
                var ok = fix is not null
                    ? fix.Revert(entry, context)
                    : RevertFromJournalData(entry);

                if (ok)
                {
                    reverted.Add(entry);
                    results.Add(FixResult.Applied(entry.FixId, entry.Title,
                        $"Возвращено значение {entry.OldValue}" +
                        (fix is null ? " (по данным журнала)" : string.Empty),
                        new[] { entry }));
                }
                else
                {
                    results.Add(FixResult.Failed(entry.FixId, entry.Title,
                        fix is null
                            ? "Не удалось вернуть по данным журнала — проверьте параметр вручную"
                            : "Откат не подтвердился — проверьте параметр вручную"));
                }
            }
            catch (Exception ex)
            {
                results.Add(FixResult.Failed(entry.FixId, entry.Title, "Исключение: " + ex.Message));
            }
        }

        // Из журнала убираем только то, что реально вернули.
        if (reverted.Count == entries.Count)
        {
            journal.Clear();
        }
        else
        {
            var remaining = journal.Entries.Where(e => !reverted.Contains(e)).ToList();
            journal.Clear();
            foreach (var entry in remaining) journal.Add(entry);
        }

        return new FixReport { Results = results, JournalPath = journal.FilePath };
    }

    /// <summary>
    /// Откат по данным самой записи журнала — без обработчика исправления.
    ///
    /// Зачем: журнал хранит, где именно и какое значение было до правки. Этого
    /// достаточно, чтобы вернуть настройку, даже если программа обновилась и
    /// исправление переименовали. Иначе такая запись застряла бы в журнале навсегда.
    /// </summary>
    public static bool RevertFromJournalData(JournalEntry entry)
    {
        return entry.Kind switch
        {
            // Ключевое слово драйвера сетевой карты и обычное значение реестра
            // возвращаются одинаково: ветка, путь и имя параметра есть в записи.
            "registryKeyword" or "registryValue" =>
                string.IsNullOrEmpty(entry.OldValue)
                    ? RegistryValueReader.DeleteValue(ParseHive(entry.Hive), entry.Location, entry.Name)
                    : RegistryValueReader.WriteValue(ParseHive(entry.Hive), entry.Location, entry.Name, entry.OldValue),

            // Настройка электропитания: значение возвращается через powercfg.
            "powercfg" => RevertPowerCfg(entry),

            // Файл настроек игры: есть резервная копия — восстанавливаем её.
            "cs2VideoFile" => RevertCs2VideoFile(entry),

            _ => false
        };
    }

    /// <summary>Разобрать название ветки реестра из записи журнала.</summary>
    private static Microsoft.Win32.RegistryHive ParseHive(string? hive) =>
        string.Equals(hive, "CurrentUser", StringComparison.OrdinalIgnoreCase)
            ? Microsoft.Win32.RegistryHive.CurrentUser
            : Microsoft.Win32.RegistryHive.LocalMachine;

    private static bool RevertPowerCfg(JournalEntry entry)
    {
        try
        {
            var parts = entry.Location.Split('/');
            if (parts.Length != 2) return false;

            var argument = entry.Name == "AC" ? "/setacvalueindex" : "/setdcvalueindex";
            var command = $"{argument} SCHEME_CURRENT {parts[0]} {parts[1]} {entry.OldValue}";

            var output = Windows.LatencyProbe
                .RunProcessAsync("powercfg.exe", command, CancellationToken.None)
                .GetAwaiter().GetResult();

            if (output is null) return false;

            Windows.LatencyProbe
                .RunProcessAsync("powercfg.exe", "/setactive SCHEME_CURRENT", CancellationToken.None)
                .GetAwaiter().GetResult();

            return true;
        }
        catch
        {
            return false;
        }
    }

    private static bool RevertCs2VideoFile(JournalEntry entry)
    {
        try
        {
            var backup = entry.Location + ".cs2latencydoc-backup";
            if (!File.Exists(backup)) return false;

            File.Copy(backup, entry.Location, overwrite: true);
            return true;
        }
        catch
        {
            return false;
        }
    }
}
