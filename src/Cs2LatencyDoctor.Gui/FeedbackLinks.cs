using System.Diagnostics;

namespace Cs2LatencyDoctor.Gui;

/// <summary>
/// Ссылки на страницы проекта: где сообщить об ошибке и где почитать описание.
///
/// Зачем это в программе. Человек, у которого что-то не работает, не пойдёт искать
/// репозиторий на сайте — он закроет программу и не скажет ничего. Тогда ошибка
/// останется ненайденной, а программа — хуже, чем могла бы быть.
/// Поэтому кнопка должна быть в самом окне, и нажатие должно сразу открывать
/// форму, где уже подставлена версия программы.
/// </summary>
public static class FeedbackLinks
{
    /// <summary>Адрес репозитория.</summary>
    public const string RepositoryUrl = "https://github.com/MVitalii177A/Cs2LatencyDoctor";

    /// <summary>Форма нового сообщения об ошибке с уже заполненной версией.</summary>
    public static string NewIssueUrl()
    {
        // В адресе передаём заголовок и метку: человеку меньше писать, а сообщение
        // сразу попадает в нужную категорию. Тело не заполняем — шаблон сам
        // задаст нужные вопросы.
        var title = Uri.EscapeDataString("[Ошибка] ");
        var body = Uri.EscapeDataString(
            "**Версия программы:** " + Core.AppVersion.Display + "\n" +
            "**Какой файл скачал:** \n" +
            "**Версия Windows:** \n\n" +
            "**Что случилось:**\n\n\n" +
            "**Что должно было быть:**\n\n\n" +
            "**Файл отчёта прикреплён:** нет\n");

        return $"{RepositoryUrl}/issues/new?labels={Uri.EscapeDataString("ошибка")}" +
               $"&title={title}&body={body}";
    }

    /// <summary>Все открытые сообщения: посмотреть перед отправкой, нет ли уже такого.</summary>
    public static string IssuesUrl() => RepositoryUrl + "/issues";

    /// <summary>Описание программы.</summary>
    public static string ReadmeUrl() => RepositoryUrl + "/blob/main/README.md";

    /// <summary>
    /// Попытка открыть ссылку. useShell: true — обычный способ через оболочку Windows,
    /// false — через explorer.exe, который вызывает оболочку по-другому.
    ///
    /// Второй способ нужен потому, что первый зависит от браузера по умолчанию:
    /// если он настроен неправильно, ссылка не откроется вообще.
    /// </summary>
    public static bool TryOpen(string url, bool useShell)
    {
        try
        {
            var info = new ProcessStartInfo
            {
                FileName = useShell ? url : "explorer.exe",
                Arguments = useShell ? string.Empty : url,
                UseShellExecute = true
            };

            Process.Start(info);
            return true;
        }
        catch
        {
            return false;
        }
    }

    /// <summary>Открыть ссылку, попробовав оба способа. Возвращает false, если не вышло.</summary>
    public static bool Open(string url) =>
        TryOpen(url, useShell: true) || TryOpen(url, useShell: false);
}
