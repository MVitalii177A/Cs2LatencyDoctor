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
/// Что именно исправлять: исправление целиком либо один его подпункт.
/// Задаётся явно, потому что по одной строке идентификатора это не угадать:
/// «scheduler.mmcss» — исправление, а «scheduler.mmcss.Priority» — его параметр,
/// и перепутать их означает молча ничего не сделать.
/// </summary>
/// <param name="FixId">Исправление: совпадает с IFix.Id.</param>
/// <param name="SubActionId">Один параметр внутри исправления. null — исправление целиком.</param>
public sealed record FixTarget(string FixId, string? SubActionId = null)
{
    /// <summary>Разобрать запись плана, пришедшую от находки.</summary>
    public static FixTarget From(FixAction action) => new(action.Id, action.SubActionId);
}

/// <summary>
/// Что именно применить. Собирается из находок диагностики: программа должна
/// делать ровно то, что нашла, а не «всё, что умеет».
///
/// Здесь важно различать два похожих случая, и раньше они были свалены в один:
///   * проверка нашла проблемы, но исправлять нечего — применять НЕ надо;
///   * диагностика вообще не запускалась — тогда применяем всё, что умеем.
/// Если эти случаи не различать, кнопка «Применить исправления» без предварительной
/// проверки начнёт менять всё подряд, хотя находок нет.
/// </summary>
public sealed class FixPlan
{
    private readonly HashSet<string> _actionIds = new(StringComparer.OrdinalIgnoreCase);
    private readonly HashSet<string> _subActionIds = new(StringComparer.OrdinalIgnoreCase);

    /// <summary>
    /// Исправления, добавленные ЦЕЛИКОМ. Хранится отдельно от подпунктов, потому что
    /// «поменяй всё в этом исправлении» и «поменяй ровно этот параметр» — разные вещи,
    /// и по одному идентификатору их не различить.
    /// </summary>
    private readonly HashSet<string> _wholeFixes = new(StringComparer.OrdinalIgnoreCase);

    /// <summary>План собран из находок. false означает «диагностика не выполнялась».</summary>
    public bool BuiltFromFindings { get; private init; }

    /// <summary>
    /// Применять всё, что программа умеет. Используется только когда находок нет
    /// потому, что диагностика не запускалась.
    /// </summary>
    public static FixPlan Everything { get; } = new() { BuiltFromFindings = false };

    /// <summary>Собрать план из находок: берём только то, что программа правда может сделать.</summary>
    public static FixPlan FromFindings(IEnumerable<CheckResult> findings)
    {
        var plan = new FixPlan { BuiltFromFindings = true };

        foreach (var finding in findings)
        {
            foreach (var action in finding.ApplicableFixes)
            {
                plan.Add(FixTarget.From(action));
            }
        }

        return plan;
    }

    /// <summary>Применять указанное. Нужно для проверок и ручного выбора в интерфейсе.</summary>
    public static FixPlan For(params FixTarget[] targets)
    {
        var plan = new FixPlan { BuiltFromFindings = true };
        foreach (var target in targets) plan.Add(target);

        return plan;
    }

    /// <summary>Добавить в план исправление целиком либо один его параметр.</summary>
    private void Add(FixTarget target)
    {
        _actionIds.Add(target.FixId);

        if (string.IsNullOrWhiteSpace(target.SubActionId))
        {
            _wholeFixes.Add(target.FixId);
            return;
        }

        _subActionIds.Add(target.SubActionId);
    }

    /// <summary>Есть ли в плане хоть что-то выбранное.</summary>
    public bool IsEmpty => _actionIds.Count == 0 && _subActionIds.Count == 0;

    /// <summary>
    /// Нужно ли применять это исправление.
    ///
    /// Если план собран из находок, а исправления в нём нет — не применяем: значит
    /// проверка его не просила. Если диагностика не выполнялась, применяем всё.
    /// </summary>
    public bool WantsFix(string fixId) =>
        BuiltFromFindings
            ? _actionIds.Contains(fixId) || _subActionIds.Contains(fixId)
            : true;

    /// <summary>
    /// Нужно ли менять этот подпункт внутри исправления.
    ///
    /// Если исправление добавлено целиком — меняем все его параметры.
    /// Если указаны только отдельные параметры — меняем ровно их.
    /// </summary>
    public bool WantsSubAction(string fixId, string subActionId)
    {
        if (!WantsFix(fixId)) return false;

        // Диагностика не выполнялась: применяем всё, что исправление умеет.
        if (!BuiltFromFindings) return true;

        if (_wholeFixes.Contains(fixId)) return true;

        return _subActionIds.Contains(subActionId);
    }

    /// <summary>Человеческое описание плана для отчёта.</summary>
    public string Describe()
    {
        if (!BuiltFromFindings) return "всё, что программа умеет";
        if (IsEmpty) return "нечего: по результатам проверки всё уже настроено правильно";

        return string.Join(", ", _actionIds.OrderBy(id => id, StringComparer.OrdinalIgnoreCase));
    }
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

    /// <summary>
    /// Применить выбранное из плана. Возвращает результат и список изменений для отката.
    /// План нужен, чтобы менять ровно то, что нашла проверка: иначе кнопка применяет
    /// весь набор исправления, даже если проверка указала на один параметр.
    /// </summary>
    FixResult Apply(DiagnosticContext context, UndoJournal journal, FixPlan plan);

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

    public abstract FixResult Apply(DiagnosticContext context, UndoJournal journal, FixPlan plan);

    public abstract bool Revert(JournalEntry entry, DiagnosticContext context);

    /// <summary>Проверка, что значение действительно записалось.</summary>
    protected static bool Verify(Func<string?> read, string expected)
    {
        var actual = read();
        return string.Equals(actual, expected, StringComparison.OrdinalIgnoreCase);
    }
}
