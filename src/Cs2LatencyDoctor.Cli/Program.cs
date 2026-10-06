using System.Text;
using System.Text.Json;
using Cs2LatencyDoctor.Core;
using Cs2LatencyDoctor.Core.Background;
using Cs2LatencyDoctor.Core.Checks;
using Cs2LatencyDoctor.Core.Fixes;
using Cs2LatencyDoctor.Core.History;
using Cs2LatencyDoctor.Core.Report;

// Регистрируем старые кодировки: нужны для чтения вывода ping.exe и powercfg.
AppBootstrap.Initialize();

// Папку данных фиксируем один раз и передаём дочерним процессам через окружение.
// Без этого запуск от администратора через UAC писал бы журнал отката в профиль
// другого пользователя, и откат бы его не нашёл.
if (string.IsNullOrEmpty(Environment.GetEnvironmentVariable(UndoJournal.DataDirectoryVariable)))
{
    var resolved = UndoJournal.ResolveJournalPath();
    var directory = resolved is null ? null : Path.GetDirectoryName(resolved);
    if (directory is not null)
        Environment.SetEnvironmentVariable(UndoJournal.DataDirectoryVariable, directory);
}

Console.OutputEncoding = Encoding.UTF8;
Console.InputEncoding = Encoding.UTF8;

// ---------------------------------------------------------------- аргументы
var probeSeconds = 20;
var jsonMode = false;
var showHelp = false;
var selfTest = false;
var doFix = false;
var doRevert = false;
var doPlan = false;
var showHistory = false;
var doPause = false;
var doResume = false;
var assumeYes = false;
var showJournal = false;
var showPauseList = false;
var clearHistory = false;
var revertOneNumber = 0;
string? exportPath = null;

for (var i = 0; i < args.Length; i++)
{
    switch (args[i])
    {
        case "--seconds" or "-s" when i + 1 < args.Length && int.TryParse(args[i + 1], out var s):
            probeSeconds = Math.Clamp(s, 5, 120);
            i++;
            break;
        case "--json":
            jsonMode = true;
            break;
        case "--selftest":
            selfTest = true;
            break;
        case "--plan":
            doPlan = true;
            break;
        case "--fix":
            doFix = true;
            break;
        case "--revert":
            doRevert = true;
            break;
        case "--journal":
            showJournal = true;
            break;
        case "--export" when i + 1 < args.Length:
            exportPath = args[i + 1];
            i++;
            break;
        case "--revert-one":
            // Откат одной записи: номер берём из --journal.
            if (i + 1 < args.Length && int.TryParse(args[i + 1], out var entryNumber))
            {
                revertOneNumber = entryNumber;
                i++;
            }
            break;
        case "--history":
            showHistory = true;
            break;
        case "--history-clear":
            clearHistory = true;
            break;
        case "--pause":
            doPause = true;
            break;
        case "--pause-list":
            showPauseList = true;
            break;
        case "--resume":
            doResume = true;
            break;
        case "--yes" or "-y":
            assumeYes = true;
            break;
        case "--help" or "-h":
            showHelp = true;
            break;
    }
}

// ------------------------------------------------------------- самопроверка
if (selfTest)
{
    return SelfTestCommand.Run();
}

// ------------------------------------------------------------------ история
if (clearHistory)
{
    return HistoryCommand.Clear(assumeYes);
}

if (showHistory)
{
    return HistoryCommand.Run();
}

// ------------------------------------------------------------------ журнал
if (showJournal || revertOneNumber > 0)
{
    return JournalCommand.Run(showJournal, revertOneNumber);
}

// ------------------------------------------- свой список программ для паузы
if (showPauseList)
{
    var path = BackgroundAppService.EnsureUserListFile();

    if (path is null)
    {
        Console.Error.WriteLine("  Не удалось создать файл: каталог данных недоступен.");
        return 4;
    }

    Console.WriteLine();
    Console.WriteLine("  СВОЙ СПИСОК ПРОГРАММ ДЛЯ ПАУЗЫ");
    Console.WriteLine("  " + new string('-', 70));
    Console.WriteLine("  Файл: " + path);
    Console.WriteLine();
    Console.WriteLine("  Впишите в него имена процессов — по одному в строке, без .exe.");
    Console.WriteLine("  Эти программы появятся в списке кандидатов при следующем запуске.");
    Console.WriteLine();
    return 0;
}

// ----------------------------------------------------------- пауза фоновых
if (doPause || doResume)
{
    return PauseCommand.Run(doPause, doResume, assumeYes, jsonMode);
}

// ------------------------------------------------------- исправления и откат
if (doFix || doRevert || doPlan)
{
    return await FixCommand.RunAsync(doFix, doRevert, doPlan, probeSeconds, jsonMode);
}

if (showHelp)
{
    Console.WriteLine("""
        Cs2LatencyDoctor — диагностика и исправление причин задержек в CS2.

        Использование:
          cs2latency [--seconds N] [--json]   только диагностика, ничего не меняет
          cs2latency --plan                   что можно исправить (ничего не меняет)
          cs2latency --export FILE            сохранить отчёт: .txt для чтения, .json для обработки

        Исправления (нужны права администратора):
          cs2latency --fix                    применить исправления по найденному
          cs2latency --revert                 вернуть всё как было
          cs2latency --journal                что именно было изменено
          cs2latency --revert-one N           вернуть одну запись из журнала

        История замеров:
          cs2latency --history                что улучшилось и что ухудшилось
          cs2latency --history-clear          удалить историю замеров

        Фоновые программы:
          cs2latency --pause                  поставить на паузу
          cs2latency --resume                 вернуть обратно
          cs2latency --pause-list             файл со своим списком программ

        Прочее:
          cs2latency --selftest               проверить логику оценки на записанных данных
          cs2latency --help                   эта справка

        Параметры:
          --seconds N   длительность замера сети в секундах (5..120, по умолчанию 20)
          --json        вывести отчёт в формате JSON
          --yes         не спрашивать подтверждение (для --pause и --history-clear)

        Без параметров программа только читает состояние и показывает находки.
        При исправлении каждое изменённое значение сохраняется в журнал,
        поэтому --revert возвращает систему в исходное состояние.

        История замеров хранится ТОЛЬКО на этом компьютере и никуда не отправляется.
        """);
    return 0;
}

// ------------------------------------------------------------------ запуск
var isAdmin = HostInfo.IsAdministrator();

if (!jsonMode)
{
    Console.WriteLine();
    Console.WriteLine("  Cs2LatencyDoctor — что мешает играть в CS2");
        Console.WriteLine("  " + AppVersion.Display);
    Console.WriteLine("  " + new string('─', 66));
    Console.WriteLine($"  Права администратора: {(isAdmin ? "есть" : "нет (часть проверок будет пропущена)")}");
    Console.WriteLine($"  Замер сети: {probeSeconds} сек на каждый узел");
    Console.WriteLine();
}

var context = new DiagnosticContext
{
    IsAdministrator = isAdmin,
    ProbeSeconds = probeSeconds,
    OnProgress = jsonMode ? null : message => Console.Write("\r  " + message.PadRight(64))
};

var runner = DiagnosticRunner.CreateDefault(probeSeconds);
var report = await runner.RunAsync(context);

if (!jsonMode) Console.Write("\r" + new string(' ', 66) + "\r");

// Сохраняем замер в локальную историю: она нужна, чтобы показать человеку,
// помогли ли правки. История не покидает компьютер.
var historyStore = new HistoryStore();
var historyBefore = report.CaptureAndSummarize(historyStore);

// ------------------------------------------------------------------- экспорт
// Отчёт сохраняется по явной команде и остаётся файлом на диске: программа
// ничего никуда не отправляет — это её основное обещание.
if (exportPath is not null)
{
    try
    {
        var fullPath = Path.GetFullPath(exportPath);
        var isJson = fullPath.EndsWith(".json", StringComparison.OrdinalIgnoreCase);

        var exported = isJson
            ? ReportExporter.SaveJson(report, fullPath, historyBefore)
            : ReportExporter.SaveText(report, fullPath, historyBefore);

        if (!jsonMode)
        {
            Console.WriteLine();
            Console.WriteLine($"  Отчёт сохранён ({exported.Format}, {exported.SizeText}):");
            Console.WriteLine("  " + exported.FilePath);
        }
    }
    catch (Exception ex)
    {
        Console.Error.WriteLine("  Не удалось сохранить отчёт: " + ex.Message);
        return 4;
    }
}

// ------------------------------------------------------------------- вывод
if (jsonMode)
{
    var payload = new
    {
        startedAt = report.StartedAt,
        durationSeconds = Math.Round(report.Duration.TotalSeconds, 1),
        summary = report.Summary,
        history = new
        {
            totalRuns = historyBefore.TotalRuns,
            runsWithProblems = historyBefore.RunsWithProblems,
            text = historyBefore.Text
        },
        results = report.Results.Select(r => new
        {
            id = r.Id,
            title = r.Title,
            severity = r.Severity.ToString(),
            detail = r.Detail,
            why = r.Why,
            recommendation = r.Recommendation,
            noHelpReason = r.NoHelpReason.ToString(),
            fixes = r.Fixes.Select(f => new { id = f.Id, title = f.Title, risk = f.Risk.ToString(), note = f.Note }),
            metrics = r.Metrics
        })
    };

    Console.WriteLine(JsonSerializer.Serialize(payload, new JsonSerializerOptions { WriteIndented = true }));
    return report.Count(Severity.Problem) > 0 ? 2 : 0;
}

// Если замеры уже были раньше — показываем, что изменилось с прошлого раза.
if (historyBefore.HasHistory)
{
    Console.WriteLine("  " + new string('─', 66));
    Console.WriteLine("  ЧТО ИЗМЕНИЛОСЬ С ПРОШЛЫХ ЗАМЕРОВ");
    Console.WriteLine("  " + new string('─', 66));
    Console.WriteLine("  " + historyBefore.Text);

    foreach (var trend in historyBefore.Improved.Take(6))
    {
        Console.ForegroundColor = ConsoleColor.Green;
        Console.WriteLine($"    {trend.Title}: {MetricName(trend.MetricName)} " +
                          $"{trend.FirstValue:0.#} → {trend.LastValue:0.#}  ({trend.Verdict})");
        Console.ResetColor();
    }

    foreach (var trend in historyBefore.Worsened.Take(6))
    {
        Console.ForegroundColor = ConsoleColor.Red;
        Console.WriteLine($"    {trend.Title}: {MetricName(trend.MetricName)} " +
                          $"{trend.FirstValue:0.#} → {trend.LastValue:0.#}  ({trend.Verdict})");
        Console.ResetColor();
    }

    Console.WriteLine();
}

Console.WriteLine("  " + new string('─', 66));
Console.WriteLine("  НАХОДКИ");
Console.WriteLine("  " + new string('─', 66));

foreach (var result in report.Results)
{
    var (mark, color) = result.Severity switch
    {
        Severity.Problem => ("[ПРОБЛЕМА]   ", ConsoleColor.Red),
        Severity.Warning => ("[ВНИМАНИЕ]   ", ConsoleColor.Yellow),
        Severity.Info => ("[МОЖНО ЛУЧШЕ]", ConsoleColor.DarkYellow),
        Severity.Ok => ("[ОК]         ", ConsoleColor.Green),
        _ => ("[ПРОПУСК]    ", ConsoleColor.DarkGray)
    };

    Console.ForegroundColor = color;
    Console.Write("  " + mark + " ");
    Console.ResetColor();
    Console.WriteLine(result.Title);
    Console.WriteLine("                " + result.Detail);

    if (!string.IsNullOrWhiteSpace(result.Why))
    {
        Console.ForegroundColor = ConsoleColor.DarkGray;
        foreach (var line in Wrap(result.Why, 70))
            Console.WriteLine("                " + line);
        Console.ResetColor();
    }

    foreach (var fix in result.Fixes)
    {
        Console.ForegroundColor = ConsoleColor.Cyan;
        Console.WriteLine($"                → исправимо: {fix.Title}" +
                          (fix.Risk == FixRisk.Tradeoff ? "  (с компромиссом)" : string.Empty));
        Console.ResetColor();
    }

    // Если программа помочь не может или может не всё — объясняем, почему и что делать.
    if (result.HasRecommendation)
    {
        var label = result.NoHelpReason == NoHelpReason.None
            ? "Что сделать вам"
            : $"Что делать ({DescribeReason(result.NoHelpReason)})";

        Console.ForegroundColor = ConsoleColor.Magenta;
        Console.WriteLine($"                → {label}:");
        Console.ResetColor();

        foreach (var line in Wrap(result.Recommendation!, 66))
            Console.WriteLine("                  " + line);
    }

    Console.WriteLine();
}

// ------------------------------------------------------------------- итоги
Console.WriteLine("  " + new string('─', 66));
Console.WriteLine($"  ИТОГ: {report.Summary}");
Console.WriteLine($"  Проверок: {report.Results.Count}, время: {report.Duration.TotalSeconds:0.#} с");
Console.WriteLine();

var manualFixes = report.Results.SelectMany(r => r.Fixes)
    .Where(f => f.Risk == FixRisk.ManualOnly)
    .DistinctBy(f => f.Id)
    .ToList();

if (manualFixes.Count > 0)
{
    Console.WriteLine("  Требует ручного действия:");
    foreach (var fix in manualFixes)
        Console.WriteLine($"    • {fix.Title}" + (fix.Note is null ? "" : $" — {fix.Note}"));
    Console.WriteLine();
}

Console.ForegroundColor = ConsoleColor.DarkGray;
Console.WriteLine("  Программа ничего не изменяла. Это только диагностика.");
Console.ResetColor();
Console.WriteLine();

return report.Count(Severity.Problem) > 0 ? 2 : 0;

// ------------------------------------------------------------------ хелперы
/// <summary>Человеческое объяснение причины, по которой программа не смогла помочь.</summary>
static string DescribeReason(NoHelpReason reason) => reason switch
{
    NoHelpReason.HardwareNotSupported => "железо не поддерживает настройку",
    NoHelpReason.BlockedBySystem => "система блокирует доступ",
    NoHelpReason.VendorLocked => "закрыто производителем",
    NoHelpReason.OutsideThisPc => "причина вне компьютера",
    NoHelpReason.NeedsPhysicalAction => "нужно физическое действие",
    NoHelpReason.NotEnoughData => "не хватает данных",
    NoHelpReason.NeedsAdmin => "нужны права администратора",
    _ => "пояснение"
};

static string MetricName(string metric) => metric switch
{
    "spike_percent" => "всплески, %",
    "loss_percent" => "потери, %",
    "stddev_ms" => "разброс, мс",
    "max_ms" => "максимум, мс",
    "median_ms" => "медиана, мс",
    _ => metric
};

static IEnumerable<string> Wrap(string text, int width)
{
    var words = text.Split(' ', StringSplitOptions.RemoveEmptyEntries);
    var line = new StringBuilder();

    foreach (var word in words)
    {
        if (line.Length + word.Length + 1 > width && line.Length > 0)
        {
            yield return line.ToString();
            line.Clear();
        }

        if (line.Length > 0) line.Append(' ');
        line.Append(word);
    }

    if (line.Length > 0) yield return line.ToString();
}

internal static class HostInfo
{
    public static bool IsAdministrator()
    {
        try
        {
            using var identity = System.Security.Principal.WindowsIdentity.GetCurrent();
            var principal = new System.Security.Principal.WindowsPrincipal(identity);
            return principal.IsInRole(System.Security.Principal.WindowsBuiltInRole.Administrator);
        }
        catch
        {
            return false;
        }
    }
}

/// <summary>
/// Применение исправлений, просмотр плана и откат.
/// Каждое изменённое значение пишется в журнал ДО правки — иначе откат невозможен.
/// </summary>
internal static class FixCommand
{
    public static async Task<int> RunAsync(bool fix, bool revert, bool plan, int probeSeconds, bool jsonMode)
    {
        var isAdmin = HostInfo.IsAdministrator();
        var journal = new UndoJournal();

        if (fix && revert)
        {
            Console.WriteLine("  Нельзя одновременно --fix и --revert. Выберите одно.");
            return 1;
        }

        // ------------------------------------------------------------------ откат
        if (revert)
        {
            Console.WriteLine();
            Console.WriteLine("  ОТКАТ ИЗМЕНЕНИЙ");
            Console.WriteLine("  " + new string('-', 70));
            Console.WriteLine("  Журнал: " + journal.JournalPathText());
            Console.WriteLine("  Записей: " + journal.Entries.Count);
            Console.WriteLine();

            if (!isAdmin)
            {
                Console.WriteLine("  Нужны права администратора. Запустите программу от имени администратора.");
                return 1;
            }

            if (journal.Entries.Count == 0)
            {
                Console.WriteLine("  Программа ничего не меняла — откатывать нечего.");
                return 0;
            }

            var context = new DiagnosticContext
            {
                IsAdministrator = isAdmin,
                OnProgress = jsonMode ? null : m => Console.Write("\r  " + m.PadRight(68))
            };

            var revertReport = FixRunner.RevertAll(context, journal);
            if (!jsonMode) Console.Write("\r" + new string(' ', 70) + "\r");
            PrintReport(revertReport, "ОТКАТ");

            return revertReport.FailedCount > 0 ? 3 : 0;
        }

        // ------------------------------------------------------------------- план
        if (plan)
        {
            return await PrintPlanAsync(probeSeconds, jsonMode);
        }

        // ------------------------------------------------------------ применение
        Console.WriteLine();
        Console.WriteLine("  ПРИМЕНЕНИЕ ИСПРАВЛЕНИЙ");
        Console.WriteLine("  " + new string('─', 70));

        if (!isAdmin)
        {
            Console.WriteLine("  Нужны права администратора: исправления меняют системные настройки.");
            Console.WriteLine("  Запустите программу от имени администратора.");
            return 1;
        }

        // Сначала диагностика: она же определяет основной адаптер и путь к CS2.
        var diagContext = new DiagnosticContext
        {
            IsAdministrator = isAdmin,
            ProbeSeconds = probeSeconds,
            OnProgress = jsonMode ? null : m => Console.Write("\r  " + m.PadRight(68))
        };

        var diagnostic = await DiagnosticRunner.CreateDefault(probeSeconds).RunAsync(diagContext);
        if (!jsonMode) Console.Write("\r" + new string(' ', 70) + "\r");

        Console.WriteLine($"  Найдено до правки: {diagnostic.Summary}");
        Console.WriteLine();

        var runner = FixRunner.CreateDefault(FixSelection.Safe);

        // План собирается из находок: программа применяет ровно то, на что жаловалась
        // диагностика, а не весь набор исправлений целиком.
        var fixPlan = FixPlan.FromFindings(diagnostic.Results);
        if (!fixPlan.IsEmpty)
            Console.WriteLine($"  К применению: {fixPlan.Describe()}");

        var report = runner.ApplyAll(diagContext, journal, fixPlan);

        PrintReport(report, "ИСПРАВЛЕНИЯ");

        // Перезапуск адаптера нужен, чтобы настройки сетевой карты вступили в силу.
        var needAdapterRestart = report.Results
            .Any(r => r.Outcome == FixOutcome.Applied && r.FixId.StartsWith("net.adapter", StringComparison.Ordinal));

        if (needAdapterRestart)
        {
            Console.WriteLine("  Настройки сетевой карты применятся после перезапуска адаптера.");
            Console.WriteLine("  Перезапустить сейчас? Это кратко разорвёт сеть (y/n): ");

            if (!jsonMode && Console.ReadLine()?.Trim().ToLowerInvariant() is "y" or "yes" or "д" or "да")
            {
                var adapterName = diagContext.PrimaryAdapterName;
                if (!string.IsNullOrEmpty(adapterName))
                {
                    var ok = NetworkAdapterRestart.TryRestart(adapterName);
                    Console.WriteLine(ok
                        ? "  Адаптер перезапущен."
                        : "  Не удалось перезапустить автоматически — отключите и включите сеть вручную.");
                }
            }
            else
            {
                Console.WriteLine("  Пропущено. Перезапустите адаптер вручную или перезагрузите ПК.");
            }
        }

        Console.WriteLine();
        Console.WriteLine("  Откатить всё назад:  cs2latency --revert");
        Console.WriteLine();

        if (!jsonMode)
        {
            Console.WriteLine("  Проверяю результат повторным замером…");
            Console.WriteLine();

            var after = new DiagnosticContext
            {
                IsAdministrator = isAdmin,
                ProbeSeconds = probeSeconds,
                OnProgress = m => Console.Write("\r  " + m.PadRight(68))
            };

            var afterReport = await DiagnosticRunner.CreateDefault(probeSeconds).RunAsync(after);
            Console.Write("\r" + new string(' ', 70) + "\r");
            Console.WriteLine($"  После правки: {afterReport.Summary}");
            Console.WriteLine();

            CompareMetrics(diagnostic, afterReport);
        }

        return report.FailedCount > 0 ? 3 : 0;
    }

    // ------------------------------------------------------------------- план
    private static async Task<int> PrintPlanAsync(int probeSeconds, bool jsonMode)
    {
        var isAdmin = HostInfo.IsAdministrator();

        Console.WriteLine();
        Console.WriteLine("  ПЛАН ИСПРАВЛЕНИЙ (ничего не меняется)");
        Console.WriteLine("  " + new string('─', 70));
        Console.WriteLine($"  Права администратора: {(isAdmin ? "есть" : "нет")}");
        Console.WriteLine();

        var context = new DiagnosticContext
        {
            IsAdministrator = isAdmin,
            ProbeSeconds = probeSeconds,
            OnProgress = jsonMode ? null : m => Console.Write("\r  " + m.PadRight(68))
        };

        var report = await DiagnosticRunner.CreateDefault(probeSeconds).RunAsync(context);
        if (!jsonMode) Console.Write("\r" + new string(' ', 70) + "\r");

        var fixable = report.Results.Where(r => r.Fixes.Count > 0).ToList();

        if (fixable.Count == 0)
        {
            Console.WriteLine("  Исправлять нечего: все проверенные параметры уже в порядке.");
        }
        else
        {
            foreach (var result in fixable)
            {
                var mark = result.Severity switch
                {
                    Severity.Problem => "ПРОБЛЕМА",
                    Severity.Warning => "ВНИМАНИЕ",
                    _ => "МОЖНО ЛУЧШЕ"
                };

                Console.WriteLine($"  [{mark}] {result.Title}");
                Console.WriteLine($"             {result.Detail}");

                foreach (var fix in result.Fixes)
                {
                    var risk = fix.Risk switch
                    {
                        FixRisk.Safe => "безопасно, обратимо",
                        FixRisk.Tradeoff => "есть компромисс",
                        _ => "только вручную"
                    };

                    // Отдельно отмечаем то, что программа сделает по кнопке, и то,
                    // что придётся делать самому — с причиной, а не молча.
                    var suffix = fix.CanApplyAutomatically
                        ? string.Empty
                        : "   ← программа не сделает: " + (fix.WhyNotAutomatic ?? "нужно действие руками");

                    Console.WriteLine($"             → {fix.Title}  ({risk}){suffix}");
                }

                Console.WriteLine();
            }
        }

        var manual = report.Results.SelectMany(r => r.Fixes)
            .Where(f => f.Risk == FixRisk.ManualOnly)
            .DistinctBy(f => f.Id)
            .ToList();

        if (manual.Count > 0)
        {
            Console.WriteLine("  Требует ручного действия:");
            foreach (var fix in manual)
                Console.WriteLine($"    • {fix.Title}" + (fix.Note is null ? "" : $" — {fix.Note}"));
            Console.WriteLine();
        }

        var applicable = FixPlan.FromFindings(report.Results);

    if (applicable.IsEmpty)
        Console.WriteLine("  Применять нечего: программа и так ничего не будет менять.");
    else
    {
        Console.WriteLine("  Программа применит по кнопке или командой --fix:");
        foreach (var action in report.Results.SelectMany(r => r.ApplicableFixes)
                     .DistinctBy(f => f.SubActionId ?? f.Id))
        {
            Console.WriteLine("    · " + action.Title);
        }

        Console.WriteLine();
        Console.WriteLine("  Применить:  cs2latency --fix");
    }
        Console.WriteLine();

        return 0;
    }

    // ---------------------------------------------------------------- вывод
    private static void PrintReport(FixReport report, string header)
    {
        Console.WriteLine("  " + new string('─', 70));
        Console.WriteLine($"  {header}: {report.Summary}");
        Console.WriteLine("  " + new string('─', 70));

        foreach (var result in report.Results)
        {
            var (mark, color) = result.Outcome switch
            {
                FixOutcome.Applied => ("[ПРИМЕНЕНО]", ConsoleColor.Green),
                FixOutcome.AlreadyOk => ("[УЖЕ ОК]   ", ConsoleColor.DarkGreen),
                FixOutcome.Skipped => ("[ПРОПУЩЕНО]", ConsoleColor.DarkGray),
                _ => ("[ОШИБКА]   ", ConsoleColor.Red)
            };

            Console.ForegroundColor = color;
            Console.Write("  " + mark + " ");
            Console.ResetColor();
            Console.WriteLine(result.Title);
            Console.WriteLine("                " + result.Message);
            Console.WriteLine();
        }

        Console.WriteLine($"  Журнал отката: {report.JournalPathText}");
        Console.WriteLine();
    }

    /// <summary>Сравнение метрик до и после — это и есть доказательство результата.</summary>
    private static void CompareMetrics(DiagnosticReport before, DiagnosticReport after)
    {
        var pairs = new List<(string Name, double Before, double After)>();

        foreach (var afterResult in after.Results)
        {
            var beforeResult = before.Results.FirstOrDefault(r => r.Id == afterResult.Id);
            if (beforeResult is null) continue;

            foreach (var metric in afterResult.Metrics)
            {
                if (metric.Key != "spike_percent") continue;
                if (!beforeResult.Metrics.TryGetValue(metric.Key, out var oldValue)) continue;
                pairs.Add((afterResult.Title, oldValue, metric.Value));
            }
        }

        if (pairs.Count == 0)
        {
            Console.WriteLine("  Сравнить замеры не удалось (нет общих метрик).");
            return;
        }

        Console.WriteLine("  СРАВНЕНИЕ ДО/ПОСЛЕ (всплески задержки, %)");
        Console.WriteLine("  " + new string('─', 70));

        foreach (var (name, beforeValue, afterValue) in pairs)
        {
            var delta = afterValue - beforeValue;
            var verdict = delta < -0.5 ? "лучше" : delta > 0.5 ? "хуже" : "без изменений";

            Console.ForegroundColor = delta < -0.5 ? ConsoleColor.Green
                : delta > 0.5 ? ConsoleColor.Red : ConsoleColor.Gray;
            Console.WriteLine($"  {name,-34} {beforeValue,6:0.#}% → {afterValue,6:0.#}%   {verdict}");
            Console.ResetColor();
        }

        Console.WriteLine();
    }
}

/// <summary>Перезапуск сетевого адаптера, чтобы правки драйвера вступили в силу.</summary>
internal static class NetworkAdapterRestart
{
    public static bool TryRestart(string adapterName)
    {
        try
        {
            var psi = new System.Diagnostics.ProcessStartInfo
            {
                FileName = "powershell.exe",
                Arguments = $"-NoProfile -Command \"Restart-NetAdapter -Name '{adapterName}' -Confirm:$false\"",
                UseShellExecute = false,
                CreateNoWindow = true
            };

            using var process = System.Diagnostics.Process.Start(psi);
            process?.WaitForExit(30000);
            Thread.Sleep(5000);
            return true;
        }
        catch
        {
            return false;
        }
    }
}

/// <summary>
/// Пауза фоновых программ на время игры и возврат их обратно.
/// Останавливаются только процессы из списка кандидатов, есть защита от опасных имён.
/// </summary>
internal static class PauseCommand
{
    public static int Run(bool pause, bool resume, bool assumeYes, bool jsonMode)
    {
        var service = new BackgroundAppService();
        var context = new DiagnosticContext
        {
            IsAdministrator = HostInfo.IsAdministrator(),
            OnProgress = jsonMode ? null : m => Console.WriteLine("  " + m)
        };

        Console.WriteLine();

        // ------------------------------------------------------------------ возврат
        if (resume)
        {
            Console.WriteLine("  ВОЗВРАТ ФОНОВЫХ ПРОГРАММ");
            Console.WriteLine("  " + new string('-', 70));

            var state = service.GetCurrentState();
            if (state.IsEmpty)
            {
                Console.WriteLine("  Ничего не стоит на паузе.");
                Console.WriteLine();
                return 0;
            }

            Console.WriteLine($"  На паузе с {state.CreatedAt:dd.MM.yyyy HH:mm}: {state.Apps.Count} программ");
            foreach (var app in state.Apps)
                Console.WriteLine($"    • {app.Title}");
            Console.WriteLine();

            var (restored, failed) = service.Resume(context);

            Console.WriteLine();
            Console.WriteLine($"  Возвращено: {restored}, не удалось запустить: {failed}");
            if (failed > 0)
                Console.WriteLine("  Запустите их вручную из меню Пуск.");

            // Проверяем, что они действительно поднялись.
            Console.WriteLine();
            Console.WriteLine("  Проверка:");
            foreach (var app in service.Survey())
                Console.WriteLine($"    работает: {app.Title}");

            Console.WriteLine();
            return 0;
        }

        // -------------------------------------------------------------------- пауза
        Console.WriteLine("  ПАУЗА ФОНОВЫХ ПРОГРАММ");
        Console.WriteLine("  " + new string('-', 70));
        Console.WriteLine();

        var candidates = service.Survey();

        if (candidates.Count == 0)
        {
            Console.WriteLine("  Из списка кандидатов сейчас ничего не запущено — ставить на паузу нечего.");
            Console.WriteLine("  Это значит, что фоновые программы игре не мешают.");
            Console.WriteLine();
            return 0;
        }

        Console.WriteLine("  Найдено работающих программ из списка кандидатов:");
        Console.WriteLine();
        Console.WriteLine("    " + "Программа".PadRight(28) + "Проц.".PadRight(7) + "ОЗУ".PadRight(10) + "Причина");
        Console.WriteLine("    " + new string('-', 68));

        foreach (var app in candidates)
        {
            var memory = app.MemoryMb >= 1 ? $"{app.MemoryMb:0} МБ" : "< 1 МБ";
            Console.WriteLine($"    {app.Title.PadRight(28)}{app.ProcessCount.ToString().PadRight(7)}" +
                              $"{memory.PadRight(10)}{app.Reason}");
        }

        Console.WriteLine();
        var totalMb = candidates.Sum(c => c.MemoryMb);
        Console.WriteLine($"  Итого: {candidates.Count} программ, {totalMb:0} МБ памяти.");

        var withConnections = candidates.Where(c => c.OpenConnections > 0).ToList();
        if (withConnections.Count > 0)
        {
            Console.WriteLine();
            Console.WriteLine("  Важно про сеть: постоянные соединения держат " +
                              string.Join(", ", withConnections.Select(c => c.Title)) + ".");
            Console.WriteLine("  Именно они создают фоновый сетевой шум во время матча.");
        }

        Console.WriteLine();
        Console.WriteLine("  РАБОЧИЕ И СИСТЕМНЫЕ ПРОГРАММЫ НЕ ТРОГАЕМ — они защищены списком.");
        Console.WriteLine();

        if (!assumeYes)
        {
            Console.Write("  Остановить перечисленные программы? (y/n): ");
            var answer = Console.ReadLine()?.Trim().ToLowerInvariant();
            if (answer is not ("y" or "yes" or "д" or "да"))
            {
                Console.WriteLine("  Отменено, ничего не изменено.");
                Console.WriteLine();
                return 0;
            }
        }

        Console.WriteLine();
        var state2 = service.Pause(candidates.Select(c => c.ProcessName), context);

        Console.WriteLine();
        Console.WriteLine($"  Остановлено программ: {state2.Apps.Count}");
        Console.WriteLine($"  Состояние сохранено: {service.StatePathText}");
        Console.WriteLine();
        Console.WriteLine("  Запускайте CS2. После игры верните всё:  cs2latency --resume");
        Console.WriteLine();

        return 0;
    }
}

/// <summary>
/// История замеров: сколько раз запускали, что нашли и что изменилось.
/// Всё это лежит в файле на компьютере пользователя и никуда не отправляется.
/// </summary>
/// <summary>
/// Журнал изменений: что программа поменяла, когда и на какое значение.
/// Видно не только путь к файлу, но и содержимое — иначе непонятно, что откатывать.
/// </summary>
internal static class JournalCommand
{
    public static int Run(bool show, int revertNumber)
    {
        var journal = new UndoJournal();

        Console.WriteLine();
        Console.WriteLine("  ЖУРНАЛ ИЗМЕНЕНИЙ");
        Console.WriteLine("  " + new string('-', 70));
        Console.WriteLine("  Файл: " + journal.JournalPathText());
        Console.WriteLine();

        var entries = journal.Entries;

        if (entries.Count == 0)
        {
            Console.WriteLine("  Журнал пуст: программа ещё ничего не меняла в системе.");
            Console.WriteLine();
            return 0;
        }

        for (var i = 0; i < entries.Count; i++)
        {
            var e = entries[i];
            Console.WriteLine($"  {i + 1}. {e.Title}");
            Console.WriteLine($"     было: {Show(e.OldValue)}   стало: {Show(e.NewValue)}");
            Console.WriteLine($"     изменено: {e.AppliedAt:dd.MM.yyyy HH:mm}   ({e.FixId})");
            Console.WriteLine();
        }

        if (!show) return 0;

        // Откат одной записи: человек указал номер из списка выше.
        if (revertNumber <= 0)
        {
            Console.WriteLine("  Вернуть одну запись:  cs2latency --revert-one N");
            Console.WriteLine("  Вернуть всё:          cs2latency --revert");
            return 0;
        }

        if (revertNumber > entries.Count)
        {
            Console.WriteLine($"  Записи с номером {revertNumber} нет: в журнале {entries.Count}.");
            return 1;
        }

        if (!HostInfo.IsAdministrator())
        {
            Console.WriteLine("  Нужны права администратора: возврат меняет системные настройки.");
            return 1;
        }

        var target = entries[revertNumber - 1];
        var context = new DiagnosticContext { IsAdministrator = true };
        var report = FixRunner.RevertOne(context, journal, target);

        Console.WriteLine();
        Console.WriteLine("  ВОЗВРАТ ОДНОЙ ЗАПИСИ");
        Console.WriteLine("  " + new string('-', 70));

        foreach (var r in report.Results)
        {
            var mark = r.Outcome switch
            {
                FixOutcome.Applied => "✓",
                FixOutcome.AlreadyOk => "•",
                FixOutcome.Skipped => "—",
                _ => "✗"
            };

            Console.WriteLine($"    {mark} {r.Title}: {r.Message}");
        }

        Console.WriteLine();
        Console.WriteLine($"  {report.Summary}");

        return report.FailedCount > 0 ? 3 : 0;
    }

    /// <summary>Пустое значение показываем словами: пустая строка в отчёте непонятна.</summary>
    private static string Show(string value) =>
        string.IsNullOrEmpty(value) ? "(не было)" : value;
}
internal static class HistoryCommand
{
    /// <summary>
    /// Удалить историю замеров. Спрашиваем подтверждение: файл небольшой,
    /// но восстановить его нельзя, а человек мог нажать ключ по ошибке.
    /// </summary>
    public static int Clear(bool assumeYes)
    {
        var store = new HistoryStore();

        Console.WriteLine();
        Console.WriteLine("  УДАЛЕНИЕ ИСТОРИИ ЗАМЕРОВ");
        Console.WriteLine("  " + new string('-', 70));
        Console.WriteLine("  Файл: " + store.FilePathText);
        Console.WriteLine();

        var summary = store.Summarize();

        if (!summary.HasHistory)
        {
            Console.WriteLine("  История и так пуста — удалять нечего.");
            return 0;
        }

        Console.WriteLine($"  Будет удалено замеров: {summary.TotalRuns}");
        Console.WriteLine($"  Период: {summary.FirstRun:dd.MM.yyyy} — {summary.LastRun:dd.MM.yyyy}");
        Console.WriteLine();

        if (!assumeYes)
        {
            Console.Write("  Удалить историю? Восстановить её будет нельзя. (y/n): ");
            var answer = Console.ReadLine()?.Trim().ToLowerInvariant();
            if (answer is not ("y" or "yes" or "д" or "да"))
            {
                Console.WriteLine("  Отменено, история не тронута.");
                return 0;
            }
        }

        if (store.Clear())
        {
            Console.WriteLine("  История удалена.");
            return 0;
        }

        Console.Error.WriteLine("  Не удалось удалить файл: возможно, он занят другой программой.");
        return 4;
    }

    public static int Run()
    {
        var store = new HistoryStore();
        var summary = store.Summarize();
        var all = store.Read();

        Console.WriteLine();
        Console.WriteLine("  ИСТОРИЯ ЗАМЕРОВ (хранится только на этом компьютере)");
        Console.WriteLine("  " + new string('-', 70));
        Console.WriteLine("  Файл: " + store.FilePathText);
        Console.WriteLine();

        if (!summary.HasHistory)
        {
            Console.WriteLine("  История пуста. Запустите диагностику без параметров — замер запишется.");
            Console.WriteLine();
            return 0;
        }

        Console.WriteLine($"  Всего замеров: {summary.TotalRuns}");
        Console.WriteLine($"  Первый: {summary.FirstRun:dd.MM.yyyy HH:mm}   Последний: {summary.LastRun:dd.MM.yyyy HH:mm}");
        Console.WriteLine($"  Период: {(summary.DaysTracked <= 0 ? "меньше дня" : summary.DaysTracked + " дн.")}");
        Console.WriteLine($"  Замеров с найденными проблемами: {summary.RunsWithProblems}");
        Console.WriteLine();

        if (summary.Improved.Count > 0 || summary.Worsened.Count > 0)
        {
            Console.WriteLine("  ИЗМЕНЕНИЯ МЕЖДУ ПЕРВЫМ И ПОСЛЕДНИМ ЗАМЕРОМ");
            Console.WriteLine("  " + new string('-', 70));

            foreach (var trend in summary.Improved)
            {
                Console.ForegroundColor = ConsoleColor.Green;
                Console.WriteLine($"    улучшилось  {trend.Title}: {MetricName(trend.MetricName)} " +
                                  $"{trend.FirstValue:0.#} -> {trend.LastValue:0.#}");
                Console.ResetColor();
            }

            foreach (var trend in summary.Worsened)
            {
                Console.ForegroundColor = ConsoleColor.Red;
                Console.WriteLine($"    ухудшилось  {trend.Title}: {MetricName(trend.MetricName)} " +
                                  $"{trend.FirstValue:0.#} -> {trend.LastValue:0.#}");
                Console.ResetColor();
            }

            Console.WriteLine();
        }
        else
        {
            Console.WriteLine("  Существенных изменений между замерами не зафиксировано.");
            Console.WriteLine();
        }

        Console.WriteLine("  ПОСЛЕДНИЕ ЗАМЕРЫ");
        Console.WriteLine("  " + new string('-', 70));

        foreach (var snapshot in all.TakeLast(10).Reverse())
        {
            var mark = snapshot.ProblemCount > 0 ? "!" : " ";
            Console.WriteLine($"   {mark} {snapshot.Timestamp:dd.MM.yyyy HH:mm}  {snapshot.Summary}");
        }

        Console.WriteLine();
        Console.WriteLine("  Удалить историю: удалите файл, указанный выше.");
        Console.WriteLine();

        return 0;
    }

    private static string MetricName(string metric) => metric switch
    {
        "spike_percent" => "всплески, %",
        "loss_percent" => "потери, %",
        "stddev_ms" => "разброс, мс",
        "max_ms" => "максимум, мс",
        _ => metric
    };
}

/// <summary>
/// Самопроверка логики оценки на записанных замерах.
/// Нужна потому, что сетевые замеры доступны не на всякой машине (ICMP часто блокируется),
/// а правила оценки должны быть верными всегда.
/// </summary>
internal static class SelfTestCommand
{    public static int Run()
    {
        Console.WriteLine();
        Console.WriteLine("  САМОПРОВЕРКА логики оценки задержки (обращений к сети нет)");
        Console.WriteLine("  " + new string('─', 70));

        var results = Cs2LatencyDoctor.Core.Checks.SelfTests.Run();
        var passed = 0;

        foreach (var (test, actual, ok) in results)
        {
            if (ok) passed++;

            Console.ForegroundColor = ok ? ConsoleColor.Green : ConsoleColor.Red;
            Console.Write(ok ? "  [ПРОЙДЕНО] " : "  [ПРОВАЛ]   ");
            Console.ResetColor();
            Console.WriteLine(test.Name);

            Console.WriteLine($"               ожидалось: {test.Expected}, получено: {actual.Severity}");
            Console.WriteLine($"               данные: {test.DataSource}");
            Console.WriteLine($"               вердикт: {actual.Detail}");

            if (!ok)
            {
                Console.ForegroundColor = ConsoleColor.Red;
                Console.WriteLine($"               требование: {test.Expectation}");
                Console.ResetColor();
            }

            Console.WriteLine();
        }

        Console.WriteLine("  " + new string('─', 70));
        var allOk = passed == results.Count;

        Console.ForegroundColor = allOk ? ConsoleColor.Green : ConsoleColor.Red;
        Console.WriteLine($"  ИТОГ: {passed} из {results.Count} сценариев пройдено");
        Console.ResetColor();

        if (!allOk)
        {
            Console.WriteLine("  Логика оценки работает неверно — исправлять до выпуска.");
        }

        Console.WriteLine();
        return allOk ? 0 : 1;
    }
}
