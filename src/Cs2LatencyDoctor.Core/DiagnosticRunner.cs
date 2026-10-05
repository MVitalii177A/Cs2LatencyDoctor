using Cs2LatencyDoctor.Core.Checks;

namespace Cs2LatencyDoctor.Core;

/// <summary>Итог полной диагностики.</summary>
public sealed class DiagnosticReport
{
    public required IReadOnlyList<CheckResult> Results { get; init; }
    public required TimeSpan Duration { get; init; }
    public DateTimeOffset StartedAt { get; init; } = DateTimeOffset.Now;

    public int Count(Severity severity) => Results.Count(r => r.Severity == severity);

    public Severity Worst =>
        Results.Count == 0 ? Severity.Ok : Results.Max(r => r.Severity);

    /// <summary>Короткая сводка для заголовка интерфейса.</summary>
    public string Summary
    {
        get
        {
            var problems = Count(Severity.Problem);
            var warnings = Count(Severity.Warning);
            var ok = Count(Severity.Ok);

            if (problems > 0) return $"Найдено проблем: {problems}, предупреждений: {warnings}";
            if (warnings > 0) return $"Предупреждений: {warnings}, в порядке: {ok}";
            return $"Всё в порядке ({ok} проверок)";
        }
    }
}

/// <summary>Запускает все проверки и собирает отчёт.</summary>
public sealed class DiagnosticRunner
{
    private readonly List<IDiagnosticCheck> _checks = new();

    public DiagnosticRunner Add(IDiagnosticCheck check)
    {
        _checks.Add(check);
        return this;
    }

    /// <summary>Стандартный набор проверок.</summary>
    public static DiagnosticRunner CreateDefault(int probeSeconds = 20)
    {
        var path = NetworkPathResolver.Resolve();

        return new DiagnosticRunner()
            .Add(new NetworkLatencyCheck(path) { ExternalTarget = "1.1.1.1" })
            .Add(new NetworkAdapterCheck())
            .Add(new PowerCheck())
            .Add(new SchedulerCheck())
            .Add(new Cs2ConfigCheck());
    }

    public async Task<DiagnosticReport> RunAsync(DiagnosticContext context, CancellationToken ct = default)
    {
        var started = DateTimeOffset.Now;
        var results = new List<CheckResult>();

        foreach (var check in _checks)
        {
            ct.ThrowIfCancellationRequested();

            if (check.RequiresAdmin && !context.IsAdministrator)
            {
                results.Add(CheckResult.Skipped(check.Id, check.Title,
                    "Нужны права администратора"));
                continue;
            }

            try
            {
                var produced = await check.RunAsync(context, ct);
                results.AddRange(produced);
            }
            catch (OperationCanceledException)
            {
                throw;
            }
            catch (Exception ex)
            {
                results.Add(CheckResult.Skipped(check.Id, check.Title,
                    "Проверка не выполнена: " + ex.Message));
            }
        }

        // Сортируем: сначала проблемы, потом предупреждения, потом остальное.
        var ordered = results
            .OrderByDescending(r => r.Severity == Severity.Problem)
            .ThenByDescending(r => r.Severity == Severity.Warning)
            .ThenByDescending(r => r.Severity == Severity.Info)
            .ThenBy(r => r.Title)
            .ToList();

        return new DiagnosticReport
        {
            Results = ordered,
            Duration = DateTimeOffset.Now - started,
            StartedAt = started
        };
    }
}
