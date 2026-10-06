using System.Windows;

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
