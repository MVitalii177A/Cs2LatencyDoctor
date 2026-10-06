using System.IO;

namespace Cs2LatencyDoctor.Gui;

/// <summary>Один вариант поддержки: кошелёк с адресом и QR-кодом.</summary>
public sealed class DonationOption
{
    /// <summary>Заголовок для человека: «USDT», «Bitcoin», «TON».</summary>
    public required string Title { get; init; }

    /// <summary>Сеть или пояснение: «TRC-20 (Tron)», «ERC-20 (Ethereum)».</summary>
    public string Network { get; init; } = string.Empty;

    /// <summary>Адрес кошелька. Пустая строка — вариант ещё не заполнен.</summary>
    public string Address { get; init; } = string.Empty;

    /// <summary>Имя файла картинки с QR-кодом внутри папки Assets.</summary>
    public required string QrFileName { get; init; }

    /// <summary>Короткая подсказка под заголовком, если нужна.</summary>
    public string? Note { get; init; }

    public bool HasAddress => !string.IsNullOrWhiteSpace(Address);

    /// <summary>Полный путь к картинке рядом с программой.</summary>
    public string QrFullPath => Path.Combine(AppContext.BaseDirectory, "Assets", QrFileName);

    public bool QrExists => File.Exists(QrFullPath);

    /// <summary>Можно ли показать вариант с пользой.</summary>
    public bool IsUsable => HasAddress || QrExists;

    public string NetworkText => string.IsNullOrWhiteSpace(Network) ? string.Empty : "Сеть: " + Network;
}

/// <summary>
/// Реквизиты для благодарности разработчикам.
///
/// КАК ДОБАВИТЬ НОВЫЙ КОШЕЛЁК:
///   1. Положите картинку с QR-кодом в папку Assets, например Assets\usdt-trc20.png
///   2. Добавьте запись в список Options ниже и укажите тот же QrFileName.
/// Порядок записей = порядок блоков в окне.
///
/// Пока адрес пустой, блок показывается, но кнопка копирования отключена —
/// так видно, что вариант есть, а реквизит ещё не вписан.
/// </summary>
public static class DonationInfo
{
    /// <summary>Ссылка на страницу DonationAlerts. Пусто — кнопка будет неактивна.</summary>
    public const string DonationAlertsUrl = "";

    /// <summary>Варианты поддержки. Добавляйте сколько нужно.</summary>
    public static readonly IReadOnlyList<DonationOption> Options = new[]
    {
        new DonationOption
        {
            Title = "USDT",
            Network = "ERC-20 (Ethereum)",
            Address = "0xBA8E25ABbfe6182F2CB0b7e19925f0406ecFC8DE",
            QrFileName = "usdt-erc20.png",
            Note = "Сеть Ethereum: комиссия за перевод выше, чем у TRC-20 и BEP-20"
        }

        // Второй кошелёк добавляется так — раскомментируйте и заполните:
        //,
        //new DonationOption
        //{
        //    Title = "USDT",
        //    Network = "TRC-20 (Tron)",
        //    Address = "T...",
        //    QrFileName = "usdt-trc20.png",
        //    Note = "Самая низкая комиссия сети из распространённых"
        //}
    };

    public static bool HasDonationAlerts => !string.IsNullOrWhiteSpace(DonationAlertsUrl);

    /// <summary>Заполнен ли хотя бы один кошелёк.</summary>
    public static bool HasAnyWallet => Options.Any(o => o.HasAddress);

    /// <summary>Есть ли что показать вообще.</summary>
    public static bool IsConfigured => HasAnyWallet || HasDonationAlerts;

    /// <summary>Сколько кошельков ещё ждут адреса — для честного сообщения в окне.</summary>
    public static int PendingCount => Options.Count(o => !o.HasAddress);
}
