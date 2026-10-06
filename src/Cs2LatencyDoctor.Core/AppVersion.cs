using System.Reflection;

namespace Cs2LatencyDoctor.Core;

/// <summary>
/// Версия программы в одном месте — чтобы она одинаково показывалась
/// и в окне, и в консольном отчёте, и не приходилось вписывать её руками
/// в несколько файлов.
///
/// Номер берётся из свойств проекта (Version и FileVersion), то есть меняется
/// в одном месте — в файле проекта.
/// </summary>
public static class AppVersion
{
    /// <summary>Короткая версия: «1.0.0». Её показываем человеку.</summary>
    public static string Short { get; } = ReadShort();

    /// <summary>Полная строка вида «Cs2LatencyDoctor 1.0.0». Для заголовков отчёта.</summary>
    public static string Full => "Cs2LatencyDoctor " + Short;

    /// <summary>Дата сборки: помогает понять, какая именно сборка у человека.</summary>
    public static string BuildDate { get; } = ReadBuildDate();

    /// <summary>Строка для интерфейса: «Версия 1.0.0 от 06.10.2026».</summary>
    public static string Display =>
        string.IsNullOrEmpty(BuildDate) ? "Версия " + Short : $"Версия {Short} от {BuildDate}";

    private static string ReadShort()
    {
        try
        {
            var assembly = typeof(AppVersion).Assembly;

            // InformationalVersion задаётся в проекте и может содержать суффикс
            // вида «+коммит» — его отрезаем, человеку он не нужен.
            var informational = assembly
                .GetCustomAttribute<AssemblyInformationalVersionAttribute>()
                ?.InformationalVersion;

            if (!string.IsNullOrWhiteSpace(informational))
            {
                var plus = informational.IndexOf('+');
                return plus > 0 ? informational[..plus] : informational;
            }

            var version = assembly.GetName().Version;
            return version is null ? "неизвестна" : $"{version.Major}.{version.Minor}.{version.Build}";
        }
        catch
        {
            return "неизвестна";
        }
    }

    private static string ReadBuildDate()
    {
        try
        {
            // Дата файла сборки надёжнее, чем дата компиляции: её видно и в проводнике.
            var location = typeof(AppVersion).Assembly.Location;
            if (string.IsNullOrEmpty(location) || !System.IO.File.Exists(location)) return string.Empty;

            return System.IO.File.GetLastWriteTime(location).ToString("dd.MM.yyyy");
        }
        catch
        {
            return string.Empty;
        }
    }
}
