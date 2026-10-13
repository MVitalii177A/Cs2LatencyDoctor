using System.IO;
using Cs2LatencyDoctor.Core;
using Cs2LatencyDoctor.Core.Fixes;
using Cs2LatencyDoctor.Core.History;
using Cs2LatencyDoctor.Core.Report;

namespace Cs2LatencyDoctor.Gui;

/// <summary>
/// Подробности о состоянии программы для отчёта.
///
/// Раньше здесь собирался целый отдельный JSON, и он пересекался с обычным отчётом:
/// версия, сведения о системе, находки и история попадали в оба файла. Две кнопки,
/// два похожих отчёта — непонятно, какой отправлять.
///
/// Теперь сборкой JSON занимается ReportExporter, а этот класс только добавляет то,
/// чего в обычном отчёте нет: состояние программы на чужой машине. Туда идут
/// журнал изменений, история замеров, папка с данными и версия среды выполнения —
/// по ним видно, как программа ведёт себя там, где её запустили впервые.
///
/// <b>Ключевое: файл никуда не отправляется.</b> Человек нажимает кнопку, видит,
/// что именно собрано, и сам решает, отправлять ли. Никакой автоматики здесь нет
/// и не будет — это то же обещание, что и «данные не покидают компьютер».
/// </summary>
public static class DeveloperReport
{
    /// <summary>Предлагаемое имя файла: с версией и датой, чтобы не путать отчёты.</summary>
    public static string SuggestFileName(DateTimeOffset now) =>
        $"cs2ld-отчёт-{AppVersion.Short}-{now:yyyy-MM-dd-HH-mm}.json";

    /// <summary>
    /// Сохранить подробный отчёт. Возвращает путь или null, если записать не удалось.
    /// Проверка не обязательна: если программа падает при запуске, отчёт нужен
    /// как раз без результатов проверки.
    /// </summary>
    public static string? Save(
        string path,
        DiagnosticReport? report,
        IReadOnlyList<CheckResult> findings,
        HistorySummary? history,
        out string? error)
    {
        try
        {
            ReportExporter.SaveJson(report, path, history, findings, Collect());

            error = null;
            return path;
        }
        catch (Exception ex)
        {
            error = ex.Message;
            return null;
        }
    }

    /// <summary>Собрать сведения о состоянии программы. Ошибки глушим: отчёт важнее подробностей.</summary>
    public static DeveloperDetails Collect()
    {
        var journalPath = SafeJournalPath();
        var changes = SafeAppliedChanges();

        return new DeveloperDetails
        {
            Runtime = Environment.Version.ToString(),
            Is64Bit = Environment.Is64BitProcess,
            DataDirectory = Path.GetDirectoryName(journalPath) ?? "недоступен",
            UndoJournalExists = File.Exists(journalPath),
            UndoJournalEntries = SafeJournalEntryCount(),
            HistoryFileExists = File.Exists(SafeHistoryPath()),
            IsAdministrator = HostInfo.IsAdministrator(),
            AppliedChanges = changes,
            RevertHint = BuildRevertHint(changes.Count)
        };
    }

    /// <summary>
    /// Что именно программа изменила в системе — для отчёта.
    ///
    /// Читаем журнал отмены: в нём каждое изменение с прежним и новым значением.
    /// Нужно, чтобы по отчёту можно было ответить на вопрос «а не из-за программы ли
    /// стало хуже в другой игре» — и вернуть всё назад, даже не имея доступа к машине.
    /// </summary>
    private static IReadOnlyList<DeveloperDetails.AppliedChange> SafeAppliedChanges()
    {
        try
        {
            return new UndoJournal().Entries
                .Select(entry => new DeveloperDetails.AppliedChange
                {
                    FixId = entry.FixId,
                    Title = entry.Title,
                    Kind = entry.Kind,
                    Location = entry.Location,
                    Name = entry.Name,
                    OldValue = entry.OldValue,
                    NewValue = entry.NewValue,
                    AppliedAt = entry.AppliedAt
                })
                .ToList();
        }
        catch
        {
            // Журнал недоступен или повреждён — это не повод не сохранить отчёт.
            return Array.Empty<DeveloperDetails.AppliedChange>();
        }
    }

    /// <summary>
    /// Как вернуть изменения назад. Пишем прямо в отчёт, потому что человек,
    /// читающий отчёт, может быть уже не за тем компьютером.
    /// </summary>
    private static string? BuildRevertHint(int changeCount)
    {
        if (changeCount == 0)
            return "Программа ничего не меняла: журнал изменений пуст.";

        return
            $"Программа внесла {changeCount} изменений. Вернуть всё назад можно двумя способами:" +
            Environment.NewLine +
            "  1. В программе: кнопка «Откатить изменения» — она вернёт значения из журнала." +
            Environment.NewLine +
            "  2. Вручную: в списке выше у каждого изменения указано, где оно находится, " +
            "какой параметр менялся и какое значение было раньше. Верните прежние значения " +
            "и перезагрузите компьютер." +
            Environment.NewLine +
            "Если после применения исправлений в ДРУГОЙ игре стало хуже — начните с отката: " +
            "так вы точно узнаете, связано это с программой или нет. " +
            "Откат не затрагивает настройки самих игр: они лежат в их собственных файлах.";
    }

    /// <summary>Что именно попадёт в файл и чего в нём не будет. Показывается человеку перед сохранением.</summary>
    public static string DescribeContents() => DeveloperDetails.DescribeContents();

    // ------------------------------------------------------------------ безопасное чтение состояния

    private static string SafeJournalPath()
    {
        try { return UndoJournal.ResolveJournalPath() ?? string.Empty; }
        catch { return string.Empty; }
    }

    private static int SafeJournalEntryCount()
    {
        try { return new UndoJournal().Entries.Count; }
        catch { return 0; }
    }

    private static string SafeHistoryPath()
    {
        try { return new HistoryStore().FilePathText; }
        catch { return string.Empty; }
    }
}
