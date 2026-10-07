using System.IO;
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
    /// Сохранить отчёт. Отчёт один, а видов записи три — человек выбирает вид,
    /// а место сохранения задано заранее.
    ///
    /// Почему нет окна выбора файла. Раньше здесь было системное окно Windows,
    /// и оно падало: не в нашем коде, а внутри самой системы, с кодом 0xc0000409
    /// в библиотеке ucrtbase.dll. Такое падение нельзя поймать обработчиком
    /// исключений — процесс завершается мгновенно, и человек видит только
    /// исчезнувшее окно.
    ///
    /// Проверено на простейшей программе из тридцати строк: только системное окно
    /// и ничего больше — падает так же. Значит дело в самом окне, и единственный
    /// надёжный выход — его не показывать.
    ///
    /// Заодно так удобнее: не надо думать, куда сохранить, а файл оказывается
    /// в предсказуемом месте — в папке «отчёты» рядом с программой.
    /// </summary>
    private void OnExportClick(object sender, RoutedEventArgs e)
    {
        try
        {
            var now = DateTimeOffset.Now;

            // Спрашиваем вид отчёта обычным окном сообщения: оно системное,
            // но простое и падать ему не с чего.
            var choice = MessageBox.Show(
                "Какой отчёт сохранить?" + Environment.NewLine + Environment.NewLine +
                "«Да» — подробный, для отправки автору: всё нужное для разбора проблемы," +
                Environment.NewLine + "          включая состояние программы." + Environment.NewLine +
                "«Нет» — читаемый текст для письма или форума." + Environment.NewLine +
                "«Отмена» — данные в формате JSON, для обработки скриптом." +
                Environment.NewLine + Environment.NewLine +
                "Файл сохранится в папку «отчёты» рядом с программой." +
                Environment.NewLine +
                "Программа никуда его не отправляет — отправите сами, если захотите.",
                "Сохранить отчёт",
                MessageBoxButton.YesNoCancel, MessageBoxImage.Question);

            if (choice == MessageBoxResult.Cancel && !ConfirmJson()) return;

            var kind = choice switch
            {
                MessageBoxResult.Yes => ReportKind.Developer,
                MessageBoxResult.No => ReportKind.Readable,
                _ => ReportKind.Json
            };

            var extension = kind == ReportKind.Readable ? "txt" : "json";
            var path = ReportExporter.SuggestFullPath(now, extension);

            _viewModel.ExportReport(path, kind);

            // Показываем окно с путём, а не просто сообщение: путь нужно уметь
            // скопировать. Проводник на части машин не запускается из программы,
            // и копирование пути — единственный способ, который работает всегда.
            //
            // Для подробного отчёта добавляем описание: человек отдаёт файл
            // добровольно и должен понимать, что именно отдаёт.
            var extra = kind == ReportKind.Developer ? DeveloperReport.DescribeContents() : null;

            new ReportSavedWindow(path, extra) { Owner = this }.ShowDialog();
        }
        catch (Exception ex)
        {
            // Этот обработчик был единственным без защиты, и при сбое программа
            // закрывалась молча — человек видел только исчезнувшее окно.
            App.WriteError("Сохранение отчёта", ex);

            MessageBox.Show(
                "Не удалось сохранить отчёт." + Environment.NewLine + Environment.NewLine +
                ex.GetType().Name + ": " + ex.Message + Environment.NewLine + Environment.NewLine +
                "Подробности записаны в файл:" + Environment.NewLine + App.ErrorLogPath +
                Environment.NewLine + Environment.NewLine +
                "Покажите этот файл автору — по нему видно причину.",
                "Ошибка сохранения", MessageBoxButton.OK, MessageBoxImage.Warning);
        }
    }

    /// <summary>
    /// Уточнение перед сохранением JSON: «Отмена» в вопросе выбора означает именно
    /// этот формат, и человеку стоит подтвердить, что он понял правильно.
    /// </summary>
    private bool ConfirmJson()
    {
        var answer = MessageBox.Show(
            "Сохранить данные в формате JSON?" + Environment.NewLine + Environment.NewLine +
            "Такой файл нужен для обработки программой, а не для чтения глазами." +
            Environment.NewLine +
            "Если вы хотите отправить отчёт автору — выберите «Да» в прошлом окне.",
            "Формат JSON", MessageBoxButton.YesNo, MessageBoxImage.Question);

        return answer == MessageBoxResult.Yes;
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
                "  2. Приложите файл отчёта, если сохранили его кнопкой «Сохранить отчёт»." +
                Environment.NewLine + Environment.NewLine +
                "Версию программы я подставил за вас." + Environment.NewLine +
                "Ссылка скопирована в буфер обмена — пригодится, если страница не открылась.",
                "Сообщить о проблеме", MessageBoxButton.OK, MessageBoxImage.Information);

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
                "Сообщить о проблеме", MessageBoxButton.OK, MessageBoxImage.Information);

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
