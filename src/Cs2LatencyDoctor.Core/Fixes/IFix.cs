namespace Cs2LatencyDoctor.Core.Fixes;

/// <summary>Что произошло при попытке применить исправление.</summary>
public enum FixOutcome
{
    /// <summary>Применено и проверено.</summary>
    Applied = 0,

    /// <summary>Уже было настроено правильно — менять нечего.</summary>
    AlreadyOk = 1,

    /// <summary>Применить нельзя: нет прав, не найдено железо, условие не выполнено.</summary>
    Skipped = 2,

    /// <summary>Ошибка при применении. Проверь журнал — часть правок могла пройти.</summary>
    Failed = 3
}

/// <summary>Результат применения одного исправления.</summary>
public sealed record FixResult(
    string FixId,
    string Title,
    FixOutcome Outcome,
    string Message,
    IReadOnlyList<JournalEntry> Changes)
{
    public static FixResult Applied(string id, string title, string message, IReadOnlyList<JournalEntry> changes) =>
        new(id, title, FixOutcome.Applied, message, changes);

    public static FixResult AlreadyOk(string id, string title, string message) =>
        new(id, title, FixOutcome.AlreadyOk, message, Array.Empty<JournalEntry>());

    public static FixResult Skipped(string id, string title, string message) =>
        new(id, title, FixOutcome.Skipped, message, Array.Empty<JournalEntry>());

    public static FixResult Failed(string id, string title, string message) =>
        new(id, title, FixOutcome.Failed, message, Array.Empty<JournalEntry>());
}

/// <summary>
/// Одно исправление. Обязательный контракт: сначала прочитать текущее значение,
/// сохранить его в журнал, только потом менять. Иначе откат невозможен.
/// </summary>
public interface IFix
{
    string Id { get; }
    string Title { get; }

    /// <summary>Можно ли вообще применять на этой машине (права, наличие железа/игры).</summary>
    bool CanApply(DiagnosticContext context);

    /// <summary>Почему нельзя — текст для пользователя.</summary>
    string? SkipReason(DiagnosticContext context);

    /// <summary>Применить. Возвращает результат и список изменений для отката.</summary>
    FixResult Apply(DiagnosticContext context, UndoJournal journal);

    /// <summary>Вернуть конкретное изменение назад.</summary>
    bool Revert(JournalEntry entry, DiagnosticContext context);
}

/// <summary>
/// Базовая реализация с общими проверками. Наследники описывают только суть правки.
/// </summary>
public abstract class FixBase : IFix
{
    public abstract string Id { get; }
    public abstract string Title { get; }

    public virtual bool RequiresAdmin => true;

    public virtual bool CanApply(DiagnosticContext context) =>
        !RequiresAdmin || context.IsAdministrator;

    public virtual string? SkipReason(DiagnosticContext context) =>
        CanApply(context) ? null : "Нужны права администратора";

    public abstract FixResult Apply(DiagnosticContext context, UndoJournal journal);

    public abstract bool Revert(JournalEntry entry, DiagnosticContext context);

    /// <summary>Проверка, что значение действительно записалось.</summary>
    protected static bool Verify(Func<string?> read, string expected)
    {
        var actual = read();
        return string.Equals(actual, expected, StringComparison.OrdinalIgnoreCase);
    }
}
