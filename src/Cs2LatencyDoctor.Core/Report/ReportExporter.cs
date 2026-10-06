using System.Text;
using System.Text.Json;
using System.Text.Json.Serialization;
using Cs2LatencyDoctor.Core.History;

namespace Cs2LatencyDoctor.Core.Report;

/// <summary>Отчёт о проверке, сохранённый в файл.</summary>
public sealed class ExportedReport
{
    public required string FilePath { get; init; }
    public required string Format { get; init; }
    public long SizeBytes { get; init; }

    public string SizeText => SizeBytes < 1024
        ? $"{SizeBytes} байт"
        : $"{SizeBytes / 1024.0:0.#} КБ";
}

/// <summary>
/// Сохранение отчёта в файл.
///
/// Зачем это нужно. С результатами проверки человек идёт разговаривать: к провайдеру,
/// на форум, в поддержку. Диктовать цифры с экрана неудобно, а показать файл — просто.
/// Поэтому в отчёт попадает всё, что программа знает: версия, дата, находки, замеры
/// и отдельно то, чего программа сделать не смогла и почему.
///
/// Отчёт создаётся только по явной команде и никуда не отправляется: он остаётся
/// файлом на диске, как и всё остальное в этой программе.
/// </summary>
public static class ReportExporter
{
    private static readonly JsonSerializerOptions JsonOptions = new()
    {
        WriteIndented = true,
        DefaultIgnoreCondition = JsonIgnoreCondition.WhenWritingNull,
        Encoder = System.Text.Encodings.Web.JavaScriptEncoder.UnsafeRelaxedJsonEscaping
    };

    /// <summary>Предлагаемое имя файла: дата и время, чтобы отчёты не перезаписывали друг друга.</summary>
    public static string SuggestFileName(DateTimeOffset now, string extension) =>
        $"cs2-latency-{now:yyyy-MM-dd-HH-mm}.{extension}";

    /// <summary>Сохранить отчёт в текстовом виде: его можно прочитать глазами и переслать в чат.</summary>
    public static ExportedReport SaveText(
        DiagnosticReport report,
        string path,
        HistorySummary? history = null)
    {
        var text = BuildText(report, history);

        File.WriteAllText(path, text, new UTF8Encoding(encoderShouldEmitUTF8Identifier: true));

        return new ExportedReport
        {
            FilePath = path,
            Format = "текст",
            SizeBytes = new FileInfo(path).Length
        };
    }

    /// <summary>Сохранить отчёт в JSON: его можно обработать программой или скриптом.</summary>
    public static ExportedReport SaveJson(
        DiagnosticReport report,
        string path,
        HistorySummary? history = null)
    {
        var payload = new
        {
            tool = "Cs2LatencyDoctor",
            version = AppVersion.Full,
            buildDate = AppVersion.BuildDate,
            machine = Environment.MachineName,
            exportedAt = DateTimeOffset.Now,
            durationSeconds = Math.Round(report.Duration.TotalSeconds, 1),
            summary = report.Summary,
            history = history is null ? null : new
            {
                totalRuns = history.TotalRuns,
                runsWithProblems = history.RunsWithProblems,
                text = history.Text
            },
            results = report.Results.Select(r => new
            {
                id = r.Id,
                title = r.Title,
                severity = r.Severity.ToString(),
                detail = r.Detail,
                why = r.Why,
                recommendation = r.Recommendation,
                noHelpReason = r.NoHelpReason == NoHelpReason.None ? null : r.NoHelpReason.ToString(),
                canFixItself = r.CanFixItself,
                fixes = r.Fixes.Select(f => new
                {
                    id = f.Id,
                    title = f.Title,
                    risk = f.Risk.ToString(),
                    note = f.Note,
                    canApplyAutomatically = f.CanApplyAutomatically,
                    whyNotAutomatic = f.WhyNotAutomatic
                }),
                metrics = r.Metrics.Count == 0 ? null : r.Metrics
            })
        };

        var json = JsonSerializer.Serialize(payload, JsonOptions);
        File.WriteAllText(path, json, new UTF8Encoding(encoderShouldEmitUTF8Identifier: false));

        return new ExportedReport
        {
            FilePath = path,
            Format = "JSON",
            SizeBytes = new FileInfo(path).Length
        };
    }

    /// <summary>
    /// Текст отчёта. Пишем так, чтобы его можно было переслать без пояснений:
    /// с версией, датой и отдельным списком того, что осталось сделать руками.
    /// </summary>
    private static string BuildText(DiagnosticReport report, HistorySummary? history)
    {
        var builder = new StringBuilder();

        builder.AppendLine("ПРОВЕРКА КОМПЬЮТЕРА ДЛЯ CS2");
        builder.AppendLine(new string('=', 60));
        builder.AppendLine(AppVersion.Display);
        builder.AppendLine($"Проверка выполнена: {DateTimeOffset.Now:dd.MM.yyyy HH:mm}");
        builder.AppendLine($"Компьютер: {Environment.MachineName}");
        builder.AppendLine($"Длительность: {report.Duration.TotalSeconds:0.#} с");
        builder.AppendLine();
        builder.AppendLine("ИТОГ");
        builder.AppendLine(new string('-', 60));
        builder.AppendLine(report.Summary);

        if (history is { HasHistory: true })
        {
            builder.AppendLine();
            builder.AppendLine("ИСТОРИЯ ЗАМЕРОВ");
            builder.AppendLine(new string('-', 60));
            builder.AppendLine(history.Text);
        }

        builder.AppendLine();
        builder.AppendLine("НАХОДКИ");
        builder.AppendLine(new string('-', 60));

        foreach (var result in report.Results)
        {
            var mark = result.Severity switch
            {
                Severity.Problem => "ПРОБЛЕМА",
                Severity.Warning => "ВНИМАНИЕ",
                Severity.Info => "МОЖНО ЛУЧШЕ",
                Severity.Ok => "ОК",
                _ => "ПРОПУСК"
            };

            builder.AppendLine();
            builder.AppendLine($"[{mark}] {result.Title}");
            builder.AppendLine($"    {result.Detail}");

            if (!string.IsNullOrWhiteSpace(result.Why))
                builder.AppendLine($"    Почему: {result.Why}");

            foreach (var fix in result.Fixes)
            {
                var line = $"    → {fix.Title}";

                line += fix.CanApplyAutomatically
                    ? "  (программа сделает сама)"
                    : $"  (руками: {fix.WhyNotAutomatic ?? "требуется действие"})";

                builder.AppendLine(line);
            }

            if (!string.IsNullOrWhiteSpace(result.Recommendation))
                builder.AppendLine($"    Что делать: {result.Recommendation}");
        }

        // Отдельный список ручных действий: человеку нужен список дел, а не отчёт целиком.
        var manual = report.Results
            .SelectMany(r => r.ManualFixes.Select(f => new { Finding = r.Title, Fix = f }))
            .GroupBy(x => x.Fix.Title)
            .Select(g => g.First())
            .ToList();

        if (manual.Count > 0)
        {
            builder.AppendLine();
            builder.AppendLine("ЧТО НУЖНО СДЕЛАТЬ РУКАМИ");
            builder.AppendLine(new string('-', 60));

            foreach (var item in manual)
            {
                builder.AppendLine($"  · {item.Fix.Title}  ({item.Finding})");
                builder.AppendLine($"    {item.Fix.WhyNotAutomatic ?? "Требуется действие руками"}");
            }
        }

        builder.AppendLine();
        builder.AppendLine(new string('-', 60));
        builder.AppendLine("Отчёт создан программой Cs2LatencyDoctor и сохранён только на этом");
        builder.AppendLine("компьютере. Программа не отправляет данные в интернет.");

        return builder.ToString();
    }
}
