using System.Text;
using System.Text.Json;
using System.Text.Json.Serialization;
using Cs2LatencyDoctor.Core.History;
using Cs2LatencyDoctor.Core.Windows;

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
/// Что положить в отчёт дополнительно к результатам проверки.
///
/// Раньше это были два разных отчёта с двумя кнопками, и они пересекались:
/// версия, сведения о системе, находки и история попадали в оба. Разница была
/// только в подробностях о состоянии программы. Два отчёта об одном и том же
/// путают: непонятно, какой файл отправлять.
/// </summary>
public sealed class DeveloperDetails
{
    /// <summary>Версия среды выполнения: нужна, если проблема связана с .NET.</summary>
    public string? Runtime { get; init; }

    public bool Is64Bit { get; init; }

    /// <summary>Папка, куда программа пишет свои файлы.</summary>
    public string? DataDirectory { get; init; }

    public bool UndoJournalExists { get; init; }
    public int UndoJournalEntries { get; init; }
    public bool HistoryFileExists { get; init; }
    public bool IsAdministrator { get; init; }

    /// <summary>Что именно попадает в файл и чего в нём не будет. Показывается человеку.</summary>
    public static string DescribeContents() =>
        string.Join(Environment.NewLine, new[]
        {
            "В файл попадёт:",
            "",
            "  • версия программы и дата сборки;",
            "  • версия Windows и разрядность;",
            "  • материнская плата, процессор, видеокарта с версией драйвера;",
            "  • объём и частота памяти;",
            "  • все находки проверки с цифрами и пояснениями;",
            "  • состояние программы: журнал изменений, история замеров.",
            "",
            "Чего в файле НЕ будет:",
            "",
            "  • имени компьютера и вашего имени;",
            "  • путей к вашим личным папкам и файлам;",
            "  • паролей, ключей и содержимого документов.",
            "",
            "Программа никуда этот файл не отправляет.",
            "Он сохранится на диск, и вы сами решите, отправлять ли его."
        });
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

    /// <summary>Куда отправлять отчёт. Держим здесь, чтобы адрес не расходился с окном программы.</summary>
    public const string FeedbackUrl = "https://github.com/MVitalii177A/Cs2LatencyDoctor/issues/new";

    /// <summary>
    /// Папка, куда сохраняются отчёты. Создаётся при первом сохранении.
    ///
    /// Почему папка задана заранее, а не выбирается человеком. Раньше здесь было
    /// системное окно выбора файла, и оно падало: не в нашем коде, а внутри Windows,
    /// с кодом 0xc0000409 в библиотеке ucrtbase.dll. Такое падение невозможно
    /// поймать обработчиком исключений — процесс завершается мгновенно, без следа.
    ///
    /// Проверено на простейшей программе из тридцати строк: только системное окно
    /// и ничего больше — падает так же. Значит дело не в нашем коде, а в самом окне,
    /// и единственный надёжный выход — его не показывать.
    ///
    /// Заодно так удобнее: человек не думает, куда сохранить, а файл оказывается
    /// в предсказуемом месте рядом с программой.
    /// </summary>
    public static string ReportsDirectory
    {
        get
        {
            // Рядом с программой: человек найдёт отчёт там же, где саму программу,
            // и папка исчезнет вместе с ней, если он удалит программу.
            var baseDir = AppContext.BaseDirectory;

            return Path.Combine(baseDir, "отчёты");
        }
    }

    /// <summary>
    /// Полный путь для нового отчёта. Имя содержит дату и время, поэтому файлы
    /// не перезаписывают друг друга, даже если сохранять подряд.
    /// </summary>
    public static string SuggestFullPath(DateTimeOffset now, string extension)
    {
        var directory = ReportsDirectory;
        Directory.CreateDirectory(directory);

        return Path.Combine(directory, SuggestFileName(now, extension));
    }


    /// <summary>
    /// Имя файла: дата и время с точностью до секунды.
    ///
    /// Секунды обязательны. С точностью до минуты два отчёта, сохранённые подряд,
    /// получали одинаковое имя, и второй молча затирал первый — а человек видел
    /// только «отчёт сохранён» и терял предыдущий.
    /// </summary>
    public static string SuggestFileName(DateTimeOffset now, string extension) =>
        $"cs2-latency-{now:yyyy-MM-dd-HH-mm-ss}.{extension}";

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

    /// <summary>
    /// Сохранить отчёт в JSON. Если переданы подробности о состоянии программы,
    /// в файл добавляются разделы program и state.
    ///
    /// Один метод на оба случая намеренно. Раньше их было два, и они пересекались:
    /// версия, сведения о системе, находки и история попадали в оба файла, а
    /// различались только парой разделов. Два отчёта об одном и том же путают —
    /// непонятно, какой файл отправлять. Теперь это один отчёт, а подробности
    /// включаются по надобности.
    /// </summary>
    public static ExportedReport SaveJson(
        DiagnosticReport? report,
        string path,
        HistorySummary? history = null,
        IReadOnlyList<CheckResult>? findings = null,
        DeveloperDetails? developer = null)
    {
        var system = SystemFingerprintReader.Read();
        var results = findings ?? report?.Results ?? Array.Empty<CheckResult>();

        var payload = new
        {
            about = new
            {
                what = "Отчёт Cs2LatencyDoctor о проверке компьютера",
                tool = "Cs2LatencyDoctor",
                version = AppVersion.Full,
                buildDate = AppVersion.BuildDate,
                exportedAt = DateTimeOffset.Now,
                purpose = developer is null
                    ? "Результат проверки: находки, замеры и сведения о системе."
                    : "Результат проверки и состояние программы — для разбора проблемы.",
                privacy = "Имени компьютера, вашего имени и путей к личным папкам здесь нет.",
                howToSend = "Приложите этот файл к сообщению: " + FeedbackUrl
            },

            // Сведения о системе, на которой делалась проверка. Имени компьютера
            // здесь намеренно нет: для разбора ошибок оно не нужно, а человек
            // может выложить отчёт публично, не подумав об этом.
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

            // Состояние программы: только в подробном отчёте. Нужно, когда дело
            // не в проверках, а в том, как программа ведёт себя на чужой машине.
            program = developer is null ? null : new
            {
                runtime = developer.Runtime,
                is64Bit = developer.Is64Bit,
                isAdministrator = developer.IsAdministrator
            },

            state = developer is null ? null : new
            {
                dataDirectory = developer.DataDirectory,
                undoJournalExists = developer.UndoJournalExists,
                undoJournalEntries = developer.UndoJournalEntries,
                historyFileExists = developer.HistoryFileExists
            },

            checkRun = report is null ? null : new
            {
                durationSeconds = Math.Round(report.Duration.TotalSeconds, 1),
                summary = report.Summary,
                counts = new
                {
                    problems = report.Count(Severity.Problem),
                    warnings = report.Count(Severity.Warning),
                    info = report.Count(Severity.Info),
                    ok = report.Count(Severity.Ok),
                    skipped = report.Count(Severity.Skipped)
                }
            },

            history = history is null || !history.HasHistory
                ? null
                : new
                {
                    totalRuns = history.TotalRuns,
                    runsWithProblems = history.RunsWithProblems,
                    text = history.Text
                },

            // Находки: и в обычном, и в подробном отчёте — это главное.
            results = results.Select(r => new
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

        var system = SystemFingerprintReader.Read();

        builder.AppendLine("ПРОВЕРКА КОМПЬЮТЕРА ДЛЯ CS2");
        builder.AppendLine(new string('=', 60));
        builder.AppendLine(AppVersion.Display);
        builder.AppendLine($"Проверка выполнена: {DateTimeOffset.Now:dd.MM.yyyy HH:mm}");
        builder.AppendLine($"Длительность: {report.Duration.TotalSeconds:0.#} с");

        // Сведения о системе нужны, чтобы разобрать отчёт без переписки с вопросами
        // «а что у вас за плата». Имени компьютера здесь нет намеренно: для разбора
        // ошибок оно не нужно, а отчёт человек может выложить публично.
        builder.AppendLine();
        builder.AppendLine("НА ЧЁМ ВЫПОЛНЕНА ПРОВЕРКА");
        builder.AppendLine(new string('-', 60));

        foreach (var (label, value) in system.ToLines())
            builder.AppendLine($"  {label}: {value}");

        if (!system.HasHardware)
        {
            builder.AppendLine("  Сведения о железе прочитать не удалось.");
            builder.AppendLine("  Это не мешает разбору находок, но затрудняет поиск причины.");
        }

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
