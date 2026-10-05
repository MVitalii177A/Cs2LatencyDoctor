using System.Text.Json;
using System.Text.Json.Serialization;

namespace Cs2LatencyDoctor.Core.History;

/// <summary>Один сохранённый замер. Хранится только на диске пользователя и никуда не отправляется.</summary>
public sealed class HistorySnapshot
{
    public DateTimeOffset Timestamp { get; init; } = DateTimeOffset.Now;

    /// <summary>Версия программы, сделавшая замер — чтобы понимать, что менялось между замерами.</summary>
    public string? ToolVersion { get; init; }

    public required string Summary { get; init; }

    public int ProblemCount { get; init; }
    public int WarningCount { get; init; }
    public int OkCount { get; init; }

    /// <summary>Метрики по каждой проверке: id -> имя -> значение.</summary>
    public Dictionary<string, Dictionary<string, double>> Metrics { get; init; } = new();

    /// <summary>Заголовки проверок: id -> человеческое название (для отчётов).</summary>
    public Dictionary<string, string> Titles { get; init; } = new();
}

/// <summary>Изменение одной метрики между двумя замерами.</summary>
public sealed record MetricTrend(
    string CheckId,
    string Title,
    string MetricName,
    double FirstValue,
    double LastValue,
    int Samples)
{
    public double Delta => LastValue - FirstValue;

    /// <summary>Для метрик «чем меньше, тем лучше» уменьшение — это улучшение.</summary>
    public bool Improved => Delta < -0.5;
    public bool Worsened => Delta > 0.5;

    public string Verdict => Improved ? "лучше" : Worsened ? "хуже" : "без изменений";
}

/// <summary>Сводка по истории замеров: сколько раз запускали и что изменилось.</summary>
public sealed class HistorySummary
{
    public int TotalRuns { get; init; }
    public DateTimeOffset? FirstRun { get; init; }
    public DateTimeOffset? LastRun { get; init; }
    public int DaysTracked { get; init; }

    /// <summary>Замеры, в которых были найдены проблемы. Это и есть «сколько раз помогла».</summary>
    public int RunsWithProblems { get; init; }

    /// <summary>Метрики, которые улучшились между первым и последним замером.</summary>
    public IReadOnlyList<MetricTrend> Improved { get; init; } = Array.Empty<MetricTrend>();

    public IReadOnlyList<MetricTrend> Worsened { get; init; } = Array.Empty<MetricTrend>();

    public bool HasHistory => TotalRuns > 0;

    public string Text
    {
        get
        {
            if (!HasHistory) return "История пуста — это первый замер.";

            var days = DaysTracked <= 0 ? "меньше дня" : $"{DaysTracked} дн.";
            var text = $"Замеров: {TotalRuns}, период: {days}. ";

            if (Improved.Count > 0)
                text += $"Улучшилось метрик: {Improved.Count}. ";
            if (Worsened.Count > 0)
                text += $"Ухудшилось: {Worsened.Count}. ";
            if (Improved.Count == 0 && Worsened.Count == 0)
                text += "Существенных изменений нет.";

            return text.TrimEnd();
        }
    }
}

/// <summary>
/// Локальная история замеров. Пишется в файл рядом с журналом отката и НЕ покидает компьютер:
/// никакой сети здесь нет вообще, поэтому согласие пользователя не требуется.
///
/// Зачем: показывает человеку, помогли ли правки, и даёт честную метрику
/// «сколько раз программа нашла и устранила проблему» без слежки.
/// </summary>
public sealed class HistoryStore
{
    private static readonly JsonSerializerOptions JsonOptions = new()
    {
        DefaultIgnoreCondition = JsonIgnoreCondition.WhenWritingNull,
        Encoder = System.Text.Encodings.Web.JavaScriptEncoder.UnsafeRelaxedJsonEscaping
    };

    private readonly string? _filePath;

    public HistoryStore(string? filePath = null) => _filePath = filePath;

    /// <summary>Максимум хранимых замеров: история не должна расти бесконечно.</summary>
    public int MaxEntries { get; init; } = 500;

    public string FilePathText => ResolvePath() ?? "недоступен";

    private string? ResolvePath()
    {
        if (!string.IsNullOrEmpty(_filePath)) return _filePath;

        // Используем ту же логику поиска доступной папки, что и для журнала отката.
        var journalPath = Fixes.UndoJournal.ResolveJournalPath();
        if (journalPath is null) return null;

        var directory = Path.GetDirectoryName(journalPath);
        return directory is null ? null : Path.Combine(directory, "history.jsonl");
    }

    /// <summary>Добавить замер в историю.</summary>
    public bool Append(HistorySnapshot snapshot)
    {
        var path = ResolvePath();
        if (path is null) return false;

        try
        {
            File.AppendAllText(path, JsonSerializer.Serialize(snapshot, JsonOptions) + Environment.NewLine);
            TrimIfNeeded(path);
            return true;
        }
        catch
        {
            return false;
        }
    }

    /// <summary>Прочитать историю, от старых к новым.</summary>
    public IReadOnlyList<HistorySnapshot> Read()
    {
        var path = ResolvePath();
        if (path is null || !File.Exists(path)) return Array.Empty<HistorySnapshot>();

        var result = new List<HistorySnapshot>();

        try
        {
            foreach (var line in File.ReadLines(path))
            {
                if (string.IsNullOrWhiteSpace(line)) continue;

                try
                {
                    var snapshot = JsonSerializer.Deserialize<HistorySnapshot>(line, JsonOptions);
                    if (snapshot is not null) result.Add(snapshot);
                }
                catch
                {
                    // битая строка не должна ломать всю историю
                }
            }
        }
        catch
        {
            return Array.Empty<HistorySnapshot>();
        }

        return result;
    }

    /// <summary>
    /// Сводка: сколько замеров, за какой период и что изменилось между первым и последним.
    /// Сравниваем именно крайние точки, а не соседние: так видно общий результат работы.
    /// </summary>
    public HistorySummary Summarize()
    {
        var all = Read();
        if (all.Count == 0) return new HistorySummary { TotalRuns = 0 };

        var first = all[0];
        var last = all[^1];

        var trends = new List<MetricTrend>();

        foreach (var checkId in last.Metrics.Keys)
        {
            if (!first.Metrics.TryGetValue(checkId, out var firstMetrics)) continue;

            var title = last.Titles.TryGetValue(checkId, out var t) ? t : checkId;

            foreach (var (metricName, lastValue) in last.Metrics[checkId])
            {
                if (!firstMetrics.TryGetValue(metricName, out var firstValue)) continue;

                // Сравниваем только осмысленные метрики задержки.
                if (metricName is not ("spike_percent" or "loss_percent" or "stddev_ms" or "max_ms")) continue;

                trends.Add(new MetricTrend(checkId, title, metricName, firstValue, lastValue, all.Count));
            }
        }

        var days = (int)Math.Round((last.Timestamp - first.Timestamp).TotalDays);

        return new HistorySummary
        {
            TotalRuns = all.Count,
            FirstRun = first.Timestamp,
            LastRun = last.Timestamp,
            DaysTracked = Math.Max(0, days),
            RunsWithProblems = all.Count(s => s.ProblemCount > 0),
            Improved = trends.Where(t => t.Improved).ToList(),
            Worsened = trends.Where(t => t.Worsened).ToList()
        };
    }

    /// <summary>Ограничить размер файла, отбросив самые старые записи.</summary>
    private void TrimIfNeeded(string path)
    {
        try
        {
            var lines = File.ReadAllLines(path);
            if (lines.Length <= MaxEntries) return;

            var keep = lines.Skip(lines.Length - MaxEntries);
            File.WriteAllLines(path, keep);
        }
        catch
        {
            // не смогли подрезать — не критично
        }
    }

    /// <summary>Удалить историю целиком (пользователь должен иметь такую возможность).</summary>
    public bool Clear()
    {
        var path = ResolvePath();
        if (path is null) return false;

        try
        {
            if (File.Exists(path)) File.Delete(path);
            return true;
        }
        catch
        {
            return false;
        }
    }
}
