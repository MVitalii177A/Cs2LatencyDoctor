using System.Collections.ObjectModel;
using System.ComponentModel;
using System.Diagnostics;
using System.Runtime.CompilerServices;
using System.Windows;
using Cs2LatencyDoctor.Core;
using Cs2LatencyDoctor.Core.Background;
using Cs2LatencyDoctor.Core.Fixes;
using Cs2LatencyDoctor.Core.History;
using Cs2LatencyDoctor.Core.Report;

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

    /// <summary>Что делать, если программа помочь не может или может не всё.</summary>
    public string Recommendation { get; init; } = string.Empty;

    /// <summary>Заголовок блока рекомендации: «Что делать» или «Что делать (причина)».</summary>
    public string RecommendationTitle { get; init; } = string.Empty;

    public bool HasWhy => !string.IsNullOrWhiteSpace(Why);
    public bool HasFix => !string.IsNullOrWhiteSpace(FixHint);
    public bool HasRecommendation => !string.IsNullOrWhiteSpace(Recommendation);

    /// <summary>
    /// Идентификатор находки: по нему собирается план применения.
    /// </summary>
    public required string FindingId { get; init; }

    /// <summary>
    /// Отмечено ли исправление этой находки. Галочка показывается только там,
    /// где программа правда может исправить, и стоит по умолчанию.
    ///
    /// Зачем это. Раньше кнопка применяла всё найденное сразу, и выбрать часть
    /// было нельзя. Применялось только найденное, поэтому вреда не было, но
    /// человек, который хотел поменять одно и проверить эффект, не мог.
    /// </summary>
    public bool ApplySelected { get; set; }

    /// <summary>Можно ли применять эту находку по кнопке.</summary>
    public required bool CanApply { get; init; }
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

/// <summary>Строка списка ручных действий: что сделать и почему программа не может сама.</summary>
public sealed class ManualFixRow
{
    public required string Title { get; init; }

    /// <summary>В какой проверке это найдено — чтобы человек понимал контекст.</summary>
    public required string Finding { get; init; }

    /// <summary>Почему программа не делает это сама.</summary>
    public required string Reason { get; init; }
}

/// <summary>Строка журнала изменений: что поменяли и на какое значение.</summary>
public sealed class JournalRow
{
    public required int Number { get; init; }
    public required string Title { get; init; }
    public required string Change { get; init; }
    public required string When { get; init; }

    /// <summary>Сама запись журнала: по ней выполняется возврат.</summary>
    public required JournalEntry Entry { get; init; }
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

    /// <summary>
    /// Что придётся сделать руками, с причиной почему. Отдельным списком, потому что
    /// раньше эти пункты терялись среди находок, и человек мог решить, что программа
    /// сделает всё сама.
    /// </summary>
    public ObservableCollection<ManualFixRow> ManualFixes { get; } = new();

    /// <summary>Что программа уже изменила: видно, что и когда, с возможностью вернуть по одному.</summary>
    public ObservableCollection<JournalRow> Journal { get; } = new();

    /// <summary>Что изменилось с прошлого замера: отдельно от истории за всё время.</summary>
    private string _beforeAfterText = string.Empty;
    public string BeforeAfterText
    {
        get => _beforeAfterText;
        set { _beforeAfterText = value; OnPropertyChanged(); }
    }

    private string _beforeAfterTitle = "ПОСЛЕ ИСПРАВЛЕНИЙ";
    public string BeforeAfterTitle
    {
        get => _beforeAfterTitle;
        set { _beforeAfterTitle = value; OnPropertyChanged(); }
    }

    public bool HasManualFixes => ManualFixes.Count > 0;
    public bool HasJournal => Journal.Count > 0;

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

    private int _probeSeconds = 8;
    public int ProbeSeconds
    {
        get => _probeSeconds;
        set { _probeSeconds = Math.Clamp(value, 3, 60); OnPropertyChanged(); }
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
        // Собираем список вне потока интерфейса, применяем — в потоке интерфейса.
        var rows = _background.Survey().Select(app => new BackgroundRow
        {
            Title = app.Title,
            ProcessName = app.ProcessName,
            Details = $"{app.ProcessCount} проц., {app.MemoryMb:0} МБ" +
                      (app.OpenConnections > 0 ? $", соединений: {app.OpenConnections}" : string.Empty),
            Reason = app.Reason,

            // Найденное по нагрузке НИКОГДА не отмечаем заранее: программа не знает,
            // что это за процесс и есть ли в нём несохранённая работа. Первая версия
            // этого поиска отмечала найденное и закрыла браузер вместе с открытыми
            // вкладками. Такие строки человек выбирает сам.
            Selected = !app.FoundByActivity &&
                       (app.Title.Contains("Торрент", StringComparison.OrdinalIgnoreCase)
                        || app.Title.Contains("Dropbox", StringComparison.OrdinalIgnoreCase))
        }).ToList();

        if (rows.Count == 0)
        {
            rows.Add(new BackgroundRow
            {
                Title = "Ничего лишнего не запущено",
                ProcessName = string.Empty,
                Details = string.Empty,
                Reason = "Из списка известных фоновых программ сейчас ничего не работает — игре ничего не мешает."
            });
        }

        Ui(() =>
        {
            BackgroundApps.Clear();
            foreach (var row in rows) BackgroundApps.Add(row);
        });
    }

    // -------------------------------------------------------------- диагностика
    /// <summary>Человеческое объяснение причины, по которой программа не смогла помочь.</summary>
    private static string DescribeReason(NoHelpReason reason) => reason switch
    {
        NoHelpReason.HardwareNotSupported => "железо не поддерживает настройку",
        NoHelpReason.BlockedBySystem => "система блокирует доступ",
        NoHelpReason.VendorLocked => "закрыто производителем",
        NoHelpReason.OutsideThisPc => "причина вне компьютера",
        NoHelpReason.NeedsPhysicalAction => "нужно физическое действие",
        NoHelpReason.NotEnoughData => "не хватает данных",
        NoHelpReason.NeedsAdmin => "нужны права администратора",
        _ => "пояснение"
    };

    public async Task RunDiagnosticsAsync()
    {
        if (IsBusy) return;

        IsBusy = true;
        Findings.Clear();
        Summary = "Идёт проверка…";

        // Прогресс приходит из фоновых потоков очень часто. Если передавать его
        // в интерфейс на каждое сообщение, очередь потока интерфейса забивается,
        // и окно перестаёт реагировать на мышь и кнопку закрытия. Поэтому копим
        // последнее сообщение и показываем его по таймеру, а не на каждое событие.
        var pendingStatus = "Начинаю проверку…";
        var pendingLock = new object();

        void ReportProgress(string message)
        {
            lock (pendingLock) pendingStatus = message;
        }

        var progressTimer = new System.Windows.Threading.DispatcherTimer
        {
            Interval = TimeSpan.FromMilliseconds(250)
        };

        progressTimer.Tick += (_, _) =>
        {
            string message;
            lock (pendingLock) message = pendingStatus;

            if (!string.Equals(Status, message, StringComparison.Ordinal))
                Status = message;
        };

        progressTimer.Start();

        try
        {
            // ВАЖНО: вся диагностика выполняется внутри Task.Run, то есть в фоновом потоке.
            //
            // Почему так, а не просто await: до первого настоящего await проверки успевают
            // сделать много синхронной работы — обход сетевых адаптеров через WMI, чтение
            // реестра, поиск папок CS2. WMI в Windows 10 умеет отвечать десятками секунд,
            // и всё это время поток интерфейса остаётся занят: окно не двигается,
            // не закрывается и не перерисовывается. Именно это выглядело как зависание.
            var report = await Task.Run(async () =>
            {
                var context = new DiagnosticContext
                {
                    IsAdministrator = IsAdministrator,
                    ProbeSeconds = ProbeSeconds,
                    OnProgress = ReportProgress
                };

                return await DiagnosticRunner.CreateDefault(ProbeSeconds)
                    .RunAsync(context)
                    .ConfigureAwait(false);
            }).ConfigureAwait(false);

            // Замер пишем в локальную историю до показа результатов:
            // так сводка «что изменилось» сравнивает с прошлыми запусками.
            var historyBefore = report.CaptureAndSummarize(_history);

            // Строки отчёта готовим вне потока интерфейса, а в список добавляем
            // одним действием в потоке интерфейса — иначе WPF не даст менять коллекцию.
            var rows = new List<FindingRow>();

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

                // Галочка выбора: показываем только там, где программа действительно
                // может исправить. Обещать кнопку там, где её нет, нельзя.
                var canApply = result.ApplicableFixes.Count > 0;

                rows.Add(new FindingRow
                {
                    Mark = mark,
                    FindingId = result.Id,
                    Title = result.Title,
                    Detail = result.Detail,
                    Why = result.Why ?? string.Empty,
                    FixHint = fixHint,
                    Color = color,
                    Recommendation = result.Recommendation ?? string.Empty,
                    RecommendationTitle = result.NoHelpReason == NoHelpReason.None
                        ? "Что сделать вам"
                        : "Что делать: " + DescribeReason(result.NoHelpReason),
                    CanApply = canApply,
                    // Отмечено по умолчанию: человек нажал «Применить» именно за этим.
                    ApplySelected = canApply
                });
            }

            // Запоминаем сами находки, а не только строки для показа: по ним
            // собирается план исправлений, чтобы применять ровно найденное.
            _lastFindings = report.Results;
            _lastReport = report;
            _lastHistory = historyBefore;

            // Отдельно собираем то, что придётся делать руками. Без этого списка
            // человек видит «программа исправит не всё» и не понимает, что осталось.
            var manual = report.Results
                .SelectMany(r => r.ManualFixes.Select(f => new ManualFixRow
                {
                    Title = f.Title,
                    Finding = r.Title,
                    Reason = f.WhyNotAutomatic ?? "Требуется действие руками"
                }))
                .GroupBy(m => m.Title)
                .Select(g => g.First())
                .ToList();

            // И то, что программа уже изменила: с возможностью вернуть по одной записи.
            var journalRows = BuildJournalRows();

            Ui(() =>
            {
                Findings.Clear();
                foreach (var row in rows) Findings.Add(row);

                ManualFixes.Clear();
                foreach (var row in manual) ManualFixes.Add(row);

                Journal.Clear();
                foreach (var row in journalRows) Journal.Add(row);

                OnPropertyChanged(nameof(HasManualFixes));
                OnPropertyChanged(nameof(HasJournal));
            });

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

            // Сравнение с ПРЕДЫДУЩИМ замером, а не с первым за всё время.
            // Человеку нужен ответ на вопрос «что изменилось после того, как я применил
            // исправления», а сводка истории отвечает на другой — «помогло ли за всё
            // время вообще». Это разные вопросы, и путать их нельзя.
            UpdateBeforeAfter();
        }
        catch (Exception ex)
        {
            Summary = "Проверка не завершилась";
            Status = "Ошибка: " + ex.Message;
        }
        finally
        {
            progressTimer.Stop();
            IsBusy = false;
        }
    }

    // -------------------------------------------------------------- исправления
    public int FixableCount => Findings.Count(f => f.HasFix);

    public string ApplyResult { get; private set; } = string.Empty;

    /// <summary>Находки последней проверки: по ним собирается план исправлений.</summary>
    private IReadOnlyList<CheckResult> _lastFindings = Array.Empty<CheckResult>();

    /// <summary>Отчёт последней проверки: из него собирается файл для отправки.</summary>
    private DiagnosticReport? _lastReport;

    /// <summary>История на момент проверки: попадает в отчёт, чтобы было видно динамику.</summary>
    private HistorySummary? _lastHistory;

    /// <summary>
    /// Что именно применять. Если проверка ещё не выполнялась, план пуст и программа
    /// применит всё, что умеет: так честнее, чем молча ничего не сделать.
    /// </summary>
    private FixPlan BuildPlan()
    {
        if (_lastFindings.Count == 0) return FixPlan.Everything;

        // Оставляем только те находки, у которых человек не снял галочку.
        var selected = Findings.Where(f => f.ApplySelected).Select(f => f.FindingId).ToHashSet();

        if (selected.Count == 0) return FixPlan.FromFindings(Array.Empty<CheckResult>());

        return FixPlan.FromFindings(_lastFindings.Where(r => selected.Contains(r.Id)));
    }

    /// <summary>Сколько находок отмечено к исправлению. Для текста на кнопке.</summary>
    public int SelectedForApplyCount => Findings.Count(f => f.CanApply && f.ApplySelected);

    /// <summary>
    /// Сохранить отчёт в файл. Нужно, чтобы результат можно было показать:
    /// в поддержку провайдера, на форум, в чат. Файл остаётся у человека,
    /// никуда не отправляется.
    /// </summary>
    public void ExportReport(string path, bool asJson)
    {
        if (_lastReport is null)
        {
            ApplyResult = "Сначала выполните проверку: сохранять пока нечего.";
            OnPropertyChanged(nameof(ApplyResult));
            return;
        }

        try
        {
            var exported = asJson
                ? ReportExporter.SaveJson(_lastReport, path, _lastHistory)
                : ReportExporter.SaveText(_lastReport, path, _lastHistory);

            ApplyResult = $"Отчёт сохранён ({exported.Format}, {exported.SizeText}):" +
                          Environment.NewLine + exported.FilePath;
            Status = "Отчёт сохранён";
        }
        catch (Exception ex)
        {
            ApplyResult = "Не удалось сохранить отчёт: " + ex.Message;
            Status = "Ошибка сохранения";
        }

        OnPropertyChanged(nameof(ApplyResult));
    }
    /// <summary>
    /// Сравнить последний замер с предыдущим и показать разницу словами.
    /// Это ответ на вопрос «что изменилось после применённых исправлений».
    /// </summary>
    private void UpdateBeforeAfter()
    {
        var comparison = _history.CompareLastTwo();

        if (!comparison.HasHistory || (comparison.Improved.Count == 0 && comparison.Worsened.Count == 0))
        {
            BeforeAfterTitle = "ПОСЛЕ ИСПРАВЛЕНИЙ";
            BeforeAfterText = "Сравнивать пока не с чем: нужны два замера. " +
                              "Нажмите «Проверить компьютер» ещё раз после исправлений.";
            return;
        }

        var lines = new List<string>();

        foreach (var trend in comparison.Improved)
            lines.Add($"↓ {trend.Title}: {DescribeTrend(trend)}");

        foreach (var trend in comparison.Worsened)
            lines.Add($"↑ {trend.Title}: {DescribeTrend(trend)}");

        BeforeAfterTitle = comparison.Worsened.Count > 0
            ? "ПОСЛЕ ИСПРАВЛЕНИЙ: ЕСТЬ УХУДШЕНИЯ"
            : "ПОСЛЕ ИСПРАВЛЕНИЙ: СТАЛО ЛУЧШЕ";

        BeforeAfterText = string.Join(Environment.NewLine, lines);
    }

    /// <summary>
    /// Описать изменение человеческим языком. Имя метрики в коде («spike_percent»)
    /// ничего не говорит тому, кто читает окно, поэтому переводим.
    /// </summary>
    private static string DescribeTrend(MetricTrend trend)
    {
        var title = trend.MetricName switch
        {
            "spike_percent" => "всплески",
            "loss_percent" => "потери",
            "stddev_ms" => "разброс",
            "max_ms" => "максимум",
            "median_ms" => "медиана",
            "free_percent" => "свободное место",
            "free_gb" => "свободное место",
            "speed_mhz" => "частота",
            "modules" => "планок памяти",
            "slots_total" => "всего слотов",
            "total_gb" => "объём памяти",
            "link_speed_mbps" => "скорость линка",
            "kill_count" => "проверок",
            _ => trend.MetricName
        };

        var unit = trend.MetricName switch
        {
            "spike_percent" or "loss_percent" or "free_percent" => "%",
            "stddev_ms" or "max_ms" or "median_ms" => " мс",
            "speed_mhz" => " МГц",
            "free_gb" or "total_gb" => " ГБ",
            "slots_total" or "modules" or "kill_count" => string.Empty,
            "link_speed_mbps" => " Мбит/с",
            _ => string.Empty
        };

        return $"было {trend.FirstValue:0.#}{unit}, стало {trend.LastValue:0.#}{unit}";
    }

    /// <summary>Собрать строки журнала для показа в окне.</summary>
    private static List<JournalRow> BuildJournalRows()
    {
        try
        {
            var journal = new UndoJournal();

            return journal.Entries
                .Select((e, index) => new JournalRow
                {
                    Number = index + 1,
                    Title = e.Title,
                    Change = string.IsNullOrEmpty(e.OldValue)
                        ? "параметра не было → " + e.NewValue
                        : $"{e.OldValue} → {e.NewValue}",
                    When = e.AppliedAt.ToString("dd.MM HH:mm"),
                    Entry = e
                })
                .ToList();
        }
        catch
        {
            return new List<JournalRow>();
        }
    }

    /// <summary>
    /// Вернуть одну запись журнала. Возврат идёт в фоновом потоке: снятие настройки
    /// может занять секунды, а окно не должно замереть.
    /// </summary>
    public void RevertJournalEntry(JournalRow row)
    {
        if (IsBusy || row is null) return;

        if (!IsAdministrator)
        {
            ApplyResult = "Нужны права администратора: возврат меняет системные настройки.";
            OnPropertyChanged(nameof(ApplyResult));
            return;
        }

        IsBusy = true;
        Status = "Возвращаю: " + row.Title + "…";

        Task.Run(() =>
        {
            try
            {
                var context = new DiagnosticContext
                {
                    IsAdministrator = IsAdministrator,
                    OnProgress = message => Status = message
                };

                var journal = new UndoJournal();
                var report = FixRunner.RevertOne(context, journal, row.Entry);

                ApplyResult = string.Join(Environment.NewLine, report.Results.Select(r =>
                    (r.Outcome == FixOutcome.Applied ? "✓ " : "✗ ") + r.Title + ": " + r.Message));

                Status = report.FailedCount > 0 ? "Возврат не подтвердился" : "Возвращено";
            }
            catch (Exception ex)
            {
                ApplyResult = "Ошибка при возврате: " + ex.Message;
                Status = "Ошибка";
            }
            finally
            {
                RefreshJournal();
                IsBusy = false;
            }
        });
    }

    /// <summary>Обновить список журнала: вызывается после применения и возврата.</summary>
    public void RefreshJournal()
    {
        var rows = BuildJournalRows();

        Ui(() =>
        {
            Journal.Clear();
            foreach (var row in rows) Journal.Add(row);
            OnPropertyChanged(nameof(HasJournal));
        });
    }

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
        Status = "Применяю исправления…";

        // Работа идёт в фоновом потоке: остановка служб и перезапуск адаптера
        // занимают секунды, и в потоке интерфейса окно бы замерло.
        Task.Run(() =>
        {
            try
            {
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

                // План собирается из последних находок: применяем ровно то, на что жаловалась
                // диагностика, а не весь набор исправлений целиком.
                var plan = BuildPlan();

                if (!plan.IsEmpty)
                    Status = "К применению: " + plan.Describe();

                var report = FixRunner.CreateDefault(FixSelection.Safe).ApplyAll(context, journal, plan);

                ApplyResult = string.Join(Environment.NewLine, report.Results.Select(r => r.Outcome switch
                {
                    FixOutcome.Applied => "✓ " + r.Title + ": " + r.Message,
                    FixOutcome.AlreadyOk => "• " + r.Title + ": " + r.Message,
                    FixOutcome.Skipped => "— " + r.Title + ": " + r.Message,
                    _ => "✗ " + r.Title + ": " + r.Message
                })) +
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
                Ui(() =>
                {
                    IsBusy = false;
                    OnPropertyChanged(nameof(ApplyResult));
                });
                RefreshBackground();
            }
        });
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
        Status = "Возвращаю настройки…";

        Task.Run(() =>
        {
            try
            {
                var journal = new UndoJournal();
                if (journal.Entries.Count == 0)
                {
                    ApplyResult = "Откатывать нечего: программа ещё ничего не меняла.";
                    return;
                }

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
                Ui(() =>
                {
                    IsBusy = false;
                    OnPropertyChanged(nameof(ApplyResult));
                });
            }
        });
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
        Ui(() => PropertyChanged?.Invoke(this, new PropertyChangedEventArgs(name)));

    /// <summary>
    /// Выполнить действие в потоке интерфейса.
    ///
    /// Это не перестраховка: асинхронные проверки возвращаются из await на разных
    /// потоках, а ObservableCollection, привязанный к списку, нельзя менять из чужого
    /// потока — WPF в этом случае выбрасывает «CollectionView не поддерживает изменения
    /// из потока, отличного от Dispatcher», и список просто перестаёт обновляться.
    /// </summary>
    private static void Ui(Action action)
    {
        var dispatcher = Application.Current?.Dispatcher;

        // Интерфейса может не быть вовсе (тестовый или консольный запуск).
        if (dispatcher is null)
        {
            action();
            return;
        }

        if (dispatcher.CheckAccess())
        {
            action();
            return;
        }

        // BeginInvoke, а не Invoke: он ставит задачу в очередь и сразу возвращает
        // управление. Invoke блокировал бы фоновый поток до обработки очереди,
        // а если очередь забита — окно перестаёт отвечать на мышь и закрытие.
        dispatcher.BeginInvoke(action);
    }
}

/// <summary>Сведения о правах и о том, как повысить их при необходимости.</summary>
public static class HostInfo
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
