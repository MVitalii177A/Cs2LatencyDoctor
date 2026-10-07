using System.Windows;
using System.Windows.Controls;

namespace Cs2LatencyDoctor.Gui;

/// <summary>
/// Открытие отчёта и папки с ним.
///
/// Проводник на части машин не запускается из программы: падает с ошибкой
/// 0xc0000142 ещё до появления окна. Это не поломка отчёта и не поломка программы —
/// так ведёт себя система. Поэтому открытие папки считается необязательным
/// удобством, а основной путь к файлу — копирование пути в буфер обмена:
/// оно работает всегда и ни от чего не зависит.
/// </summary>
public static class ReportLauncher
{
    /// <summary>
    /// Попробовать открыть папку с файлом и показать сам файл.
    /// Возвращает false с текстом ошибки, если не вышло.
    /// </summary>
    public static bool TryOpenFolder(string filePath, out string? error)
    {
        error = null;

        string? directory;

        try
        {
            directory = System.IO.Path.GetDirectoryName(filePath);
        }
        catch (Exception ex)
        {
            error = ex.Message;
            return false;
        }

        if (string.IsNullOrWhiteSpace(directory) || !System.IO.Directory.Exists(directory))
        {
            error = "папка не найдена";
            return false;
        }

        // Способ 1: попросить проводник показать файл. Он подсветит нужный файл
        // в списке — это удобнее, чем просто открыть папку.
        if (TryStart("explorer.exe", "/select,\"" + filePath + "\"", out var first)) return true;

        // Способ 2: открыть папку без выделения файла. Первый способ может
        // не сработать, если путь очень длинный.
        if (TryStart("explorer.exe", "\"" + directory + "\"", out var second)) return true;

        // Способ 3: попросить систему открыть папку как оболочку.
        if (TryStart(directory, string.Empty, out var third)) return true;

        error = first ?? second ?? third;
        return false;
    }

    /// <summary>
    /// Запустить проводник и понять, получилось ли.
    ///
    /// Код возврата проводника здесь НЕ проверяется намеренно. Он ненадёжен:
    /// когда проводник уже открыт, новый запуск просто передаёт ему команду
    /// и завершается — иногда с нулём, иногда с единицей. Считать единицу
    /// ошибкой значило бы говорить «папка не открылась» там, где она открылась.
    ///
    /// Поэтому проверяем только одно: процесс удалось создать или нет.
    /// Если проводник сразу упал — это увидит и человек, а окно с путём
    /// останется открытым, и путь можно скопировать.
    /// </summary>
    private static bool TryStart(string fileName, string arguments, out string? error)
    {
        error = null;

        try
        {
            var info = new System.Diagnostics.ProcessStartInfo
            {
                FileName = fileName,
                Arguments = arguments,
                UseShellExecute = true
            };

            using var process = System.Diagnostics.Process.Start(info);

            if (process is null)
            {
                error = "не удалось запустить проводник";
                return false;
            }

            return true;
        }
        catch (Exception ex)
        {
            error = ex.Message;
            return false;
        }
    }
}
