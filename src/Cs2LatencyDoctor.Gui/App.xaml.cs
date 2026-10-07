using System.IO;
using System.Text;
using System.Windows;
using System.Windows.Threading;

namespace Cs2LatencyDoctor.Gui;

/// <summary>
/// Точка входа программы.
///
/// Кроме запуска окна здесь есть перехват необработанных ошибок. Это не украшение:
/// без него падение выглядит как «программа просто закрылась», и понять причину
/// невозможно — ни текста, ни следа. Именно так и случилось один раз: нажатие
/// кнопки закрывало программу без единого слова.
///
/// Теперь любая необработанная ошибка записывается в файл рядом с программой,
/// и человек может показать этот файл. Ошибка в интерфейсе при этом не роняет
/// программу: показываем сообщение и продолжаем работу.
/// </summary>
public partial class App : Application
{
    /// <summary>Файл с описанием последней ошибки. Лежит рядом с программой.</summary>
    public static string ErrorLogPath =>
        Path.Combine(AppContext.BaseDirectory, "ошибки.txt");

    protected override void OnStartup(StartupEventArgs e)
    {
        // Ошибки в потоке интерфейса: не даём программе закрыться молча.
        DispatcherUnhandledException += (_, args) =>
        {
            WriteError("Ошибка в окне программы", args.Exception);

            MessageBox.Show(
                "Произошла ошибка, но программа продолжает работать." + Environment.NewLine +
                Environment.NewLine +
                Describe(args.Exception) + Environment.NewLine + Environment.NewLine +
                "Подробности записаны в файл:" + Environment.NewLine + ErrorLogPath +
                Environment.NewLine + Environment.NewLine +
                "Покажите этот файл автору — по нему видно причину." +
                Environment.NewLine +
                "Кнопка «Сообщить о проблеме» откроет страницу, куда его приложить.",
                "Ошибка", MessageBoxButton.OK, MessageBoxImage.Warning);

            // Помечаем как обработанную: иначе программа закроется.
            args.Handled = true;
        };

        // Ошибки в фоновых потоках: их тоже нельзя терять.
        AppDomain.CurrentDomain.UnhandledException += (_, args) =>
        {
            if (args.ExceptionObject is Exception ex) WriteError("Ошибка в фоновом потоке", ex);
        };

        // Ошибки в задачах, результат которых никто не ждёт.
        TaskScheduler.UnobservedTaskException += (_, args) =>
        {
            WriteError("Ошибка в задаче", args.Exception);
            args.SetObserved();
        };

        base.OnStartup(e);
    }

    /// <summary>Короткое понятное описание ошибки: тип и текст, без внутренностей.</summary>
    private static string Describe(Exception ex) =>
        ex.GetType().Name + ": " + ex.Message;

    /// <summary>
    /// Записать ошибку в файл. Файл дописывается: если ошибок несколько,
    /// видно последовательность, а не только последнюю.
    /// </summary>
    public static void WriteError(string where, Exception ex)
    {
        try
        {
            var text = new StringBuilder();

            text.AppendLine(new string('-', 70));
            text.AppendLine($"{DateTimeOffset.Now:dd.MM.yyyy HH:mm:ss}  {where}");
            text.AppendLine($"Версия программы: {Core.AppVersion.Display}");
            text.AppendLine(new string('-', 70));
            text.AppendLine(ex.ToString());
            text.AppendLine();

            File.AppendAllText(ErrorLogPath, text.ToString(),
                new UTF8Encoding(encoderShouldEmitUTF8Identifier: true));
        }
        catch
        {
            // Если и запись не удалась, молчим: сообщать об этом уже нечем.
        }
    }
}
