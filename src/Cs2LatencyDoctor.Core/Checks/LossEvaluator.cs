namespace Cs2LatencyDoctor.Core.Checks;

/// <summary>
/// Оценка потерь пакетов. Вынесена отдельно от замера, чтобы её можно было
/// проверить на записанных числах: сеть доступна не на всякой машине,
/// а правила оценки должны быть верными всегда.
/// </summary>
public static class LossEvaluator
{
    /// <summary>С какого процента потерь это уже проблема. Один процент в игре заметен.</summary>
    public const double ProblemLossPercent = 1.0;

    /// <summary>Меньше этого числа попыток — статистике верить нельзя.</summary>
    public const int MinimumAttempts = 10;

    /// <summary>Что означает результат замера потерь.</summary>
    public enum Outcome
    {
        /// <summary>Ни одной потери.</summary>
        Clean = 0,

        /// <summary>Единичная потеря: может быть случайностью.</summary>
        SingleLoss = 1,

        /// <summary>Потери на уровне, который в игре уже чувствуется.</summary>
        Lossy = 2,

        /// <summary>Замер не состоялся: узел не отвечает вообще.</summary>
        Inconclusive = 3
    }

    /// <summary>Оценить результат: сколько подключений не прошло из скольких попыток.</summary>
    public static Outcome Evaluate(int attempts, int failures)
    {
        // Ни одного ответа — это не «100% потерь», а «измерить не вышло».
        // Правило проекта: не выдавать отговорку за диагноз.
        if (attempts <= 0 || failures >= attempts) return Outcome.Inconclusive;

        if (failures == 0) return Outcome.Clean;

        var percent = failures * 100.0 / attempts;
        return percent >= ProblemLossPercent ? Outcome.Lossy : Outcome.SingleLoss;
    }

    /// <summary>Процент потерь, округлённый для показа.</summary>
    public static double Percent(int attempts, int failures) =>
        attempts <= 0 ? 0 : Math.Round(failures * 100.0 / attempts, 1);

    /// <summary>Сколько попыток делать при заданной длительности замера.</summary>
    public static int AttemptsFor(int probeSeconds) =>
        Math.Max(MinimumAttempts, probeSeconds);
}
