using System.IO;
using System.Diagnostics;
using Microsoft.Win32;

namespace Cs2LatencyDoctor.Gui;

/// <summary>Браузер, установленный в системе.</summary>
/// <param name="Title">Как называть в списке выбора.</param>
/// <param name="ExecutablePath">Путь к исполняемому файлу.</param>
public sealed record InstalledBrowser(string Title, string ExecutablePath);

/// <summary>
/// Открытие ссылок в браузере.
///
/// Зачем понадобилось выбирать браузер вручную. Обычный способ — попросить Windows
/// открыть адрес — полагается на браузер по умолчанию. Если тот сломан, ссылка
/// не открывается вообще, и человек остаётся ни с чем. Именно так и вышло у первого
/// пользователя: Firefox по умолчанию падал на любой ссылке из программы.
///
/// Поэтому: сначала пробуем как обычно, а если не вышло — перебираем браузеры,
/// установленные в системе, пока какой-нибудь не откроет. Если и это не помогло,
/// даём выбрать вручную и всегда показываем адрес текстом, чтобы его можно было
/// скопировать.
/// </summary>
public static class BrowserLauncher
{
    /// <summary>
    /// Где Windows хранит список браузеров. Ключ один и тот же для программ,
    /// зарегистрированных на этого пользователя и на всех.
    /// </summary>
    private static readonly string[] RegistryPaths =
    {
        @"SOFTWARE\Clients\StartMenuInternet",
        @"SOFTWARE\WOW6432Node\Clients\StartMenuInternet"
    };

    /// <summary>
    /// Браузеры, установленные в системе. Порядок: сначала те, что чаще работают,
    /// потом остальные. Edge в списке всегда — он есть на любой Windows 10 и 11.
    /// </summary>
    public static IReadOnlyList<InstalledBrowser> FindInstalled()
    {
        var found = new List<InstalledBrowser>();
        var seen = new HashSet<string>(StringComparer.OrdinalIgnoreCase);

        void Add(string? title, string? path)
        {
            if (string.IsNullOrWhiteSpace(path) || !File.Exists(path)) return;
            if (!seen.Add(path)) return;

            found.Add(new InstalledBrowser(
                string.IsNullOrWhiteSpace(title) ? Path.GetFileNameWithoutExtension(path) : title.Trim(),
                path));
        }

        // Основной путь: список браузеров, зарегистрированных в Windows.
        foreach (var root in new[] { Registry.LocalMachine, Registry.CurrentUser })
        {
            foreach (var sub in RegistryPaths)
            {
                try
                {
                    using var clients = root.OpenSubKey(sub);
                    if (clients is null) continue;

                    foreach (var name in clients.GetSubKeyNames())
                    {
                        using var client = clients.OpenSubKey(name);
                        if (client is null) continue;

                        var title = client.GetValue(null) as string;

                        using var command = client.OpenSubKey(@"shell\open\command");
                        var raw = command?.GetValue(null) as string;

                        Add(title ?? name, ExtractPath(raw));
                    }
                }
                catch
                {
                    // Раздел недоступен: пробуем следующий.
                }
            }
        }

        // Запасной путь: пути, по которым браузеры стоят почти всегда.
        // Нужен, если раздел реестра пуст или недоступен.
        var known = new (string Title, string Path)[]
        {
            ("Microsoft Edge", @"C:\Program Files (x86)\Microsoft\Edge\Application\msedge.exe"),
            ("Microsoft Edge", @"C:\Program Files\Microsoft\Edge\Application\msedge.exe"),
            ("Google Chrome", @"C:\Program Files\Google\Chrome\Application\chrome.exe"),
            ("Google Chrome", @"C:\Program Files (x86)\Google\Chrome\Application\chrome.exe"),
            ("Mozilla Firefox", @"C:\Program Files\Mozilla Firefox\firefox.exe"),
            ("Mozilla Firefox", @"C:\Program Files (x86)\Mozilla Firefox\firefox.exe"),
            ("Opera", Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData),
                @"Programs\Opera\launcher.exe")),
            ("Яндекс.Браузер", Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData),
                @"Yandex\YandexBrowser\Application\browser.exe")),
            ("Brave", @"C:\Program Files\BraveSoftware\Brave-Browser\Application\brave.exe")
        };

        foreach (var (title, path) in known) Add(title, path);

        // Edge первым: он есть везде и почти никогда не сломан. Если браузер
        // по умолчанию падает, именно он спасёт ситуацию.
        return found
            .OrderByDescending(b => b.Title.Contains("Edge", StringComparison.OrdinalIgnoreCase))
            .ThenBy(b => b.Title, StringComparer.CurrentCulture)
            .ToList();
    }

    /// <summary>Разобрать строку команды из реестра: «"C:\...\browser.exe" --flags» → путь.</summary>
    private static string? ExtractPath(string? command)
    {
        if (string.IsNullOrWhiteSpace(command)) return null;

        var text = command.Trim();

        // Путь почти всегда в кавычках: в нём бывают пробелы.
        if (text.StartsWith('"'))
        {
            var end = text.IndexOf('"', 1);
            return end > 1 ? text[1..end] : null;
        }

        // Без кавычек: берём всё до первого ключа, начинающегося с дефиса.
        var parts = text.Split(' ', StringSplitOptions.RemoveEmptyEntries);
        return parts.Length > 0 ? parts[0] : null;
    }

    /// <summary>
    /// Открыть ссылку конкретным браузером. Возвращает false, если запустить не удалось.
    /// </summary>
    public static bool OpenWith(InstalledBrowser browser, string url)
    {
        try
        {
            Process.Start(new ProcessStartInfo
            {
                FileName = browser.ExecutablePath,
                Arguments = url,
                UseShellExecute = false
            });

            return true;
        }
        catch
        {
            return false;
        }
    }

    /// <summary>
    /// Открыть ссылку, действуя по порядку: обычный способ, потом перебор браузеров.
    /// Возвращает название сработавшего браузера или null, если не вышло ничего.
    /// </summary>
    /// <param name="skipShell">
    /// true — не пробовать обычный способ. Нужно, когда человек уже знает,
    /// что браузер по умолчанию не работает, и выбрал конкретный.
    /// </param>
    public static string? Open(string url, bool skipShell = false)
    {
        if (!skipShell)
        {
            // Обычный способ: пусть Windows сама решит, чем открыть.
            try
            {
                Process.Start(new ProcessStartInfo { FileName = url, UseShellExecute = true });
                return "браузер по умолчанию";
            }
            catch
            {
                // Не вышло — переходим к перебору.
            }

            // Второй обычный способ: через проводник. Он вызывает оболочку иначе
            // и иногда срабатывает там, где первый не смог.
            try
            {
                Process.Start(new ProcessStartInfo
                {
                    FileName = "explorer.exe",
                    Arguments = url,
                    UseShellExecute = true
                });

                return "браузер по умолчанию";
            }
            catch
            {
                // Тоже не вышло.
            }
        }

        foreach (var browser in FindInstalled())
        {
            if (OpenWith(browser, url)) return browser.Title;
        }

        return null;
    }
}
