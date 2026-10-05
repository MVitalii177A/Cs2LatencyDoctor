using System.IO;

namespace Cs2LatencyDoctor.Gui;

/// <summary>
/// Реквизиты для благодарности разработчикам.
///
/// ЧТО СЮДА ВПИСАТЬ — два места, помечены «ВСТАВИТЬ»:
///
/// 1. UsdtAddress — адрес кошелька USDT.
///    Положите рядом с программой картинку с QR-кодом этого кошелька:
///    Assets\usdt-qr.png  (имя файла обязательно такое).
///    Сеть указывайте ту, для которой сделан QR: перепутанная сеть = потерянные деньги.
///
/// 2. DonationAlertsUrl — ссылка на страницу DonationAlerts.
///
/// Пока значения пустые, кнопка благодарности открывает окно и честно говорит,
/// что реквизиты ещё не указаны — ничего не ломается.
/// </summary>
public static class DonationInfo
{
    /// <summary>ВСТАВИТЬ: адрес кошелька USDT (например TRC-20 начинается с T, ERC-20/BEP-20 с 0x).</summary>
    public const string UsdtAddress = "";

    /// <summary>ВСТАВИТЬ: сеть кошелька. Пишется рядом с адресом, чтобы не перепутали.</summary>
    public const string UsdtNetwork = "TRC-20 (Tron)";

    /// <summary>ВСТАВИТЬ: ссылка вида https://www.donationalerts.com/r/ваш_ник</summary>
    public const string DonationAlertsUrl = "";

    /// <summary>Путь к картинке с QR-кодом кошелька относительно папки программы.</summary>
    public const string QrImageRelativePath = @"Assets\usdt-qr.png";

    public static bool HasUsdt => !string.IsNullOrWhiteSpace(UsdtAddress);
    public static bool HasDonationAlerts => !string.IsNullOrWhiteSpace(DonationAlertsUrl);

    /// <summary>Полный путь к картинке QR рядом с исполняемым файлом.</summary>
    public static string QrImagePath =>
        Path.Combine(AppContext.BaseDirectory, QrImageRelativePath);

    public static bool QrImageExists => File.Exists(QrImagePath);

    /// <summary>Всё ли готово, чтобы показать окно с пользой.</summary>
    public static bool IsConfigured => HasUsdt || HasDonationAlerts;
}
