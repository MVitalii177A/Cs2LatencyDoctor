using Cs2LatencyDoctor.Core.Windows;

namespace Cs2LatencyDoctor.Core.Checks;

/// <summary>
/// Правила оценки задержки, вынесенные отдельно от замеров.
/// Так их можно проверить на записанных данных (режим --selftest),
/// не имея возможности делать реальные ICMP-запросы.
/// </summary>
public static class LatencyEvaluator
{
    /// <summary>Порог всплеска для локальной сети: там задержка около нуля, 3 мс — уже аномалия.</summary>
    public const double LocalSpikeThresholdMs = 3.0;

    /// <summary>Потери выше этого процента — это уже проблема, а не фон.</summary>
    public const double ProblemLossPercent = 1.0;

    /// <summary>Всплески выше этого процента — проблема.</summary>
    public const double ProblemSpikePercent = 5.0;

    /// <summary>Всплески выше этого процента — предупреждение.</summary>
    public const double WarnSpikePercent = 2.0;

    public const double ProblemStdDevMs = 5.0;
    public const double WarnStdDevMs = 2.0;

    /// <summary>
    /// Минимум замеров, при котором процент всплесков что-то значит.
    ///
    /// При коротком замере один выброс даёт 20% и превращается в «проблему» на пустом месте:
    /// на пяти запросах статистики нет. Ниже этого порога оцениваем по абсолютному числу всплесков.
    /// </summary>
    public const int MinSamplesForPercent = 30;

    /// <summary>
    /// Оценка «неровности» с учётом объёма выборки.
    ///
    /// На коротком замере проценты врут, поэтому смотрим на число всплесков:
    /// 1–2 выброса — фон, 3 и больше — уже повод разбираться.
    /// </summary>
    private static (bool IsProblem, bool IsWarning) ClassifySpikes(LatencyProbeResult probe)
    {
        if (probe.Received >= MinSamplesForPercent)
        {
            return (probe.SpikePercent > ProblemSpikePercent,
                    probe.SpikePercent > WarnSpikePercent);
        }

        return (probe.Spikes >= 3, probe.Spikes >= 2);
    }

    /// <summary>
    /// Порог всплеска для внешнего узла. Абсолютные 3 мс там бессмысленны:
    /// базовая задержка десятки мс, поэтому аномалией считаем заметный выброс над медианой.
    /// </summary>
    public static double ThresholdForWan(LatencyProbeResult probe) =>
        Math.Max(10.0, probe.MedianMs * 1.5);

    /// <summary>Пересчитать всплески для внешнего узла по относительному порогу.</summary>
    public static LatencyProbeResult ApplyWanThreshold(LatencyProbeResult probe)
    {
        var threshold = ThresholdForWan(probe);
        return probe with
        {
            Spikes = probe.Samples.Count(v => v >= threshold),
            SpikeThresholdMs = threshold
        };
    }

    /// <summary>
    /// Оценка результата замера. Главное правило: отсутствие ответов — это ещё не потери.
    /// Так же ведёт себя заблокированный ICMP, и об этом надо сказать честно.
    /// </summary>
    /// <param name="troubleAdvice">
    /// Что делать, если задержка неровная. Для роутера и для провайдера советы разные,
    /// поэтому текст передаёт вызывающая сторона.
    /// </param>
    public static CheckResult Evaluate(LatencyProbeResult probe, string id, string where, string why,
        string? troubleAdvice = null)
    {
        var title = $"Задержка {where}";

        if (probe.IsNoResponse)
        {
            if (probe.Method == ProbeMethod.PingExe)
            {
                return CheckResult.Skipped(id, title,
                    "Узел не ответил ни на один запрос — скорее всего он закрыт фаерволом",
                    NoHelpReason.BlockedBySystem,
                    "Программа не может измерить задержку: узел не отвечает на диагностические запросы. " +
                    "Это обычное поведение роутеров и серверов, а не поломка. Что делать: смотрите " +
                    "соседнюю проверку «Задержка до интернета» — там замер идёт до внешнего узла, " +
                    "который отвечает всегда. Если и она не измеряется, проверьте, не блокирует ли " +
                    "диагностические запросы ваш антивирус или фаервол.");
            }

            return CheckResult.Skipped(id, title,
                "ICMP-запросы блокируются в системе, и замена на TCP тоже не прошла",
                NoHelpReason.BlockedBySystem,
                "Программа не может измерить задержку напрямую: система не отдаёт ICMP, а TCP-подключение " +
                "к этому узлу не проходит. Что делать: " +
                "1) проверьте соседнюю проверку «Задержка до интернета» — если она показывает цифры, " +
                "сеть в порядке и переживать не о чем; " +
                "2) если не измеряется ничего, временно отключите антивирус или сторонний фаервол " +
                "и повторите проверку; " +
                "3) в фаерволе Windows разрешите эхо-запросы ICMPv4 (Правила для входящих подключений → " +
                "«Общий доступ к файлам и принтерам (эхо-запрос — ICMPv4-In)»).");
        }

        if (probe.Method == ProbeMethod.PingExe)
        {
            var lossText = $"отвечено {probe.Received} из {probe.Sent} ({probe.LossPercent:0.#}% потерь)";
            return probe.LossPercent > ProblemLossPercent
                ? CheckResult.Problem(id, title, lossText,
                    "Точную задержку измерить не удалось (система не отдаёт ICMP напрямую), " +
                    "но потери пакетов видны и они реальны. " + why)
                : CheckResult.Ok(id, title,
                    lossText + ". Точную задержку измерить не удалось, но потерь нет.", why);
        }

        // Замер через TCP-подключение: задержка настоящая, только способ другой.
        // ICMP в системе заблокирован, поэтому пошли по тому же пути, что и игровой трафик.
        if (probe.Method == ProbeMethod.TcpConnect)
        {
            var detailText =
                $"{probe.Received}/{probe.Sent} подключений · медиана {probe.MedianMs:0.#} мс · " +
                $"макс {probe.MaxMs:0.#} мс · отклонение ±{probe.StdDevMs:0.##} мс · " +
                $"всплесков {probe.Spikes} ({probe.SpikePercent:0.#}%)";
            if (!string.IsNullOrEmpty(probe.Detail)) detailText += $" · {probe.Detail}";

            var metricsTcp = new Dictionary<string, double>
            {
                ["median_ms"] = probe.MedianMs,
                ["max_ms"] = probe.MaxMs,
                ["stddev_ms"] = probe.StdDevMs,
                ["spike_percent"] = probe.SpikePercent,
                ["loss_percent"] = probe.LossPercent
            };

            var (spikeProblemTcp, spikeWarnTcp) = ClassifySpikes(probe);

            Severity severityTcp;
            if (probe.LossPercent > ProblemLossPercent) severityTcp = Severity.Problem;
            else if (spikeProblemTcp || probe.StdDevMs > ProblemStdDevMs) severityTcp = Severity.Problem;
            else if (spikeWarnTcp || probe.StdDevMs > WarnStdDevMs) severityTcp = Severity.Warning;
            else severityTcp = Severity.Ok;

            var noteTcp = "Замер сделан TCP-подключением: ICMP в системе недоступен, " +
                          "а TCP идёт тем же путём, что и игровой трафик.";

            if (severityTcp == Severity.Ok)
            {
                return new CheckResult
                {
                    Id = id, Title = title, Severity = Severity.Ok,
                    Detail = detailText + " Ровно, без всплесков. " + noteTcp,
                    Why = why, Metrics = metricsTcp
                };
            }

            var reasonTcp = probe.LossPercent > ProblemLossPercent
                ? $"Не удалось подключиться в {probe.LossPercent:0.#}% попыток — это прямые обрывы связи."
                : $"Разброс задержки ±{probe.StdDevMs:0.##} мс и {probe.Spikes} всплесков. " +
                  "Именно неровность, а не среднее значение, ощущается как «пули не регистрируются».";

            return new CheckResult
            {
                Id = id, Title = title, Severity = severityTcp,
                Detail = detailText + " — " + reasonTcp + " " + noteTcp,
                Why = why, Metrics = metricsTcp,
                Recommendation = troubleAdvice
            };
        }

        var detail =
            $"{probe.Received}/{probe.Sent} ответов · медиана {probe.MedianMs:0.#} мс · " +
            $"макс {probe.MaxMs:0.#} мс · отклонение ±{probe.StdDevMs:0.##} мс · " +
            $"всплесков {probe.Spikes} ({probe.SpikePercent:0.#}%)";

        var metrics = new Dictionary<string, double>
        {
            ["median_ms"] = probe.MedianMs,
            ["max_ms"] = probe.MaxMs,
            ["stddev_ms"] = probe.StdDevMs,
            ["spike_percent"] = probe.SpikePercent,
            ["loss_percent"] = probe.LossPercent
        };

        var (spikeProblem, spikeWarn) = ClassifySpikes(probe);

        Severity severity;
        if (probe.LossPercent > ProblemLossPercent) severity = Severity.Problem;
        else if (spikeProblem || probe.StdDevMs > ProblemStdDevMs) severity = Severity.Problem;
        else if (spikeWarn || probe.StdDevMs > WarnStdDevMs) severity = Severity.Warning;
        else severity = Severity.Ok;

        if (severity == Severity.Ok)
        {
            var extra = probe.SpikePercent > 0
                ? $" Единичные всплески ({probe.SpikePercent:0.#}%) — нормальный фон."
                : " Ровно, без всплесков.";

            return new CheckResult
            {
                Id = id, Title = title, Severity = Severity.Ok,
                Detail = detail + extra, Why = why, Metrics = metrics
            };
        }

        var reason = probe.LossPercent > ProblemLossPercent
            ? $"Потери пакетов {probe.LossPercent:0.#}% — это прямые промахи в игре."
            : $"Разброс задержки ±{probe.StdDevMs:0.##} мс и {probe.Spikes} всплесков. " +
              "Именно неровность, а не среднее значение, ощущается как «пули не регистрируются».";

        return new CheckResult
        {
            Id = id, Title = title, Severity = severity,
            Detail = detail + " — " + reason, Why = why, Metrics = metrics,
            Recommendation = troubleAdvice
        };
    }
}
