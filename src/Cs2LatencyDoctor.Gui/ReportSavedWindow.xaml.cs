using System.Windows;
using System.Windows.Controls;

namespace Cs2LatencyDoctor.Gui;

/// <summary>
/// Окно «отчёт сохранён»: показывает путь к файлу и даёт его скопировать.
///
/// Почему только копирование и никаких кнопок «открыть папку». Проводник Windows
/// не запускается как дочерний процесс: он завершается сразу с кодом 1, и система
/// при этом показывает окно «Ошибка при запуске приложения (0xc0000142)».
/// Это не сбой отчёта и не сбой программы — так ведёт себя проводник.
///
/// Проверено на этой машине: explorer.exe из программы падает, а notepad.exe
/// и mspaint.exe запускаются нормально. Значит дело именно в проводнике,
/// и обойти это нельзя.
///
/// Показывать человеку «ошибку приложения» из-за необязательной кнопки нельзя:
/// он решит, что сломался отчёт. Поэтому кнопки открытия здесь нет — вместо неё
/// понятное объяснение и копирование пути, которое работает всегда.
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

        // Путь к папке показываем сразу: человеку проще найти папку глазами,
        // чем разбираться с буфером обмена.
        var directory = System.IO.Path.GetDirectoryName(path) ?? path;
        FolderText.Text = "Папка: " + directory;
    }

    /// <summary>Скопировать путь: работает везде и не зависит от проводника.</summary>
    private void OnCopyPathClick(object sender, RoutedEventArgs e)
    {
        try
        {
            Clipboard.SetText(_path);
            CopiedText.Foreground = System.Windows.Media.Brushes.MediumSeaGreen;
            CopiedText.Text = "✓ Путь скопирован. Вставьте его в адресную строку проводника " +
                              "и нажмите Enter — файл откроется.";
        }
        catch
        {
            CopiedText.Foreground = System.Windows.Media.Brushes.Goldenrod;
            CopiedText.Text = "Не удалось скопировать: буфер обмена занят другой программой. " +
                              "Выделите путь выше и скопируйте вручную (Ctrl+C).";
        }

        CopiedText.Visibility = Visibility.Visible;
    }

    /// <summary>Скопировать папку: иногда удобнее вставить папку, а не файл.</summary>
    private void OnCopyFolderClick(object sender, RoutedEventArgs e)
    {
        var directory = System.IO.Path.GetDirectoryName(_path) ?? _path;

        try
        {
            Clipboard.SetText(directory);
            CopiedText.Foreground = System.Windows.Media.Brushes.MediumSeaGreen;
            CopiedText.Text = "✓ Путь к папке скопирован. Вставьте его в адресную строку проводника.";
        }
        catch
        {
            CopiedText.Foreground = System.Windows.Media.Brushes.Goldenrod;
            CopiedText.Text = "Не удалось скопировать: буфер обмена занят другой программой.";
        }

        CopiedText.Visibility = Visibility.Visible;
    }

    private void OnCloseClick(object sender, RoutedEventArgs e) => Close();

    private void OnPathBoxGotFocus(object sender, RoutedEventArgs e)
    {
        // Выделяем путь целиком: так его можно скопировать одним нажатием Ctrl+C.
        PathBox.SelectAll();
    }
}
