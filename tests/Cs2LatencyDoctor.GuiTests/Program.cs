using System.Reflection;
using System.Text;
using System.Windows;
using System.Windows.Media;
using Cs2LatencyDoctor.Gui;

namespace Cs2LatencyDoctor.GuiTests;

/// <summary>
/// Проверка GUI без показа окна: разметка разбирается, модель представления работает,
/// диагностика реально запускается. Нужна потому, что «собралось без ошибок» —
/// это ещё не «работает»: ошибки в привязках и обработчиках видны только при загрузке XAML.
/// </summary>
internal static class Program
{
    private static int _passed;
    private static int _failed;
    private static readonly StringBuilder Log = new();

    [STAThread]
    private static int Main(string[] args)
    {
        Console.OutputEncoding = Encoding.UTF8;

        var seconds = 6;
        for (var i = 0; i < args.Length - 1; i++)
            if (args[i] == "--seconds" && int.TryParse(args[i + 1], out var s))
                seconds = Math.Clamp(s, 5, 30);

        Console.WriteLine();
        Console.WriteLine("  ПРОВЕРКА GUI (окно не показывается)");
        Console.WriteLine("  " + new string('-', 68));

        // Application нужен для корректной работы WPF-типов, но окно не открываем.
        if (Application.Current is null)
        {
            _ = new Application { ShutdownMode = ShutdownMode.OnExplicitShutdown };
        }

        CheckXamlLoads();
        CheckViewModelLogic(seconds);
        CheckWindowConstruction();

        Console.WriteLine(Log.ToString());
        Console.WriteLine("  " + new string('-', 68));
        Console.WriteLine($"  ИТОГ: пройдено {_passed}, провалено {_failed}");
        Console.WriteLine();

        return _failed == 0 ? 0 : 1;
    }

    private static void Check(string name, Action action)
    {
        try
        {
            action();
            _passed++;
            Log.AppendLine($"  [ПРОЙДЕНО] {name}");
        }
        catch (Exception ex)
        {
            _failed++;
            Log.AppendLine($"  [ПРОВАЛ]   {name}");
            Log.AppendLine($"             {ex.GetType().Name}: {ex.Message}");
        }
    }

    private static void Assert(bool condition, string message)
    {
        if (!condition) throw new InvalidOperationException(message);
    }

    /// <summary>
    /// Обойти дерево, не падая, если элемент ещё не отрисован.
    /// Перебираем и визуальное дерево, и логическое: в разных состояниях
    /// (до раскладки, в скрытом окне) заполнено то одно, то другое.
    /// </summary>
    private static IEnumerable<T> FindVisualChildren<T>(DependencyObject root) where T : DependencyObject
    {
        var count = 0;
        try { count = VisualTreeHelper.GetChildrenCount(root); }
        catch { yield break; }

        for (var i = 0; i < count; i++)
        {
            DependencyObject? child = null;
            try { child = VisualTreeHelper.GetChild(root, i); }
            catch { /* элемент без визуального дерева */ }

            if (child is null) continue;
            if (child is T typed) yield return typed;

            foreach (var nested in FindVisualChildren<T>(child))
                yield return nested;
        }
    }

    /// <summary>Собрать элементы логического дерева — работает даже без раскладки.</summary>
    private static IEnumerable<T> FindLogicalChildren<T>(DependencyObject root) where T : DependencyObject
    {
        foreach (var raw in LogicalTreeHelper.GetChildren(root))
        {
            if (raw is not DependencyObject child) continue;
            if (child is T typed) yield return typed;

            foreach (var nested in FindLogicalChildren<T>(child))
                yield return nested;
        }
    }

    /// <summary>Все элементы нужного типа: сначала визуальные, если пусто — логические.</summary>
    private static List<T> Collect<T>(Window window) where T : DependencyObject
    {
        var visual = FindVisualChildren<T>(window).ToList();
        if (visual.Count > 0) return visual;

        var logical = new List<T>();
        if (window.Content is DependencyObject content) logical.AddRange(FindLogicalChildren<T>(content));
        return logical;
    }

    // ------------------------------------------------------------------ XAML
    private static void CheckXamlLoads()
    {
        Check("Разметка окна загружается (XAML разбирается)", () =>
        {
            var resourceName = FindResourceName();

            using var stream = typeof(MainWindow).Assembly.GetManifestResourceStream(resourceName)
                ?? throw new InvalidOperationException(
                    $"Ресурс разметки не найден. Искали: {resourceName}");

            // LoadComponent разбирает BAML: здесь всплывают ошибки привязок,
            // имён элементов и обработчиков событий.
            var window = new MainWindow();
            Assert(window.Content is not null, "У окна нет содержимого — разметка не применилась");
        });
    }

    private static string FindResourceName()
    {
        var names = typeof(MainWindow).Assembly.GetManifestResourceNames();
        var found = names.FirstOrDefault(n => n.EndsWith("mainwindow.baml", StringComparison.OrdinalIgnoreCase));

        return found ?? string.Join(", ", names);
    }

    // ------------------------------------------------------- модель представления
    private static void CheckViewModelLogic(int seconds)
    {
        MainViewModel? viewModel = null;

        Check("Модель представления создаётся", () =>
        {
            viewModel = new MainViewModel();
            Assert(viewModel.IsIdle, "Сразу после создания модель должна быть не занята");
            Assert(!string.IsNullOrWhiteSpace(viewModel.AdminText), "Не заполнен текст о правах");
        });

        if (viewModel is null) return;

        Check("Список фоновых программ заполняется без падения", () =>
        {
            viewModel.RefreshBackground();
            Assert(viewModel.BackgroundApps.Count > 0,
                "Список пуст: даже при отсутствии лишних программ должна быть строка-пояснение");
        });

        Check($"Диагностика выполняется ({seconds} сек на замер)", () =>
        {
            viewModel.ProbeSeconds = seconds;
            PumpUntilComplete(viewModel.RunDiagnosticsAsync());

            Assert(viewModel.Findings.Count > 0, "Диагностика не дала ни одной находки");
            Assert(viewModel.IsIdle, "После проверки модель должна вернуться в состояние «не занята»");
        });

        Check("Каждая находка заполнена для показа", () =>
        {
            foreach (var finding in viewModel.Findings)
            {
                Assert(!string.IsNullOrWhiteSpace(finding.Title), "Пустой заголовок находки");
                Assert(!string.IsNullOrWhiteSpace(finding.Detail), $"Пустая деталь у «{finding.Title}»");
                Assert(!string.IsNullOrWhiteSpace(finding.Mark), $"Нет метки у «{finding.Title}»");
                Assert(finding.Color.StartsWith('#'), $"Цвет не в формате #RRGGBB у «{finding.Title}»");
            }
        });

        Check("Сводка и статус заполнены", () =>
        {
            Assert(!string.IsNullOrWhiteSpace(viewModel.Summary), "Пустая сводка");
            Assert(!string.IsNullOrWhiteSpace(viewModel.Status), "Пустой статус");
        });

        Check("Замер попал в локальную историю", () =>
        {
            Assert(!string.IsNullOrWhiteSpace(viewModel.HistoryText), "Пустая строка истории");
        });

        Check("Повторная диагностика не ломает список", () =>
        {
            var before = viewModel.Findings.Count;
            PumpUntilComplete(viewModel.RunDiagnosticsAsync());

            Assert(viewModel.Findings.Count > 0, "После повторного запуска список опустел");
            Assert(viewModel.Findings.Count <= before * 3 + 10,
                "Список находок растёт неограниченно — значит он не очищается");
        });

        Check("Откат без прав не падает и объясняет причину", () =>
        {
            viewModel.RevertFixes();
            Assert(!string.IsNullOrWhiteSpace(viewModel.ApplyResult),
                "После попытки отката нет пояснения для пользователя");
        });

        Check("Пауза без выбранных программ объясняет, что делать", () =>
        {
            foreach (var app in viewModel.BackgroundApps) app.Selected = false;
            viewModel.PauseSelectedBackground();

            Assert(viewModel.BackgroundResult.Contains("Ничего не выбрано", StringComparison.OrdinalIgnoreCase),
                "Нет понятного сообщения при пустом выборе");
        });
    }

    // -------------------------------------------------------------------- окно
    private static void CheckWindowConstruction()
    {
        Check("Окно собирается целиком (разметка + обработчики + модель)", () =>
        {
            var window = new MainWindow();

            Assert(window.DataContext is MainViewModel, "Модель представления не привязана к окну");
            Assert(window.Title.Contains("Cs2LatencyDoctor"), "Не задан заголовок окна");
            Assert(window.Width > 900, "Ширина окна меньше ожидаемой — разметка не применилась");
            Assert(window.Content is not null, "У окна нет содержимого — разметка не применилась");

            window.Measure(new Size(1100, 760));
            window.Arrange(new Rect(0, 0, 1100, 760));
            window.UpdateLayout();

            var buttons = Collect<System.Windows.Controls.Button>(window);
            Assert(buttons.Count >= 6, $"Кнопок найдено {buttons.Count}, ожидалось не меньше шести");

            var titles = buttons.Select(b => b.Content?.ToString() ?? string.Empty).ToList();
            foreach (var expected in new[] { "Проверить компьютер", "Применить исправления", "Откатить изменения" })
            {
                Assert(titles.Any(t => t.Contains(expected, StringComparison.OrdinalIgnoreCase)),
                    $"Не найдена кнопка «{expected}»");
            }

            window.Close();
        });

        Check("Привязки данных действительно применены", () =>
        {
            // Проверяем не имена элементов, а то, что привязки дают реальные значения.
            // Если привязка сломана, текст останется пустым — и это сразу видно.
            var viewModel = new MainViewModel { ProbeSeconds = 5 };
            var window = new MainWindow { DataContext = viewModel };

            // Привязки применяются не в момент загрузки разметки, а когда очередь
            // диспетчера доходит до них. Без этой прокачки тексты будут пустыми,
            // и тест «поймает» несуществующую ошибку.
            PumpLayout(window);

            var texts = Collect<System.Windows.Controls.TextBlock>(window)
                .Select(t => t.Text ?? string.Empty)
                .Where(t => t.Length > 0)
                .ToList();

            Assert(texts.Count > 0, "В окне нет ни одного заполненного текстового блока\n"
                                    + "Найденные тексты:\n  " + string.Join("\n  ", texts.Take(20)));

            Assert(texts.Any(t => t.Contains("Cs2LatencyDoctor", StringComparison.Ordinal)),
                "Не найден заголовок программы\nНайденные тексты:\n  " + string.Join("\n  ", texts.Take(20)));

            Assert(texts.Any(t => t.Contains("Права", StringComparison.OrdinalIgnoreCase)),
                "Не выводится строка о правах администратора — привязка AdminText не работает.\n"
                + "Найденные тексты:\n  " + string.Join("\n  ", texts.Take(20)));

            Assert(texts.Any(t => t.Contains("замер", StringComparison.OrdinalIgnoreCase)
                                  || t.Contains("История", StringComparison.Ordinal)),
                "Не выводится текст истории замеров — привязка HistoryText не работает.\n"
                + "Найденные тексты:\n  " + string.Join("\n  ", texts.Take(20)));

            PumpUntilComplete(viewModel.RunDiagnosticsAsync(), window);
            PumpLayout(window);

            var state = $"\n  Состояние модели: находок={viewModel.Findings.Count}, IsIdle={viewModel.IsIdle}\n"
                        + $"  Summary: {viewModel.Summary}\n  Status: {viewModel.Status}";

            Assert(viewModel.Findings.Count > 0, "Диагностика не дала находок");

            // Элементы списков WPF создаёт только при реальной отрисовке окна —
            // в скрытом окне контейнеров не будет. Поэтому проверяем само связывание:
            // у списка находок источник данных должен указывать на модель.
            var lists = Collect<System.Windows.Controls.ItemsControl>(window);

            Assert(lists.Count > 0, "В окне нет ни одного списка — разметка потеряла ItemsControl");

            var findingsList = lists.FirstOrDefault(l => ReferenceEquals(l.ItemsSource, viewModel.Findings));
            Assert(findingsList is not null,
                "Список находок не привязан к модели — привязка ItemsSource не работает." + state);

            var backgroundList = lists.FirstOrDefault(l => ReferenceEquals(l.ItemsSource, viewModel.BackgroundApps));
            Assert(backgroundList is not null,
                "Список фоновых программ не привязан к модели." + state);

            window.Close();
        });
    }

    /// <summary>
    /// Прогнать раскладку и очередь диспетчера. Без этого привязки,
    /// заданные в разметке, ещё не применены к элементам.
    /// </summary>
    private static void PumpLayout(Window window)
    {
        window.Measure(new Size(1100, 760));
        window.Arrange(new Rect(0, 0, 1100, 760));
        window.UpdateLayout();

        // Прокачиваем очередь на всех приоритетах, включая Loaded и Binding,
        // иначе значения привязок не успевают попасть в элементы.
        foreach (var priority in new[]
                 {
                     System.Windows.Threading.DispatcherPriority.Loaded,
                     System.Windows.Threading.DispatcherPriority.DataBind,
                     System.Windows.Threading.DispatcherPriority.Render,
                     System.Windows.Threading.DispatcherPriority.Background
                 })
        {
            window.Dispatcher.Invoke(() => { }, priority);
        }

        window.UpdateLayout();
    }

    /// <summary>
    /// Дождаться задачи, продолжая прокачивать очередь диспетчера.
    ///
    /// Важно: обычный GetResult() здесь нельзя. Он блокирует поток STA,
    /// и тогда продолжения асинхронных методов не выполняются — модель
    /// остаётся в состоянии «идёт проверка», а список находок пустым.
    /// </summary>
    private static void PumpUntilComplete(Task task, Window? window = null, int timeoutSeconds = 180)
    {
        var dispatcher = window?.Dispatcher ?? System.Windows.Threading.Dispatcher.CurrentDispatcher;
        var deadline = DateTime.UtcNow.AddSeconds(timeoutSeconds);

        while (!task.IsCompleted && DateTime.UtcNow < deadline)
        {
            dispatcher.Invoke(() => { }, System.Windows.Threading.DispatcherPriority.Background);
            Thread.Sleep(25);
        }

        if (!task.IsCompleted)
            throw new TimeoutException($"Задача не завершилась за {timeoutSeconds} секунд");

        // Пробрасываем исключение, если оно было.
        task.GetAwaiter().GetResult();

        if (window is not null) PumpLayout(window);
        else dispatcher.Invoke(() => { }, System.Windows.Threading.DispatcherPriority.Background);
    }
}
