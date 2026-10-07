using System;
using System.IO;
using System.Windows;
using Cs2LatencyDoctor.Gui;

class P
{
    [STAThread]
    static void Main()
    {
        var log = new System.Text.StringBuilder();
        var app = new Application();
        var dir = Path.Combine(@"C:\Deepseek_place\cs2-latency-doctor", "vm-out-" + Guid.NewGuid().ToString("N")[..6]);
        Directory.CreateDirectory(dir);

        try
        {
            var vm = new MainViewModel();
            log.AppendLine("модель создана");

            // Сохранение без проверки: так делает человек, который сразу нажал кнопку.
            foreach (var kind in new[] { ReportKind.Readable, ReportKind.Json, ReportKind.Developer })
            {
                var path = Path.Combine(dir, kind + ".txt");
                try
                {
                    vm.ExportReport(path, kind);
                    log.AppendLine(kind + ": без ошибок, файл " + (File.Exists(path) ? "создан" : "НЕ создан"));
                }
                catch (Exception ex)
                {
                    log.AppendLine(kind + ": ИСКЛЮЧЕНИЕ " + ex.GetType().Name + " — " + ex.Message);
                }
            }

            // Теперь с выполненной проверкой
            log.AppendLine("запускаю проверку…");
            var run = vm.RunDiagnosticsAsync();
            for (var i = 0; i < 60 && !run.IsCompleted; i++) System.Threading.Thread.Sleep(1000);
            log.AppendLine("проверка завершена: " + run.IsCompleted);

            foreach (var kind in new[] { ReportKind.Readable, ReportKind.Json, ReportKind.Developer })
            {
                var path = Path.Combine(dir, "после-" + kind + ".txt");
                try
                {
                    vm.ExportReport(path, kind);
                    log.AppendLine(kind + " после проверки: файл " + (File.Exists(path) ? "создан, " + new FileInfo(path).Length + " байт" : "НЕ создан"));
                }
                catch (Exception ex)
                {
                    log.AppendLine(kind + " после проверки: ИСКЛЮЧЕНИЕ " + ex.GetType().Name + " — " + ex.Message);
                }
            }
        }
        catch (Exception ex)
        {
            log.AppendLine("ОБЩАЯ ОШИБКА " + ex.GetType().Name + " — " + ex.Message);
            log.AppendLine(ex.StackTrace);
        }

        File.WriteAllText(@"C:\Deepseek_place\cs2-latency-doctor\vmtest.txt", log.ToString());
        try { Directory.Delete(dir, true); } catch { }
        app.Shutdown();
    }
}