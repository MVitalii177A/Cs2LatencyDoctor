namespace Cs2LatencyDoctor.Core;

/// <summary>Насколько всё плохо по итогам конкретной проверки.</summary>
public enum Severity
{
    /// <summary>Всё в порядке, вмешательство не требуется.</summary>
    Ok = 0,

    /// <summary>Не идеально, но на игру влияет слабо. Можно улучшить.</summary>
    Info = 1,

    /// <summary>Реальная причина задержек. Чинить.</summary>
    Warning = 2,

    /// <summary>Критично: сломано или настроено так, что играть больно.</summary>
    Problem = 3,

    /// <summary>Проверить нельзя (нет прав, нет железа, нет игры). Это не ошибка пользователя.</summary>
    Skipped = 4
}

/// <summary>Что можно сделать с найденной проблемой.</summary>
public enum FixRisk
{
    /// <summary>Обратимо, безопасно, не ломает ничего. Применяем по кнопке.</summary>
    Safe = 0,

    /// <summary>Обратимо, но что-то перестанет работать (например WSL при отключении гипервизора).</summary>
    Tradeoff = 1,

    /// <summary>Автоматически применять нельзя. Показываем инструкцию для рук.</summary>
    ManualOnly = 2
}

/// <summary>Одно исправление, которое программа умеет делать.</summary>
/// <param name="Id">Стабильный идентификатор для логов и откатов (например net.interrupt-moderation).</param>
/// <param name="Title">Что делаем, человеческим языком.</param>
/// <param name="Risk">Можно ли применять автоматически.</param>
/// <param name="Note">Чем придётся заплатить или почему это ручная операция.</param>
public sealed record FixAction(
    string Id,
    string Title,
    FixRisk Risk,
    string? Note = null);

/// <summary>Почему программа не может помочь в этом случае. Нужно, чтобы «не могу» было объяснено, а не молчало.</summary>
public enum NoHelpReason
{
    /// <summary>Программа может помочь — причина не указана.</summary>
    None = 0,

    /// <summary>Аппаратное обеспечение не поддерживает нужную настройку (например, драйвер другого производителя).</summary>
    HardwareNotSupported = 1,

    /// <summary>Система запрещает доступ: фаервол, антивирус, политика безопасности.</summary>
    BlockedBySystem = 2,

    /// <summary>Настройка закрыта производителем: правится только его утилитой или панелью драйвера.</summary>
    VendorLocked = 3,

    /// <summary>Причина вне компьютера: провайдер, маршрут, сервер игры. Настройками Windows не лечится.</summary>
    OutsideThisPc = 4,

    /// <summary>Требуется физическое действие: кабель, замена железа, перезагрузка.</summary>
    NeedsPhysicalAction = 5,

    /// <summary>Не хватает данных: игра не запускалась, файл ещё не создан.</summary>
    NotEnoughData = 6,

    /// <summary>Не хватает прав администратора.</summary>
    NeedsAdmin = 7
}

/// <summary>Результат одной проверки. Программа не говорит "всё плохо" — она говорит что именно и почему.</summary>
public sealed class CheckResult
{
    public required string Id { get; init; }
    public required string Title { get; init; }
    public required Severity Severity { get; init; }

    /// <summary>Что нашли, с цифрами. Без цифр это не диагноз, а мнение.</summary>
    public required string Detail { get; init; }

    /// <summary>Почему это влияет на игру. Нужно, чтобы пользователь понимал, а не верил.</summary>
    public string? Why { get; init; }

    /// <summary>
    /// Что делать, если программа сама помочь не может.
    ///
    /// Правило проекта: молчаливого «пропущено» быть не должно. Если мы не можем
    /// исправить — говорим, почему именно, и что человеку сделать руками.
    /// </summary>
    public string? Recommendation { get; init; }

    /// <summary>Почему помочь не получилось. None — программа справляется сама.</summary>
    public NoHelpReason NoHelpReason { get; init; } = NoHelpReason.None;

    public IReadOnlyList<FixAction> Fixes { get; init; } = Array.Empty<FixAction>();

    /// <summary>Числовые метрики для отчёта "до/после".</summary>
    public IReadOnlyDictionary<string, double> Metrics { get; init; } =
        new Dictionary<string, double>();

    /// <summary>Есть ли у человека ручное действие помимо того, что программа делает сама.</summary>
    public bool HasRecommendation => !string.IsNullOrWhiteSpace(Recommendation);

    /// <summary>Может ли программа исправить это сама.</summary>
    public bool CanFixItself => Fixes.Any(f => f.Risk != FixRisk.ManualOnly);

    public static CheckResult Ok(string id, string title, string detail, string? why = null,
        string? recommendation = null) =>
        new()
        {
            Id = id, Title = title, Severity = Severity.Ok, Detail = detail, Why = why,
            Recommendation = recommendation
        };

    public static CheckResult Info(string id, string title, string detail, string? why = null,
        IReadOnlyList<FixAction>? fixes = null, string? recommendation = null) =>
        new()
        {
            Id = id, Title = title, Severity = Severity.Info, Detail = detail, Why = why,
            Fixes = fixes ?? Array.Empty<FixAction>(), Recommendation = recommendation
        };

    public static CheckResult Warn(string id, string title, string detail, string? why = null,
        IReadOnlyList<FixAction>? fixes = null, string? recommendation = null) =>
        new()
        {
            Id = id, Title = title, Severity = Severity.Warning, Detail = detail, Why = why,
            Fixes = fixes ?? Array.Empty<FixAction>(), Recommendation = recommendation
        };

    public static CheckResult Problem(string id, string title, string detail, string? why = null,
        IReadOnlyList<FixAction>? fixes = null, string? recommendation = null) =>
        new()
        {
            Id = id, Title = title, Severity = Severity.Problem, Detail = detail, Why = why,
            Fixes = fixes ?? Array.Empty<FixAction>(), Recommendation = recommendation
        };

    /// <summary>
    /// Проверка не выполнена — и мы говорим, почему и что с этим делать.
    /// Без причины и рекомендации этот метод использовать не следует.
    /// </summary>
    public static CheckResult Skipped(string id, string title, string detail,
        NoHelpReason reason = NoHelpReason.NotEnoughData, string? recommendation = null) =>
        new()
        {
            Id = id, Title = title, Severity = Severity.Skipped, Detail = detail,
            NoHelpReason = reason, Recommendation = recommendation
        };
}
