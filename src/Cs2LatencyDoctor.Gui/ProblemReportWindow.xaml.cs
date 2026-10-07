using System;
using System.Windows;
using System.Windows.Controls;
using Cs2LatencyDoctor.Gui;

namespace Cs2LatencyDoctor.Gui;

/// <summary>
/// Окно «Сообщить о проблеме»: показывает ссылку и даёт её скопировать.
///
/// Почему не просто открыть браузер и закрыть вопрос. Открыть браузер из программы
/// надёжно не получается: поведение зависит от того, запущен он уже или нет.
/// Firefox в одном случае стартует, в другом падает; Edge запускается через раз;
/// Chrome выходит сразу, передав ссылку уже открытому окну.
///
/// Проверить, открылась ли страница, программа не может — браузер ей об этом
/// не сообщает. Поэтому она не обещает результат, а даёт ссылку: её видно, её можно
/// скопировать, её можно вставить в браузер руками. Это работает всегда.
/// </summary>
public partial class ProblemReportWindow : Window
{
    private readonly string _url;

    public ProblemReportWindow(string url, string? openedWith)
    {
        _url = url;

        InitializeComponent();

        UrlBox.Text = url;

        // Говорим только то, что знаем: какой браузер программа запустила.
        // Открылась ли в нём страница — ей неизвестно.
        if (!string.IsNullOrWhiteSpace(openedWith))
        {
            OpenResult.Text = "Программа запустила: " + openedWith + "." + Environment.NewLine +
                              "Если страница не открылась или браузер сообщил об ошибке — " +
                              "скопируйте ссылку кнопкой выше и откройте её сами. " +
                              "Это надёжнее: браузер запускается из программы по-разному, " +
                              "а ссылка работает всегда.";
            OpenResult.Foreground = System.Windows.Media.Brushes.MediumSeaGreen;
        }
        else
        {
            OpenResult.Text = "Автоматически запустить браузер не получилось — " +
                              "он не запустился ни один." + Environment.NewLine +
                              "Скопируйте ссылку кнопкой выше и вставьте её в браузер сами.";
            OpenResult.Foreground = System.Windows.Media.Brushes.Goldenrod;
        }

        OpenResult.Visibility = Visibility.Visible;

        // Браузеры для ручного выбора: пригодятся, если обычный способ не сработал.
        var browsers = BrowserLauncher.FindInstalled();

        foreach (var browser in browsers)
        {
            var button = new Button
            {
                Content = browser.Title,
                Padding = new Thickness(12, 6, 12, 6),
                Margin = new Thickness(0, 0, 8, 8),
                Background = new System.Windows.Media.SolidColorBrush(
                    System.Windows.Media.Color.FromRgb(0x2A, 0x2C, 0x34)),
                Foreground = new System.Windows.Media.SolidColorBrush(
                    System.Windows.Media.Color.FromRgb(0xE6, 0xE7, 0xEB)),
                BorderThickness = new Thickness(0),
                Tag = browser
            };

            button.Click += OnOpenInBrowserClick;
            BrowserButtons.Children.Add(button);
        }

        if (browsers.Count == 0) BrowserPanel.Visibility = Visibility.Collapsed;
    }

    /// <summary>Скопировать ссылку: единственный способ, который работает всегда.</summary>
    private void OnCopyClick(object sender, RoutedEventArgs e)
    {
        try
        {
            Clipboard.SetText(_url);
            CopiedText.Foreground = System.Windows.Media.Brushes.MediumSeaGreen;
            CopiedText.Text = "✓ Ссылка скопирована. Вставьте её в адресную строку браузера " +
                              "и нажмите Enter.";
        }
        catch
        {
            CopiedText.Foreground = System.Windows.Media.Brushes.Goldenrod;
            CopiedText.Text = "Не удалось скопировать: буфер обмена занят другой программой. " +
                              "Выделите ссылку выше и скопируйте вручную (Ctrl+C).";
        }

        CopiedText.Visibility = Visibility.Visible;
    }

    /// <summary>Попробовать открыть конкретным браузером — по выбору человека.</summary>
    private void OnOpenInBrowserClick(object sender, RoutedEventArgs e)
    {
        if (sender is not Button button || button.Tag is not InstalledBrowser browser) return;

        try
        {
            // only: открываем именно выбранным браузером, а не тем, что назначен
            // по умолчанию — иначе выбор человека ничего не менял бы.
            var opened = BrowserLauncher.Open(_url, skipShell: true, only: browser);

            OpenResult.Foreground = opened is not null
                ? System.Windows.Media.Brushes.MediumSeaGreen
                : System.Windows.Media.Brushes.Goldenrod;

            OpenResult.Text = opened is not null
                ? "Запустил: " + browser.Title + ". Проверьте, появилось ли окно браузера."
                : "Запустить " + browser.Title + " не удалось. Попробуйте другой браузер " +
                  "или скопируйте ссылку.";
        }
        catch (Exception ex)
        {
            App.WriteError("Открытие браузера", ex);

            OpenResult.Foreground = System.Windows.Media.Brushes.Goldenrod;
            OpenResult.Text = "Не удалось запустить браузер: " + ex.Message;
        }

        OpenResult.Visibility = Visibility.Visible;
    }

    private void OnCloseClick(object sender, RoutedEventArgs e) => Close();

    private void OnUrlBoxGotFocus(object sender, RoutedEventArgs e) => UrlBox.SelectAll();
}
