using System.Text;
using System.Text.Json;
using Cs2LatencyDoctor.Core;
using Cs2LatencyDoctor.Core.Checks;
using Cs2LatencyDoctor.Core.Fixes;

// Регистрируем старые кодировки: нужны для чтения вывода ping.exe и powercfg.
AppBootstrap.Initialize();

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
          cs2latency [--seconds N] [--json]      только диагностика, ничего не меняет
          cs2latency --plan                      что можно исправить (ничего не меняет)
          cs2latency --fix                       применить безопасные исправления
          cs2latency --revert                    вернуть всё как было
          cs2latency --selftest                  проверить логику оценки на записанных данных

        Параметры:
          --seconds N   длительность замера сети в секундах (5..120, по умолчанию 20)
          --json        вывести отчёт в формате JSON

        Без параметров программа только читает состояние и показывает находки.
        При исправлении каждое изменённое значение сохраняется в журнал,
        поэтому --revert возвращает систему в исходное состояние.

        Для --fix и --revert нужны права администратора.
        """);
    return 0;
}

// ------------------------------------------------------------------ запуск
var isAdmin = HostInfo.IsAdministrator();

if (!jsonMode)
{
    Console.WriteLine();
    Console.WriteLine("  Cs2LatencyDoctor — что мешает играть в CS2");
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

// ------------------------------------------------------------------- вывод
if (jsonMode)
{
    var payload = new
    {
        startedAt = report.StartedAt,
        durationSeconds = Math.Round(report.Duration.TotalSeconds, 1),
        summary = report.Summary,
        results = report.Results.Select(r => new
        {
            id = r.Id,
            title = r.Title,
            severity = r.Severity.ToString(),
            detail = r.Detail,
            why = r.Why,
            fixes = r.Fixes.Select(f => new { id = f.Id, title = f.Title, risk = f.Risk.ToString(), note = f.Note }),
            metrics = r.Metrics
        })
    };

    Console.WriteLine(JsonSerializer.Serialize(payload, new JsonSerializerOptions { WriteIndented = true }));
    return report.Count(Severity.Problem) > 0 ? 2 : 0;
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
        var report = runner.ApplyAll(diagContext, journal);

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
                    Console.WriteLine($"             → {fix.Title}  ({risk})");
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

        Console.WriteLine("  Применить безопасные исправления:  cs2latency --fix");
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
