using System.Text;

namespace Cs2LatencyDoctor.Core;

/// <summary>
/// Разовая инициализация приложения. На .NET Core старые кодировки (включая 866,
/// которая нужна для чтения вывода ping.exe и powercfg на русской Windows)
/// доступны только после регистрации провайдера.
/// </summary>
public static class AppBootstrap
{
    private static bool _initialized;

    public static void Initialize()
    {
        if (_initialized) return;
        _initialized = true;

        try
        {
            Encoding.RegisterProvider(CodePagesEncodingProvider.Instance);
        }
        catch
        {
            // Если не получилось — программа продолжит работать, но часть текста
            // от системных утилит может отображаться некорректно.
        }
    }
}
