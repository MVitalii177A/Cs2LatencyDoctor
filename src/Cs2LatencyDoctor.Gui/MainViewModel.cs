using System.Collections.ObjectModel;
using System.ComponentModel;
using System.Diagnostics;
using System.Runtime.CompilerServices;
using System.Windows;
using Cs2LatencyDoctor.Core;
using Cs2LatencyDoctor.Core.Background;
using Cs2LatencyDoctor.Core.Fixes;
using Cs2LatencyDoctor.Core.History;

namespace Cs2LatencyDoctor.Gui;

/// <summary>Строка отчёта для списка в окне.</summary>
public sealed class FindingRow
{
    public required string Mark { get; init; }
    public required string Title { get; init; }
    public required string Detail { get; init; }
    public string Why { get; init; } = string.Empty;
    public string FixHint { get; init; } = string.Empty;
    public required string Color { get; init; }

    public bool HasWhy => !string.IsNullOrWhiteSpace(Why);
    public bool HasFix => !string.IsNullOrWhiteSpace(FixHint);
}

/// <summary>Строка списка фоновых программ.</summary>
public sealed class BackgroundRow
{
    public required string Title { get; init; }
    public required string Details { get; init; }
    public required string Reason { get; init; }
    public required string ProcessName { get; init; }
    public bool Selected { get; set; }
}

/// <summary>
/// Логика окна. Держит состояние отдельно от разметки, чтобы её можно было проверить.
/// Сеть здесь не используется вообще: только локальные замеры.
/// </summary>
public sealed class MainViewModel : INotifyPropertyChanged
{
    private readonly HistoryStore _history = new();
    private readonly BackgroundAppService _background = new();

    public ObservableCollection<FindingRow> Findings { get; } = new();
    public ObservableCollection<BackgroundRow> BackgroundApps { get; } = new();

    private string _status = "Готово к проверке";
    public string Status
    {
        get => _status;
        set { _status = value; OnPropertyChanged(); }
    }

    private string _summary = "Нажмите «Проверить компьютер», чтобы начать";
    public string Summary
    {
        get => _summary;
        set { _summary = value; OnPropertyChanged(); }
    }

    private string _historyText = string.Empty;
    public string HistoryText
    {
        get => _historyText;
        set { _historyText = value; OnPropertyChanged(); }
    }

    private string _adminText = string.Empty;
    public string AdminText
    {
        get => _adminText;
        set { _adminText = value; OnPropertyChanged(); }
    }

    private bool _isBusy;
    public bool IsBusy
    {
        get => _isBusy;
        set { _isBusy = value; OnPropertyChanged(); OnPropertyChanged(nameof(IsIdle)); }
    }

    public bool IsIdle => !IsBusy;

    private int _probeSeconds = 15;
    public int ProbeSeconds
    {
        get => _probeSeconds;
        set { _probeSeconds = Math.Clamp(value, 5, 60); OnPropertyChanged(); }
    }

    public bool IsAdministrator { get; private set; }

    public MainViewModel()
    {
        IsAdministrator = HostInfo.IsAdministrator();
        AdminText = IsAdministrator
            ? "Права администратора: есть — исправления доступны"
            : "Права администратора: нет — исправления будут недоступны (запустите от имени администратора)";

        RefreshHistory();
        RefreshBackground();
    }

    // ------------------------------------------------------------------ история
    public void RefreshHistory()
    {
        var summary = _history.Summarize();
        HistoryText = summary.HasHistory
            ? summary.Text
            : "История пуста: это будет первый замер. Данные остаются на этом компьютере.";
    }

    // --------------------------------------------------------------------- фон
    public void RefreshBackground()
    {
        BackgroundApps.Clear();

        foreach (var app in _background.Survey())
        {
            BackgroundApps.Add(new BackgroundRow
            {
                Title = app.Title,
                ProcessName = app.ProcessName,
                Details = $"{app.ProcessCount} проц., {app.MemoryMb:0} МБ" +
                          (app.OpenConnections > 0 ? $", соединений: {app.OpenConnections}" : string.Empty),
                Reason = app.Reason,
                Selected = app.Title.Contains("Торрент", StringComparison.OrdinalIgnoreCase)
                           || app.Title.Contains("Dropbox", StringComparison.OrdinalIgnoreCase)
            });
        }

        if (BackgroundApps.Count == 0)
        {
            BackgroundApps.Add(new BackgroundRow
            {
                Title = "Ничего лишнего не запущено",
                ProcessName = string.Empty,
                Details = string.Empty,
                Reason = "Из списка известных фоновых программ сейчас ничего не работает — игре ничего не мешает."
            });
        }
    }

    // -------------------------------------------------------------- диагностика
    public async Task RunDiagnosticsAsync()
    {
        if (IsBusy) return;

        IsBusy = true;
        Findings.Clear();
        Summary = "Идёт проверка…";

        try
        {
            var context = new DiagnosticContext
            {
                IsAdministrator = IsAdministrator,
                ProbeSeconds = ProbeSeconds,
                OnProgress = message => Status = message
            };

            var runner = DiagnosticRunner.CreateDefault(ProbeSeconds);
            var report = await runner.RunAsync(context);

            // Замер пишем в локальную историю до показа результатов:
            // так сводка «что изменилось» сравнивает с прошлыми запусками.
            var historyBefore = report.CaptureAndSummarize(_history);

            foreach (var result in report.Results)
            {
                var (mark, color) = result.Severity switch
                {
                    Severity.Problem => ("ПРОБЛЕМА", "#E5484D"),
                    Severity.Warning => ("ВНИМАНИЕ", "#F5A524"),
                    Severity.Info => ("МОЖНО ЛУЧШЕ", "#8B8D98"),
                    Severity.Ok => ("ОК", "#30A46C"),
                    _ => ("ПРОПУСК", "#6E6E77")
                };

                var fixHint = result.Fixes.Count > 0
                    ? string.Join("; ", result.Fixes.Select(f => f.Title))
                    : string.Empty;

                Findings.Add(new FindingRow
                {
                    Mark = mark,
                    Title = result.Title,
                    Detail = result.Detail,
                    Why = result.Why ?? string.Empty,
                    FixHint = fixHint,
                    Color = color
                });
            }

            Summary = report.Summary;
            Status = $"Готово за {report.Duration.TotalSeconds:0.#} с";

            if (historyBefore.HasHistory)
            {
                var changes = historyBefore.Improved.Count + historyBefore.Worsened.Count;
                HistoryText = historyBefore.Text +
                              (changes > 0 ? " Подробности: cs2latency --history" : string.Empty);
            }
            else
            {
                HistoryText = "Это первый замер — сравнить пока не с чем. Следующие запуски покажут изменения.";
            }
        }
        catch (Exception ex)
        {
            Summary = "Проверка не завершилась";
            Status = "Ошибка: " + ex.Message;
        }
        finally
        {
            IsBusy = false;
        }
    }

    // -------------------------------------------------------------- исправления
    public int FixableCount => Findings.Count(f => f.HasFix);

    public string ApplyResult { get; private set; } = string.Empty;

    public void ApplyFixes()
    {
        if (IsBusy) return;

        if (!IsAdministrator)
        {
            ApplyResult = "Нужны права администратора: закройте программу и запустите её от имени администратора.";
            OnPropertyChanged(nameof(ApplyResult));
            return;
        }

        IsBusy = true;

        try
        {
            Status = "Применяю исправления…";

            var context = new DiagnosticContext
            {
                IsAdministrator = IsAdministrator,
                OnProgress = message => Status = message
            };

            var journal = new UndoJournal();
            if (!journal.IsFileReady)
            {
                ApplyResult = "Не удалось создать журнал отката. Изменения не применялись: " +
                              "без журнала вернуть настройки назад невозможно.";
                return;
            }

            var report = FixRunner.CreateDefault(FixSelection.Safe).ApplyAll(context, journal);

            var lines = report.Results.Select(r => r.Outcome switch
            {
                FixOutcome.Applied => "✓ " + r.Title + ": " + r.Message,
                FixOutcome.AlreadyOk => "• " + r.Title + ": " + r.Message,
                FixOutcome.Skipped => "— " + r.Title + ": " + r.Message,
                _ => "✗ " + r.Title + ": " + r.Message
            });

            ApplyResult = string.Join(Environment.NewLine, lines) +
                          Environment.NewLine + Environment.NewLine +
                          "Журнал отката: " + report.JournalPathText +
                          Environment.NewLine + "Вернуть всё назад можно кнопкой «Откатить изменения».";

            Status = report.Summary;
        }
        catch (Exception ex)
        {
            ApplyResult = "Ошибка при применении: " + ex.Message;
        }
        finally
        {
            IsBusy = false;
            OnPropertyChanged(nameof(ApplyResult));
            RefreshBackground();
        }
    }

    public void RevertFixes()
    {
        if (IsBusy) return;

        if (!IsAdministrator)
        {
            ApplyResult = "Нужны права администратора для отката.";
            OnPropertyChanged(nameof(ApplyResult));
            return;
        }

        IsBusy = true;

        try
        {
            var journal = new UndoJournal();
            if (journal.Entries.Count == 0)
            {
                ApplyResult = "Откатывать нечего: программа ещё ничего не меняла.";
                return;
            }

            Status = "Возвращаю настройки…";

            var context = new DiagnosticContext
            {
                IsAdministrator = IsAdministrator,
                OnProgress = message => Status = message
            };

            var report = FixRunner.RevertAll(context, journal);

            ApplyResult = string.Join(Environment.NewLine, report.Results.Select(r =>
                (r.Outcome == FixOutcome.Applied ? "✓ " : "✗ ") + r.Title + ": " + r.Message)) +
                Environment.NewLine + Environment.NewLine + report.Summary;

            Status = "Откат выполнен";
        }
        catch (Exception ex)
        {
            ApplyResult = "Ошибка при откате: " + ex.Message;
        }
        finally
        {
            IsBusy = false;
            OnPropertyChanged(nameof(ApplyResult));
        }
    }

    // -------------------------------------------------------------- пауза фона
    public string BackgroundResult { get; private set; } = string.Empty;

    public void PauseSelectedBackground()
    {
        var selected = BackgroundApps.Where(a => a.Selected && !string.IsNullOrEmpty(a.ProcessName)).ToList();
        if (selected.Count == 0)
        {
            BackgroundResult = "Ничего не выбрано. Отметьте программы, которые нужно поставить на паузу.";
            OnPropertyChanged(nameof(BackgroundResult));
            return;
        }

        var context = new DiagnosticContext { IsAdministrator = IsAdministrator };
        var state = _background.Pause(selected.Select(a => a.ProcessName), context);

        BackgroundResult = state.Apps.Count == 0
            ? "Ничего не удалось остановить (возможно, требуются права администратора)."
            : $"На паузе: {string.Join(", ", state.Apps.Select(a => a.Title))}. " +
              "Вернуть их можно кнопкой «Вернуть обратно».";

        OnPropertyChanged(nameof(BackgroundResult));
        RefreshBackground();
    }

    public void ResumeBackground()
    {
        var state = _background.GetCurrentState();
        if (state.IsEmpty)
        {
            BackgroundResult = "Ничего не стоит на паузе.";
            OnPropertyChanged(nameof(BackgroundResult));
            return;
        }

        var context = new DiagnosticContext { IsAdministrator = IsAdministrator };
        var (restored, failed) = _background.Resume(context);

        BackgroundResult = $"Возвращено программ: {restored}" +
                           (failed > 0 ? $", не удалось запустить: {failed} — запустите вручную." : ".");

        OnPropertyChanged(nameof(BackgroundResult));
        RefreshBackground();
    }

    // ------------------------------------------------------------------ служебное
    public event PropertyChangedEventHandler? PropertyChanged;

    private void OnPropertyChanged([CallerMemberName] string? name = null) =>
        PropertyChanged?.Invoke(this, new PropertyChangedEventArgs(name));
}

/// <summary>Сведения о правах и о том, как повысить их при необходимости.</summary>
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

    /// <summary>Перезапустить программу с запросом прав администратора.</summary>
    public static void RestartElevated()
    {
        try
        {
            var exe = Process.GetCurrentProcess().MainModule?.FileName;
            if (string.IsNullOrEmpty(exe)) return;

            Process.Start(new ProcessStartInfo
            {
                FileName = exe,
                UseShellExecute = true,
                Verb = "runas"
            });

            Application.Current.Shutdown();
        }
        catch
        {
            // пользователь отказался от UAC — остаёмся без прав, это допустимо
        }
    }
}
