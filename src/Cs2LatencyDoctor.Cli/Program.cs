using System.Text;
using System.Text.Json;
using Cs2LatencyDoctor.Core;
using Cs2LatencyDoctor.Core.Checks;

// Регистрируем старые кодировки: нужны для чтения вывода ping.exe и powercfg.
AppBootstrap.Initialize();

Console.OutputEncoding = Encoding.UTF8;
Console.InputEncoding = Encoding.UTF8;

// ---------------------------------------------------------------- аргументы
var probeSeconds = 20;
var jsonMode = false;
var showHelp = false;
var selfTest = false;

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

if (showHelp)
{
    Console.WriteLine("""
        Cs2LatencyDoctor — диагностика причин задержек в CS2.

        Использование:
          Cs2LatencyDoctor.Cli [--seconds N] [--json]

        Параметры:
          --seconds N   длительность замера сети в секундах (5..120, по умолчанию 20)
          --json        вывести отчёт в формате JSON
          --selftest    проверить логику оценки на записанных данных (без обращений к сети)
          --help        эта справка

        Программа ничего не меняет. Только читает состояние и показывает находки.
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
/// Самопроверка логики оценки на записанных замерах.
/// Нужна потому, что сетевые замеры доступны не на всякой машине (ICMP часто блокируется),
/// а правила оценки должны быть верными всегда.
/// </summary>
internal static class SelfTestCommand
{
    public static int Run()
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
