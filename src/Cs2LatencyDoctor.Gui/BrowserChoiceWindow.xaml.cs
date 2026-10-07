using System.Collections.ObjectModel;
using System.Windows;

namespace Cs2LatencyDoctor.Gui;

/// <summary>
/// Окно выбора браузера.
///
/// Зачем оно нужно. Обычный способ открыть ссылку — попросить Windows — полагается
/// на браузер по умолчанию. Если тот сломан, ссылка не открывается вообще: именно
/// это случилось у первого пользователя, у которого Firefox падал на любой ссылке
/// из программы.
///
/// Здесь показаны браузеры, найденные в системе, и поле с адресом: если не работает
/// ни один, адрес можно скопировать и открыть где угодно ещё.
/// </summary>
public partial class BrowserChoiceWindow : Window
{
    public ObservableCollection<BrowserRow> Browsers { get; } = new();

    public string Url { get; }

    public BrowserChoiceWindow(string url, IReadOnlyList<InstalledBrowser> browsers)
    {
        Url = url;

        foreach (var browser in browsers)
        {
            Browsers.Add(new BrowserRow
            {
                Title = browser.Title,
                Path = browser.ExecutablePath,
                Browser = browser
            });
        }

        InitializeComponent();

        UrlBox.Text = url;
        List.ItemsSource = Browsers;
    }

    /// <summary>Скопировать адрес: на случай, когда ни один браузер не открылся.</summary>
    private void OnCopyUrlClick(object sender, RoutedEventArgs e)
    {
        try
        {
            Clipboard.SetText(Url);
            Title = "Открыть ссылку в браузере — адрес скопирован";
        }
        catch
        {
            // Буфер обмена может быть занят другой программой.
            MessageBox.Show("Не удалось скопировать: буфер обмена занят другой программой.",
                "Копирование", MessageBoxButton.OK, MessageBoxImage.Information);
        }
    }

    /// <summary>
    /// Открыть ссылку выбранным браузером. Результат показываем прямо в строке,
    /// а не отдельным окном: человек видит, какой браузер сработал, и может
    /// попробовать следующий, не закрывая список.
    /// </summary>
    private void OnOpenHereClick(object sender, RoutedEventArgs e)
    {
        if (sender is not System.Windows.Controls.Button button) return;
        if (button.Tag is not BrowserRow row) return;

        if (BrowserLauncher.OpenWith(row.Browser, Url))
        {
            row.Result = "✓ ссылка открыта этим браузером";
            return;
        }

        row.Result = "✗ не удалось запустить — попробуйте другой";
    }

    private void OnCloseClick(object sender, RoutedEventArgs e) => Close();
}
