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

    public FixReport ApplyAll(DiagnosticContext context, UndoJournal journal)
    {
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
            context.Progress($"Применяю: {fix.Title}…");

            try
            {
                if (!fix.CanApply(context))
                {
                    results.Add(FixResult.Skipped(fix.Id, fix.Title,
                        fix.SkipReason(context) ?? "Не выполнены условия применения"));
                    continue;
                }

                results.Add(fix.Apply(context, journal));
            }
            catch (Exception ex)
            {
                results.Add(FixResult.Failed(fix.Id, fix.Title, "Исключение: " + ex.Message));
            }
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
            if (fix is null)
            {
                results.Add(FixResult.Failed(entry.FixId, entry.Title,
                    "Не найдена реализация отката для этого исправления"));
                continue;
            }

            try
            {
                if (fix.Revert(entry, context))
                {
                    reverted.Add(entry);
                    results.Add(FixResult.Applied(entry.FixId, entry.Title,
                        $"Возвращено значение {entry.OldValue}", new[] { entry }));
                }
                else
                {
                    results.Add(FixResult.Failed(entry.FixId, entry.Title,
                        "Откат не подтвердился — проверьте параметр вручную"));
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
}
