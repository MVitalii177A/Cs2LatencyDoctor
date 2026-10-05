namespace Cs2LatencyDoctor.Core.History;

/// <summary>Мост между отчётом диагностики и записью в историю.</summary>
public static class HistoryCapture
{
    /// <summary>Превратить отчёт в запись истории. Метрики берём только числовые — их можно сравнивать между запусками.</summary>
    public static HistorySnapshot ToSnapshot(this DiagnosticReport report)
    {
        var metrics = new Dictionary<string, Dictionary<string, double>>();
        var titles = new Dictionary<string, string>();

        foreach (var result in report.Results)
        {
            titles[result.Id] = result.Title;

            if (result.Metrics.Count == 0) continue;

            metrics[result.Id] = result.Metrics
                .Where(m => !double.IsNaN(m.Value) && !double.IsInfinity(m.Value))
                .ToDictionary(m => m.Key, m => m.Value);
        }

        return new HistorySnapshot
        {
            Timestamp = DateTimeOffset.Now,
            ToolVersion = typeof(HistoryCapture).Assembly.GetName().Version?.ToString(),
            Summary = report.Summary,
            ProblemCount = report.Count(Severity.Problem),
            WarningCount = report.Count(Severity.Warning),
            OkCount = report.Count(Severity.Ok),
            Metrics = metrics,
            Titles = titles
        };
    }

    /// <summary>
    /// Записать замер в историю и, если это не первый запуск, показать изменения.
    /// Возвращает сводку до добавления новой записи — чтобы было с чем сравнивать.
    /// </summary>
    public static HistorySummary CaptureAndSummarize(this DiagnosticReport report, HistoryStore store)
    {
        var before = store.Summarize();
        store.Append(report.ToSnapshot());
        return before;
    }
}
