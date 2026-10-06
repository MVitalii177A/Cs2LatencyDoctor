using System.Diagnostics;
using System.IO;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Media;
using System.Windows.Media.Animation;
using System.Windows.Media.Effects;
using System.Windows.Media.Imaging;
using System.Windows.Threading;

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
    /// Наведение на карточку кошелька: карточка заметно растёт, её рамка
    /// подсвечивается, а сзади загорается зелёное свечение.
    ///
    /// Рост сделан через RenderTransform — он не влияет на раскладку,
    /// поэтому соседние карточки не сдвигаются.
    /// </summary>
    private void OnCardMouseEnter(object sender, System.Windows.Input.MouseEventArgs e)
    {
        if (sender is not Border card) return;

        // Наведённая карточка поднимается на верхний слой, иначе её свечение
        // перекрывается соседними карточками.
        //
        // Поднимать надо ЯЧЕЙКУ СПИСКА, а не саму карточку: соседние карточки
        // лежат не в одной Canvas, а в разных контейнерах ItemsControl, поэтому
        // ZIndex внутри карточки на порядок отрисовки не влияет. Проверено
        // замером: без этого свечение справа от средней карточки не рисовалось.
        RaiseCard(card, top: true);

        // Свечение тусклее и короче: мягкая подсветка, а не яркое пятно.
        AnimateCard(card, CardHoverScale, 1.0, glowRadius: 38, glowOpacity: 0.55);
    }

    private void OnCardMouseLeave(object sender, System.Windows.Input.MouseEventArgs e)
    {
        if (sender is not Border card) return;

        AnimateCard(card, 1.0, 0.0, glowRadius: 0, glowOpacity: 0);

        // Опускаем ячейку обратно только после того, как свечение погаснет:
        // иначе на середине затухания ореол резко уйдёт под соседнюю карточку.
        var timer = new DispatcherTimer { Interval = TimeSpan.FromMilliseconds(160) };
        timer.Tick += (_, _) =>
        {
            timer.Stop();
            RaiseCard(card, top: false);
        };
        timer.Start();
    }

    /// <summary>
    /// Поднять или опустить ячейку списка, в которой лежит карточка.
    ///
    /// Подниматься надо именно до ячейки, созданной ItemsControl: соседние
    /// карточки лежат в разных контейнерах, поэтому ZIndex внутри карточки
    /// на порядок отрисовки не влияет.
    ///
    /// Начинаем с родителя карточки. Если начать с самой карточки, первым
    /// найдётся её собственный ContentPresenter — тот, что показывает внутри
    /// неё StackPanel, — и ZIndex уйдёт не туда. Проверено замером: из-за
    /// этого свечение продолжало уходить под соседнюю карточку.
    /// </summary>
    private static void RaiseCard(Border card, bool top)
    {
        try
        {
            System.Windows.DependencyObject? node = VisualTreeHelper.GetParent(card);

            // Ищем ячейку, которая лежит внутри панели списка.
            while (node is not null)
            {
                if (node is ContentPresenter presenter &&
                    VisualTreeHelper.GetParent(presenter) is Panel)
                {
                    Panel.SetZIndex(presenter, top ? 100 : 1);
                    return;
                }

                node = VisualTreeHelper.GetParent(node);
            }
        }
        catch
        {
            // Если дерево ещё не построено — не критично, порядок останется прежним.
        }
    }

    /// <summary>Во сколько раз растёт карточка при наведении.</summary>
    private const double CardHoverScale = 1.10;

    /// <summary>Размеры карточки в покое. При наведении растут на CardHoverScale.</summary>
    private const double CardIdleWidth = 232;
    private const double CardIdleHeight = 510;

    private static void AnimateCard(Border card, double scale, double borderOpacity,
        double glowRadius, double glowOpacity)
    {
        var duration = TimeSpan.FromMilliseconds(130);
        var easing = new CubicEase { EasingMode = EasingMode.EaseOut };

        // --- рост карточки ---
        // Растут Ширина и Высота, а не масштаб. Так содержимое перестраивается
        // под новый размер и пропорции остаются правильными. При масштабе рамка
        // осталась бы прежней, а содержимое вылезло за её края — карточка
        // выглядела сломанной.
        //
        // Место под увеличенный размер отведено заранее (ячейка Canvas),
        // поэтому соседние карточки не сдвигаются. И побочно: эффект свечения
        // WPF считает по фактическим границам, поэтому ореол остаётся целым.
        card.BeginAnimation(FrameworkElement.WidthProperty,
            new DoubleAnimation(CardIdleWidth * scale, duration) { EasingFunction = easing });
        card.BeginAnimation(FrameworkElement.HeightProperty,
            new DoubleAnimation(CardIdleHeight * scale, duration) { EasingFunction = easing });

        // --- подсветка рамки ---
        if (card.BorderBrush is SolidColorBrush)
        {
            // Тёплый оранжевый — в тон свечению.
            var colour = new ColorAnimation(
                borderOpacity > 0 ? Color.FromRgb(0xFF, 0x8A, 0x24) : Colors.Transparent, duration)
            {
                EasingFunction = easing
            };

            card.BorderBrush = new SolidColorBrush(Colors.Transparent);
            card.BorderBrush.BeginAnimation(SolidColorBrush.ColorProperty, colour);
        }

        // --- свечение сзади ---
        // Анимируются только радиус и прозрачность: анимация самих параметров
        // эффекта дешевле, чем подмена эффекта на каждый кадр.
        AnimateGlow(card.Effect as DropShadowEffect, glowRadius, glowOpacity, duration, easing);

        // Узкое ядро у краёв: размытие меньше, поэтому у карточки цвет теплее.
        var core = (card.Child as FrameworkElement)?.Effect as DropShadowEffect;
        AnimateGlow(core, glowRadius * 0.45, glowOpacity * 0.9, duration, easing);
    }

    /// <summary>Плавно перевести слой свечения в заданное состояние.</summary>
    private static void AnimateGlow(DropShadowEffect? glow, double radius, double opacity,
        TimeSpan duration, IEasingFunction easing)
    {
        if (glow is null) return;

        glow.BeginAnimation(DropShadowEffect.BlurRadiusProperty,
            new DoubleAnimation(radius, duration) { EasingFunction = easing });
        glow.BeginAnimation(DropShadowEffect.OpacityProperty,
            new DoubleAnimation(opacity, duration) { EasingFunction = easing });
    }
}
