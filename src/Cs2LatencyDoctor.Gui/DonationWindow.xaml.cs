using System.Diagnostics;
using System.IO;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Media.Imaging;

namespace Cs2LatencyDoctor.Gui;

/// <summary>Строка окна благодарности: один кошелёк с QR-кодом и адресом.</summary>
public sealed class DonationRow
{
    public required string Title { get; init; }
    public string NetworkText { get; init; } = string.Empty;
    public string Address { get; init; } = string.Empty;
    public string? Note { get; init; }
    public BitmapImage? QrImage { get; init; }
    public string? ImageProblem { get; init; }

    public bool HasAddress => !string.IsNullOrWhiteSpace(Address);
    public bool HasImage => QrImage is not null;
    public bool HasNote => !string.IsNullOrWhiteSpace(Note);
    public bool HasImageProblem => !string.IsNullOrWhiteSpace(ImageProblem);
}

/// <summary>
/// Окно благодарности разработчикам: кошельки с QR-кодами и ссылка на DonationAlerts.
/// Ничего никуда не отправляет и не требует — только показывает реквизиты,
/// если человек сам захочет поддержать.
/// </summary>
public partial class DonationWindow : Window
{
    public DonationWindow()
    {
        InitializeComponent();
        FillContent();
    }

    private void FillContent()
    {
        WalletList.ItemsSource = DonationInfo.Options.Select(BuildRow).ToList();

        // -------------------------------------------------- DonationAlerts
        if (DonationInfo.HasDonationAlerts)
        {
            DonationAlertsButton.IsEnabled = true;
        }
        else
        {
            DonationAlertsButton.IsEnabled = false;
            DonationAlertsMissingText.Visibility = Visibility.Visible;
            DonationAlertsMissingText.Text =
                "Ссылка на страницу DonationAlerts пока не указана. Если она появится, " +
                "здесь будет кнопка, открывающая страницу в браузере.";
        }

        // ------------------------------------------------------------ статус
        StatusText.Text = DonationInfo.IsConfigured
            ? "Спасибо, что пользуетесь программой. Поддержка не обязательна."
            : "Реквизиты ещё не заполнены разработчиком — поддержать пока нельзя. " +
              "Это не мешает работе программы.";
    }

    private static DonationRow BuildRow(DonationOption option)
    {
        BitmapImage? image = null;
        string? problem = null;

        if (option.QrExists)
        {
            try
            {
                var bitmap = new BitmapImage();
                bitmap.BeginInit();
                bitmap.UriSource = new Uri(option.QrFullPath);

                // OnLoad освобождает файл сразу: картинку можно заменить,
                // не закрывая программу.
                bitmap.CacheOption = BitmapCacheOption.OnLoad;
                bitmap.EndInit();
                bitmap.Freeze();

                image = bitmap;
            }
            catch (Exception ex)
            {
                problem = $"Картинку QR не удалось открыть ({ex.Message}). " +
                          $"Файл: Assets\\{option.QrFileName}";
            }
        }
        else
        {
            problem = "Картинка QR не найдена. Положите её рядом с программой: " +
                      $"Assets\\{option.QrFileName}";
        }

        return new DonationRow
        {
            Title = option.Title,
            NetworkText = option.NetworkText,
            Address = option.HasAddress ? option.Address : "Адрес пока не указан",
            Note = option.Note,
            QrImage = image,
            ImageProblem = problem
        };
    }

    private void OnCopyAddressClick(object sender, RoutedEventArgs e)
    {
        if (sender is not Button button) return;

        var address = button.Tag as string;
        if (string.IsNullOrWhiteSpace(address)) return;

        try
        {
            Clipboard.SetText(address);
            StatusText.Text = "Адрес скопирован в буфер обмена. Проверьте сеть перед переводом.";
        }
        catch (Exception ex)
        {
            StatusText.Text = "Не удалось скопировать адрес: " + ex.Message +
                              ". Выделите адрес в поле и скопируйте вручную (Ctrl+C).";
        }
    }

    private void OnDonationAlertsClick(object sender, RoutedEventArgs e)
    {
        if (!DonationInfo.HasDonationAlerts) return;

        try
        {
            Process.Start(new ProcessStartInfo
            {
                FileName = DonationInfo.DonationAlertsUrl,
                UseShellExecute = true
            });

            StatusText.Text = "Страница открыта в браузере.";
        }
        catch (Exception ex)
        {
            StatusText.Text = "Не удалось открыть браузер: " + ex.Message +
                              ". Ссылка: " + DonationInfo.DonationAlertsUrl;
        }
    }

    private void OnCloseClick(object sender, RoutedEventArgs e) => Close();
}
