using System.IO;
using System.Text;
using System.Text.Json;
using System.Text.Json.Serialization;
using Cs2LatencyDoctor.Core;
using Cs2LatencyDoctor.Core.Fixes;
using Cs2LatencyDoctor.Core.History;
using Cs2LatencyDoctor.Core.Windows;

namespace Cs2LatencyDoctor.Gui;

/// <summary>
/// Отчёт для разработчика: всё, что нужно для разбора проблемы, в одном файле.
///
/// Чем отличается от обычного отчёта. Обычный («Сохранить отчёт») сделан для человека:
/// его показывают провайдеру или выкладывают на форум. Этот — для того, кто будет
/// искать причину: в нём дополнительно состояние самой программы (журнал отмен,
/// история замеров, папки с данными, настройки), потому что половина ошибок
/// кроется не в проверках, а в том, как программа себя ведёт на чужой машине.
///
/// <b>Ключевое: файл никуда не отправляется.</b> Человек нажимает кнопку, видит,
/// что именно собрано, и сам решает, отправлять ли. Никакой автоматики здесь нет
/// и не будет — это то же обещание, что и «данные не покидают компьютер».
/// </summary>
public static class DeveloperReport
{
    private static readonly JsonSerializerOptions JsonOptions = new()
    {
        WriteIndented = true,
        DefaultIgnoreCondition = JsonIgnoreCondition.WhenWritingNull,
        Encoder = System.Text.Encodings.Web.JavaScriptEncoder.UnsafeRelaxedJsonEscaping
    };

    /// <summary>Предлагаемое имя файла: с версией и датой, чтобы не путать отчёты.</summary>
    public static string SuggestFileName(DateTimeOffset now) =>
        $"cs2ld-отчёт-{AppVersion.Short}-{now:yyyy-MM-dd-HH-mm}.json";

    /// <summary>
    /// Собрать отчёт. Сохраняется как JSON: его можно и прочитать глазами,
    /// и обработать скриптом, когда отчётов станет много.
    /// </summary>
    public static string BuildJson(
        DiagnosticReport? report,
        IReadOnlyList<CheckResult> findings,
        HistorySummary? history)
    {
        var system = SystemFingerprintReader.Read();

        var payload = new
        {
            // Что это за файл и как его читать.
            about = new
            {
                what = "Отчёт Cs2LatencyDoctor для разработчика",
                purpose = "Помогает найти причину проблемы. Отправляется вручную: программа его никуда не шлёт.",
                howToSend = "Приложите этот файл к сообщению: " + FeedbackLinks.NewIssueUrl(),
                privacy = "Имени компьютера, вашего имени и путей к личным папкам здесь нет. " +
                          "Только конфигурация железа, версия Windows и результат проверки."
            },

            program = new
            {
                name = "Cs2LatencyDoctor",
                version = AppVersion.Full,
                shortVersion = AppVersion.Short,
                buildDate = AppVersion.BuildDate,
                assemblyVersion = typeof(DeveloperReport).Assembly.GetName().Version?.ToString(),
                runtime = Environment.Version.ToString(),
                is64Bit = Environment.Is64BitProcess,
                culture = System.Globalization.CultureInfo.CurrentCulture.Name
            },

            // Сведения о системе: то, без чего разбор превращается в переписку.
            system = new
            {
                windows = system.ShortLine,
                architecture = system.Architecture,
                motherboard = system.Motherboard,
                bios = system.BiosVersion,
                cpu = system.Cpu,
                gpu = system.Gpu,
                gpuDriver = system.GpuDriver,
                memory = system.MemoryTotal,
                memorySpeed = system.MemorySpeed
            },

            // Состояние программы на этой машине.
            state = new
            {
                dataDirectory = SafeDataDirectory(),
                undoJournalExists = File.Exists(SafeJournalPath()),
                undoJournalEntries = SafeJournalEntryCount(),
                historyFileExists = File.Exists(SafeHistoryPath()),
                historySnapshots = SafeHistoryCount(),
                isAdministrator = HostInfo.IsAdministrator(),
                probeSeconds = 0
            },

            // Результат последней проверки.
            checkRun = report is null
                ? null
                : new
                {
                    durationSeconds = Math.Round(report.Duration.TotalSeconds, 1),
                    summary = report.Summary,
                    duration = report.Duration.TotalSeconds,
                    counts = new
                    {
                        problems = report.Count(Severity.Problem),
                        warnings = report.Count(Severity.Warning),
                        info = report.Count(Severity.Info),
                        ok = report.Count(Severity.Ok),
                        skipped = report.Count(Severity.Skipped)
                    }
                },

            // Все находки с объяснениями: это главное для разбора.
            findings = findings.Select(f => new
            {
                id = f.Id,
                title = f.Title,
                severity = f.Severity.ToString(),
                detail = f.Detail,
                why = f.Why,
                recommendation = f.Recommendation,
                noHelpReason = f.NoHelpReason == NoHelpReason.None ? null : f.NoHelpReason.ToString(),
                canFixItself = f.CanFixItself,
                fixes = f.Fixes.Select(x => new
                {
                    id = x.Id,
                    title = x.Title,
                    risk = x.Risk.ToString(),
                    note = x.Note,
                    canApplyAutomatically = x.CanApplyAutomatically,
                    whyNotAutomatic = x.WhyNotAutomatic
                }),
                metrics = f.Metrics.Count == 0 ? null : f.Metrics
            }),

            history = history is null || !history.HasHistory
                ? null
                : new
                {
                    totalRuns = history.TotalRuns,
                    runsWithProblems = history.RunsWithProblems,
                    text = history.Text
                }
        };

        return JsonSerializer.Serialize(payload, JsonOptions);
    }

    /// <summary>
    /// Записать отчёт в файл. Возвращает путь или null, если записать не удалось.
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
            var json = BuildJson(report, findings, history);

            // С меткой UTF-8: файл открывают Блокнотом, а он без метки
            // показывает русский текст кракозябрами.
            File.WriteAllText(path, json, new UTF8Encoding(encoderShouldEmitUTF8Identifier: true));

            error = null;
            return path;
        }
        catch (Exception ex)
        {
            error = ex.Message;
            return null;
        }
    }

    /// <summary>Короткое описание того, что попадёт в файл. Показывается человеку до сохранения.</summary>
    public static string DescribeContents()
    {
        var system = SystemFingerprintReader.Read();
        var lines = new List<string>
        {
            "В файл попадёт:",
            "",
            "  • версия программы и дата сборки;",
            "  • версия Windows и разрядность;"
        };

        if (system.HasHardware)
        {
            lines.Add("  • материнская плата, процессор, видеокарта с версией драйвера;");
            lines.Add("  • объём и частота памяти;");
        }

        lines.Add("  • все находки проверки с цифрами и пояснениями;");
        lines.Add("  • состояние программы: журнал изменений, история замеров.");
        lines.Add("");
        lines.Add("Чего в файле НЕ будет:");
        lines.Add("");
        lines.Add("  • имени компьютера и вашего имени;");
        lines.Add("  • путей к вашим личным папкам и файлам;");
        lines.Add("  • паролей, ключей и содержимого документов.");
        lines.Add("");
        lines.Add("Программа никуда этот файл не отправляет.");
        lines.Add("Он сохранится на диск, и вы сами решите, отправлять ли его.");

        return string.Join(Environment.NewLine, lines);
    }

    // ------------------------------------------------------------------ безопасное чтение состояния

    private static string SafeDataDirectory() =>
        Path.GetDirectoryName(SafeJournalPath()) ?? "недоступен";

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

    private static int SafeHistoryCount()
    {
        try { return new HistoryStore().Summarize().TotalRuns; }
        catch { return 0; }
    }
}
