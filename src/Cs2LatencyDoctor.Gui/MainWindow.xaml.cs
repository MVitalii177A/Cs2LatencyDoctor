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
    /// Сохранить отчёт в файл. Человек выбирает место сам; по расширению понятно,
    /// в каком виде сохранять — текст для чтения или JSON для обработки.
    /// </summary>
    private void OnExportClick(object sender, RoutedEventArgs e)
    {
        var dialog = new Microsoft.Win32.SaveFileDialog
        {
            Title = "Сохранить отчёт о проверке",
            FileName = ReportExporter.SuggestFileName(DateTimeOffset.Now, "txt"),
            DefaultExt = ".txt",
            Filter = "Текстовый отчёт (*.txt)|*.txt|Данные в формате JSON (*.json)|*.json",
            InitialDirectory = Environment.GetFolderPath(Environment.SpecialFolder.DesktopDirectory),
            AddExtension = true
        };

        if (dialog.ShowDialog(this) != true) return;

        var asJson = dialog.FileName.EndsWith(".json", StringComparison.OrdinalIgnoreCase);
        _viewModel.ExportReport(dialog.FileName, asJson);
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

        if (FeedbackLinks.Open(url))
        {
            MessageBox.Show(
                "Открыл страницу, где можно написать о проблеме.\n\n" +
                "Чтобы разобраться быстро:\n" +
                "  1. Опишите, что случилось и что вы делали.\n" +
                "  2. Нажмите «Сохранить отчёт» и приложите файл к сообщению.\n\n" +
                "Версию программы я подставил за вас.\n" +
                "Ссылка скопирована в буфер обмена — если страница не открылась, " +
                "вставьте её в браузер вручную.",
                "Что-то не работает?", MessageBoxButton.OK, MessageBoxImage.Information);

            return;
        }

        MessageBox.Show(
            "Не удалось открыть браузер.\n\n" +
            "Ссылка скопирована в буфер обмена — вставьте её в адресную строку браузера:\n\n" +
            url,
            "Что-то не работает?", MessageBoxButton.OK, MessageBoxImage.Information);
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
