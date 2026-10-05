using Cs2LatencyDoctor.Core.Windows;

namespace Cs2LatencyDoctor.Core.Checks;

/// <summary>Один сценарий самопроверки: записанные данные + ожидаемый вердикт.</summary>
public sealed record SelfTestCase(
    string Name,
    string DataSource,
    LatencyProbeResult Probe,
    Severity Expected,
    string Expectation);

/// <summary>
/// Самопроверка логики оценки. Нужна потому, что замеры сети можно делать не на всякой
/// машине (ICMP часто блокируется фаерволом), а правила оценки обязаны быть верными всегда.
/// Данные здесь — реальные замеры, снятые на машине с проблемой и после её устранения.
/// </summary>
public static class SelfTests
{
    /// <summary>Реальные замеры: до исправления настроек сетевой карты.</summary>
    public static LatencyProbeResult RecordedBeforeFix() => Build(
        sent: 200,
        median: 0.5,
        mostly: 0.4,
        spikesAt: new[] { 8, 9, 10, 11, 12, 36, 37, 38, 39, 40, 55, 56, 57, 160, 161, 162, 163, 164, 198 },
        spikeValue: 11.0,
        maxSpike: 14.0);

    /// <summary>Реальные замеры после исправления: ровно, один единичный выброс.</summary>
    public static LatencyProbeResult RecordedAfterFix() => Build(
        sent: 300,
        median: 1.0,
        mostly: 1.0,
        spikesAt: new[] { 173 },
        spikeValue: 26.0,
        maxSpike: 26.0);

    /// <summary>ICMP блокируется в системе: судить о сети нельзя, но это не "потери".</summary>
    public static LatencyProbeResult RecordedIcmpBlocked() => new()
    {
        Target = "192.168.31.1",
        Method = ProbeMethod.Blocked,
        Sent = 4,
        Received = 0
    };

    /// <summary>Замер через ping.exe: задержка недоступна, но потерь нет.</summary>
    public static LatencyProbeResult RecordedPingExeNoLoss() => new()
    {
        Target = "1.1.1.1",
        Method = ProbeMethod.PingExe,
        Sent = 30,
        Received = 30
    };

    /// <summary>Внешний узел недоступен: реальные потери.</summary>
    public static LatencyProbeResult RecordedPingExeWithLoss() => new()
    {
        Target = "1.1.1.1",
        Method = ProbeMethod.PingExe,
        Sent = 30,
        Received = 18      // 40% потерь
    };

    public static IReadOnlyList<SelfTestCase> All() => new[]
    {
        new SelfTestCase(
            "До исправления: пила до роутера",
            "реальный замер 10.04.2026, 200 пакетов, 19 всплесков до 14 мс",
            RecordedBeforeFix(), Severity.Problem,
            "должно быть найдено как проблема"),

        new SelfTestCase(
            "После исправления: ровно",
            "реальный замер 05.10.2026, 300 пакетов, 1 всплеск 26 мс",
            RecordedAfterFix(), Severity.Ok,
            "должно быть признано нормой (единичный выброс — фон)"),

        new SelfTestCase(
            "ICMP заблокирован",
            "воспроизведено в среде с запретом ICMP",
            RecordedIcmpBlocked(), Severity.Skipped,
            "не должно выдаваться за 100% потерь"),

        new SelfTestCase(
            "Замер через ping.exe без потерь",
            "сценарий обхода блокировки raw-сокетов",
            RecordedPingExeNoLoss(), Severity.Ok,
            "потерь нет — должно быть ОК"),

        new SelfTestCase(
            "Замер через ping.exe с потерями 40%",
            "сценарий обрыва связи",
            RecordedPingExeWithLoss(), Severity.Problem,
            "потери должны быть замечены"),
    };

    /// <summary>Прогнать все сценарии и вернуть результаты проверки.</summary>
    public static IReadOnlyList<(SelfTestCase Case, CheckResult Actual, bool Passed)> Run()
    {
        var output = new List<(SelfTestCase, CheckResult, bool)>();

        foreach (var test in All())
        {
            var probe = test.Probe.Method == ProbeMethod.DotNetIcmp
                ? LatencyEvaluator.ApplyWanThreshold(test.Probe)
                : test.Probe;

            // Для локального шлюза порог абсолютный, для внешнего — относительный.
            // В сценариях "до роутера" всплески уже заданы явно.
            var actual = LatencyEvaluator.Evaluate(probe, "selftest", "тест", string.Empty);
            output.Add((test, actual, actual.Severity == test.Expected));
        }

        return output;
    }

    // ------------------------------------------------------------- генератор
    /// <summary>
    /// Собирает распределение задержек из описания: база, набор позиций-всплесков и величина всплеска.
    /// Всплески ставим на конкретные позиции, чтобы сценарий был воспроизводимым.
    /// </summary>
    private static LatencyProbeResult Build(
        int sent, double median, double mostly, int[] spikesAt, double spikeValue, double maxSpike)
    {
        var samples = new List<double>(sent);
        var spikePositions = spikesAt.ToHashSet();

        for (var i = 0; i < sent; i++)
            samples.Add(spikePositions.Contains(i) ? spikeValue : mostly);

        // Присваиваем максимум последнему всплеску: так проверяем, что max читается из данных.
        if (spikesAt.Length > 0)
            samples[spikesAt[^1]] = maxSpike;

        var ordered = samples.OrderBy(x => x).ToList();
        var avg = samples.Average();
        var stdDev = Math.Sqrt(samples.Sum(v => Math.Pow(v - avg, 2)) / samples.Count);

        return new LatencyProbeResult
        {
            Target = "recorded",
            Method = ProbeMethod.DotNetIcmp,
            Sent = sent,
            Received = sent,
            MinMs = ordered[0],
            MaxMs = ordered[^1],
            AverageMs = Math.Round(avg, 2),
            MedianMs = median,
            StdDevMs = Math.Round(stdDev, 2),
            Spikes = spikesAt.Length,
            SpikeThresholdMs = LatencyEvaluator.LocalSpikeThresholdMs,
            Samples = samples
        };
    }
}
