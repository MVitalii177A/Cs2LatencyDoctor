using System.Diagnostics;
using System.IO;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Media;
using System.Windows.Media.Animation;
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

        // QR-код страницы: рядом с кнопкой, чтобы можно было навести телефон
        if (DonationInfo.DonationAlertsQrExists)
        {
            var image = LoadQr(DonationInfo.DonationAlertsQrPath);

            if (image is not null)
            {
                DonationAlertsQrImage.Source = image;
                DonationAlertsQrFrame.Visibility = Visibility.Visible;
            }
        }

        // ------------------------------------------------------------ статус
        StatusText.Text = DonationInfo.IsConfigured
            ? "Спасибо, что пользуетесь программой. Поддержка не обязательна."
            : "Реквизиты ещё не заполнены разработчиком — поддержать пока нельзя. " +
              "Это не мешает работе программы.";
    }

    /// <summary>
    /// Загрузить картинку QR так, чтобы файл сразу освобождался:
    /// иначе картинку нельзя заменить, не закрыв программу.
    /// </summary>
    private static BitmapImage? LoadQr(string path)
    {
        try
        {
            var bitmap = new BitmapImage();
            bitmap.BeginInit();
            bitmap.UriSource = new Uri(path);
            bitmap.CacheOption = BitmapCacheOption.OnLoad;
            bitmap.EndInit();
            bitmap.Freeze();

            return bitmap;
        }
        catch
        {
            return null;
        }
    }

    private static DonationRow BuildRow(DonationOption option)
    {
        BitmapImage? image = null;
        string? problem = null;

        if (option.QrExists)
        {
            image = LoadQr(option.QrFullPath);

            if (image is null)
                problem = $"Картинку QR не удалось открыть. Файл: Assets\\{option.QrFileName}";
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

        // Открываем двумя способами: если браузер по умолчанию настроен криво
        // или падает, второй способ обычно срабатывает.
        var opened = TryOpen(DonationInfo.DonationAlertsUrl, useShell: true);

        if (!opened)
            opened = TryOpen(DonationInfo.DonationAlertsUrl, useShell: false);

        StatusText.Text = opened
            ? "Страница открыта в браузере. Если браузер показал сообщение о падении — " +
              "это его собственная проблема, скопируйте ссылку и откройте вручную."
            : "Не удалось запустить браузер. Скопируйте ссылку кнопкой рядом " +
              "и откройте её вручную: " + DonationInfo.DonationAlertsUrl;
    }

    /// <summary>
    /// Попытка открыть ссылку. useShell: true — обычный способ через оболочку Windows,
    /// false — через explorer.exe, который вызывает оболочку по-другому.
    /// </summary>
    private static bool TryOpen(string url, bool useShell)
    {
        try
        {
            var info = new ProcessStartInfo
            {
                FileName = useShell ? url : "explorer.exe",
                UseShellExecute = useShell
            };

            if (!useShell) info.Arguments = '"' + url + '"';

            Process.Start(info);
            return true;
        }
        catch
        {
            return false;
        }
    }

    private void OnCopyLinkClick(object sender, RoutedEventArgs e)
    {
        if (!DonationInfo.HasDonationAlerts) return;

        try
        {
            Clipboard.SetText(DonationInfo.DonationAlertsUrl);
            StatusText.Text = "Ссылка скопирована: " + DonationInfo.DonationAlertsUrl +
                              " — вставьте её в адресную строку браузера (Ctrl+V).";
        }
        catch (Exception ex)
        {
            StatusText.Text = "Не удалось скопировать ссылку: " + ex.Message +
                              ". Ссылка: " + DonationInfo.DonationAlertsUrl;
        }
    }

    private void OnCloseClick(object sender, RoutedEventArgs e) => Close();

    /// <summary>
    /// Наведение на карточку кошелька: карточка плавно растёт, её рамка
    /// подсвечивается. Рост сделан через RenderTransform — он не влияет
    /// на раскладку, поэтому соседние карточки не сдвигаются.
    /// </summary>
    private void OnCardMouseEnter(object sender, System.Windows.Input.MouseEventArgs e)
    {
        if (sender is not Border card) return;

        AnimateCard(card, scale: 1.04, borderBrush: (Brush)FindResource("AccentBrush"));
    }

    private void OnCardMouseLeave(object sender, System.Windows.Input.MouseEventArgs e)
    {
        if (sender is not Border card) return;

        AnimateCard(card, scale: 1.0, borderBrush: (Brush)FindResource("LineBrush"));
    }

    private static void AnimateCard(Border card, double scale, Brush borderBrush)
    {
        var duration = TimeSpan.FromMilliseconds(160);

        // Плавное изменение размера
        if (card.RenderTransform is ScaleTransform transform &&
            transform.IsFrozen == false)
        {
            var easing = new CubicEase { EasingMode = EasingMode.EaseOut };

            var scaleX = new DoubleAnimation(scale, duration) { EasingFunction = easing };
            var scaleY = new DoubleAnimation(scale, duration) { EasingFunction = easing };

            transform.BeginAnimation(ScaleTransform.ScaleXProperty, scaleX);
            transform.BeginAnimation(ScaleTransform.ScaleYProperty, scaleY);
        }

        // Подсветка рамки
        var colour = new ColorAnimation(
            (borderBrush as SolidColorBrush)?.Color ?? Colors.Transparent, duration)
        {
            EasingFunction = new CubicEase { EasingMode = EasingMode.EaseOut }
        };

        card.BorderBrush = new SolidColorBrush(Colors.Transparent);
        card.BorderBrush.BeginAnimation(SolidColorBrush.ColorProperty, colour);
    }
}
