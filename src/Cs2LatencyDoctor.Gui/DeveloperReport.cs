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

        return new DeveloperDetails
        {
            Runtime = Environment.Version.ToString(),
            Is64Bit = Environment.Is64BitProcess,
            DataDirectory = Path.GetDirectoryName(journalPath) ?? "недоступен",
            UndoJournalExists = File.Exists(journalPath),
            UndoJournalEntries = SafeJournalEntryCount(),
            HistoryFileExists = File.Exists(SafeHistoryPath()),
            IsAdministrator = HostInfo.IsAdministrator()
        };
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
