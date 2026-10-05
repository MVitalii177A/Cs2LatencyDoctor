using System.Diagnostics;
using System.IO;
using System.Windows;
using System.Windows.Media.Imaging;

namespace Cs2LatencyDoctor.Gui;

/// <summary>
/// Окно благодарности разработчикам: адрес кошелька USDT с QR-кодом и ссылка
/// на DonationAlerts. Ничего никуда не отправляет и не требует — только показывает
/// реквизиты, если человек сам захочет поддержать.
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
        // ------------------------------------------------------------- USDT
        if (DonationInfo.HasUsdt)
        {
            AddressText.Text = DonationInfo.UsdtAddress;
            UsdtNetworkText.Text = "Сеть: " + DonationInfo.UsdtNetwork;
        }
        else
        {
            AddressText.Text = "Адрес пока не указан";
            UsdtNetworkText.Text = "Сеть: " + DonationInfo.UsdtNetwork;
            CopyAddressButton.IsEnabled = false;
        }

        // QR-код берём готовой картинкой: её рисует сам кошелёк или сервис,
        // так надёжнее, чем строить код самостоятельно.
        if (DonationInfo.QrImageExists)
        {
            try
            {
                var bitmap = new BitmapImage();
                bitmap.BeginInit();
                bitmap.UriSource = new Uri(DonationInfo.QrImagePath);
                bitmap.CacheOption = BitmapCacheOption.OnLoad; // чтобы файл не был занят
                bitmap.EndInit();

                QrImage.Source = bitmap;
            }
            catch (Exception ex)
            {
                ShowQrProblem("Картинку QR не удалось открыть: " + ex.Message);
            }
        }
        else
        {
            ShowQrProblem("Файл с QR-кодом не найден. Положите картинку рядом с программой: " +
                          DonationInfo.QrImageRelativePath);
        }

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

        StatusText.Text = DonationInfo.IsConfigured
            ? "Спасибо, что пользуетесь программой. Поддержка не обязательна."
            : "Реквизиты ещё не заполнены разработчиком — поддержать пока нельзя. " +
              "Это не мешает работе программы.";
    }

    private void ShowQrProblem(string message)
    {
        QrFrame.Visibility = Visibility.Collapsed;
        QrMissingText.Visibility = Visibility.Visible;
        QrMissingText.Text = message;
    }

    private void OnCopyAddressClick(object sender, RoutedEventArgs e)
    {
        if (!DonationInfo.HasUsdt) return;

        try
        {
            Clipboard.SetText(DonationInfo.UsdtAddress);
            StatusText.Text = "Адрес скопирован в буфер обмена. Не забудьте проверить сеть: " +
                              DonationInfo.UsdtNetwork;
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
