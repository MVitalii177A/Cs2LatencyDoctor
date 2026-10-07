using System.Windows;
using Cs2LatencyDoctor.Core.Report;

namespace Cs2LatencyDoctor.Gui;

/// <summary>
/// Окно программы. Вся логика лежит в MainViewModel — здесь только реакция на нажатия.
/// </summary>
public partial class MainWindow : Window
{
    private readonly MainViewModel _viewModel = new();

    public MainWindow()
    {
        InitializeComponent();

        // Версия в шапке: по ней видно, какая сборка у человека, —
        // это первое, о чём спрашивают при разборе проблемы.
        VersionText.Text = Core.AppVersion.Display;

        DataContext = _viewModel;
    }

    private async void OnCheckClick(object sender, RoutedEventArgs e)
    {
        await _viewModel.RunDiagnosticsAsync();
    }

    private void OnApplyClick(object sender, RoutedEventArgs e)
    {
        var warning = "Программа изменит системные настройки, чтобы убрать задержки.\n\n" +
                      "Все прежние значения сохраняются, поэтому изменения можно вернуть " +
                      "кнопкой «Откатить изменения».\n\nПродолжить?";

        if (MessageBox.Show(warning, "Применить исправления",
                MessageBoxButton.YesNo, MessageBoxImage.Question) != MessageBoxResult.Yes)
            return;

        _viewModel.ApplyFixes();
    }

    private void OnRevertClick(object sender, RoutedEventArgs e)
    {
        var warning = "Все настройки, изменённые программой, будут возвращены к прежним значениям.\n\n" +
                      "Продолжить?";

        if (MessageBox.Show(warning, "Откатить изменения",
                MessageBoxButton.YesNo, MessageBoxImage.Question) != MessageBoxResult.Yes)
            return;

        _viewModel.RevertFixes();
    }

    private void OnElevateClick(object sender, RoutedEventArgs e)
    {
        var warning = "Программа будет перезапущена с правами администратора.\n" +
                      "Это нужно, чтобы менять системные настройки и останавливать службы.\n\n" +
                      "Продолжить?";

        if (MessageBox.Show(warning, "Перезапуск от администратора",
                MessageBoxButton.YesNo, MessageBoxImage.Question) != MessageBoxResult.Yes)
            return;

        HostInfo.RestartElevated();
    }

    private void OnPauseClick(object sender, RoutedEventArgs e)
    {
        var selected = _viewModel.BackgroundApps.Count(a => a.Selected && !string.IsNullOrEmpty(a.ProcessName));
        if (selected == 0)
        {
            _viewModel.PauseSelectedBackground();
            return;
        }

        var warning = $"Будет остановлено программ: {selected}.\n\n" +
                      "Если среди них есть синхронизация файлов, она прервётся и продолжится " +
                      "после возврата — данные не потеряются.\n\nПродолжить?";

        if (MessageBox.Show(warning, "Поставить на паузу",
                MessageBoxButton.YesNo, MessageBoxImage.Question) != MessageBoxResult.Yes)
            return;

        _viewModel.PauseSelectedBackground();
    }

    private void OnResumeClick(object sender, RoutedEventArgs e)
    {
        _viewModel.ResumeBackground();
    }

    /// <summary>
    /// Вернуть одну запись журнала. Кнопка есть у каждой записи: раньше откат
    /// возвращал весь журнал целиком, и отменить только часть было нельзя.
    /// </summary>
    private void OnRevertJournalClick(object sender, RoutedEventArgs e)
    {
        if (sender is not System.Windows.Controls.Button button) return;
        if (button.Tag is not JournalRow row) return;

        _viewModel.RevertJournalEntry(row);
    }

    /// <summary>
    /// Сохранить отчёт. Одна кнопка на два случая: раньше их было две, и отчёты
    /// пересекались — версия, сведения о системе, находки и история попадали в оба
    /// файла. Разница только в подробностях о состоянии программы, поэтому выбор
    /// делается здесь, при сохранении, а не двумя кнопками в окне.
    ///
    /// Формулировки про человека, а не про того, кто читает: «отправить автору»
    /// понятнее, чем «для разработчика», и не заставляет гадать, чем файлы различаются.
    /// </summary>
    private void OnExportClick(object sender, RoutedEventArgs e)
    {
        var dialog = new Microsoft.Win32.SaveFileDialog
        {
            Title = "Сохранить отчёт о проверке",
            FileName = ReportExporter.SuggestFileName(DateTimeOffset.Now, "txt"),
            DefaultExt = ".txt",
            Filter =
                "Отчёт для отправки автору — подробный (*.txt)|*.txt|" +
                "Отчёт для чтения и письма (*.txt)|*.txt|" +
                "Данные в формате JSON — для обработки (*.json)|*.json",
            FilterIndex = 2,
            InitialDirectory = Environment.GetFolderPath(Environment.SpecialFolder.DesktopDirectory),
            AddExtension = true
        };

        if (dialog.ShowDialog(this) != true) return;

        var kind = dialog.FilterIndex switch
        {
            1 => ReportKind.Developer,
            3 => ReportKind.Json,
            _ => ReportKind.Readable
        };

        _viewModel.ExportReport(dialog.FileName, kind);

        // Для подробного отчёта показываем, что в него попало и чего в нём нет:
        // человек отдаёт файл добровольно и должен понимать, что именно отдаёт.
        if (kind != ReportKind.Developer) return;

        var answer = MessageBox.Show(
            "Подробный отчёт сохранён." + Environment.NewLine + Environment.NewLine +
            DeveloperReport.DescribeContents() + Environment.NewLine + Environment.NewLine +
            "Отправить его автору сейчас? Откроется страница, где нужно приложить файл.",
            "Отчёт сохранён", MessageBoxButton.YesNo, MessageBoxImage.Information);

        if (answer == MessageBoxResult.Yes) OnReportProblemClick(sender, e);
    }

    /// <summary>
    /// Сообщить о проблеме. Открывает форму на GitHub, где уже подставлена версия
    /// программы: человеку меньше писать, а мне сразу видно, какая это сборка.
    ///
    /// Ссылку кладём ещё и в буфер обмена. Это не мелочь: у части людей браузер
    /// по умолчанию настроен так, что ссылки из программ не открываются. Тогда
    /// человек хотя бы вставит её вручную, а не останется ни с чем.
    /// </summary>
    private void OnReportProblemClick(object sender, RoutedEventArgs e)
    {
        var url = FeedbackLinks.NewIssueUrl();

        try { System.Windows.Clipboard.SetText(url); }
        catch { /* буфер обмена может быть занят другой программой */ }

        // Пробуем открыть обычным способом, а если не вышло — перебираем браузеры.
        // У первого пользователя браузер по умолчанию падал на любой ссылке,
        // поэтому «просто открыть» здесь недостаточно.
        var openedWith = BrowserLauncher.Open(url);

        if (openedWith is not null)
        {
            MessageBox.Show(
                "Открыл страницу в браузере: " + openedWith + "." +
                Environment.NewLine + Environment.NewLine +
                "Чтобы разобраться быстро:" + Environment.NewLine +
                "  1. Опишите, что случилось и что вы делали." + Environment.NewLine +
                "  2. Нажмите «Отчёт разработчику» и приложите файл к сообщению." +
                Environment.NewLine + Environment.NewLine +
                "Версию программы я подставил за вас." + Environment.NewLine +
                "Ссылка скопирована в буфер обмена — пригодится, если страница не открылась.",
                "Что-то не работает?", MessageBoxButton.OK, MessageBoxImage.Information);

            return;
        }

        // Не открылось ничего: даём выбрать браузер вручную.
        OfferBrowserChoice(url);
    }

    /// <summary>
    /// Открыть ссылку в выбранном вручную браузере. Нужно, когда браузер
    /// по умолчанию не работает, а остальные программа не смогла запустить сама.
    /// </summary>
    private void OfferBrowserChoice(string url)
    {
        var browsers = BrowserLauncher.FindInstalled();

        if (browsers.Count == 0)
        {
            MessageBox.Show(
                "Не удалось открыть браузер: в системе не найдено ни одного." +
                Environment.NewLine + Environment.NewLine +
                "Ссылка скопирована в буфер обмена. Вставьте её в адресную строку " +
                "любого браузера:" + Environment.NewLine + Environment.NewLine + url,
                "Что-то не работает?", MessageBoxButton.OK, MessageBoxImage.Information);

            return;
        }

        var window = new BrowserChoiceWindow(url, browsers) { Owner = this };
        window.ShowDialog();
    }

    /// <summary>
    /// Кнопка благодарности. Программа бесплатная, поэтому это не «покупка»
    /// и не напоминание при запуске — только окно с реквизитами по желанию.
    /// </summary>
    private void OnDonateClick(object sender, RoutedEventArgs e)
    {
        try
        {
            var window = new DonationWindow { Owner = this };
            window.ShowDialog();
        }
        catch (Exception ex)
        {
            MessageBox.Show("Не удалось открыть окно благодарности: " + ex.Message,
                "Поблагодарить разработчиков", MessageBoxButton.OK, MessageBoxImage.Information);
        }
    }
}
