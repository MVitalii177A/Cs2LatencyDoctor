using System.Windows;
using System.Windows.Controls;

namespace Cs2LatencyDoctor.Gui;

/// <summary>
/// Окно «отчёт сохранён»: показывает путь к файлу, даёт его скопировать
/// и, если получится, открыть папку.
///
/// Почему не просто окно сообщения с кнопкой «открыть папку». Проводник на части
/// машин не запускается из программы: он падает с ошибкой 0xc0000142 ещё до
/// старта окна. Показать это человеку как «ошибку приложения» — значит напугать
/// его без причины: с отчётом всё в порядке, не открылась только папка.
///
/// Поэтому здесь три пути к одному и тому же: скопировать путь одной кнопкой,
/// выделить текст вручную, попробовать открыть папку. Работает хотя бы один —
/// и человек найдёт свой файл.
/// </summary>
public partial class ReportSavedWindow : Window
{
    private readonly string _path;

    public ReportSavedWindow(string path, string? extra)
    {
        _path = path;

        InitializeComponent();

        PathBox.Text = path;

        if (!string.IsNullOrWhiteSpace(extra))
        {
            ExtraText.Text = extra;
            ExtraText.Visibility = Visibility.Visible;
        }
    }

    /// <summary>Скопировать путь: работает везде и не зависит от проводника.</summary>
    private void OnCopyPathClick(object sender, RoutedEventArgs e)
    {
        try
        {
            Clipboard.SetText(_path);
            CopiedText.Text = "✓ Путь скопирован. Вставьте его в адресную строку проводника.";
            CopiedText.Visibility = Visibility.Visible;
        }
        catch
        {
            CopiedText.Text = "Не удалось скопировать: буфер обмена занят другой программой. " +
                              "Выделите путь выше и скопируйте вручную (Ctrl+C).";
            CopiedText.Visibility = Visibility.Visible;
        }
    }

    /// <summary>
    /// Попробовать открыть папку. Если проводник не запускается — говорим об этом
    /// спокойно и сразу даём выход: путь уже можно скопировать.
    ///
    /// Окно не закрываем даже при успехе: человеку может понадобиться ещё раз
    /// скопировать путь или прочитать, что в отчёте.
    /// </summary>
    private void OnOpenFolderClick(object sender, RoutedEventArgs e)
    {
        if (ReportLauncher.TryOpenFolder(_path, out var error))
        {
            CopiedText.Text = "Открываю папку… Если окно проводника не появилось, " +
                              "скопируйте путь кнопкой выше — это работает всегда.";
            CopiedText.Visibility = Visibility.Visible;
            return;
        }

        CopiedText.Text = "Проводник не открылся" +
                          (string.IsNullOrWhiteSpace(error) ? "." : ": " + error + ".") +
                          " С отчётом всё в порядке — скопируйте путь кнопкой выше " +
                          "и вставьте его в адресную строку проводника.";
        CopiedText.Visibility = Visibility.Visible;
    }

    private void OnCloseClick(object sender, RoutedEventArgs e) => Close();

    private void OnPathBoxGotFocus(object sender, RoutedEventArgs e)
    {
        // Выделяем путь целиком: так его можно скопировать одним нажатием Ctrl+C.
        PathBox.SelectAll();
    }
}
