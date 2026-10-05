using Cs2LatencyDoctor.Core.Windows;

namespace Cs2LatencyDoctor.Core.Checks;

/// <summary>
/// Проверка питания USB: если Windows разрешает отключать USB-порты для экономии,
/// мышь и клавиатура могут "просыпаться" с задержкой, особенно между раундами.
/// </summary>
public sealed class PowerCheck : IDiagnosticCheck
{
    public string Id => "power.usb";
    public string Title => "Питание USB и схема электропитания";
    public bool RequiresAdmin => false;

    public Task<IReadOnlyList<CheckResult>> RunAsync(DiagnosticContext context, CancellationToken ct)
    {
        var results = new List<CheckResult>();
        context.Progress("Проверяю параметры питания USB…");

        PowerState state;
        try
        {
            state = PowerConfigReader.Read();
        }
        catch
        {
            results.Add(CheckResult.Skipped(Id, Title, "Не удалось прочитать параметры питания"));
            return Task.FromResult<IReadOnlyList<CheckResult>>(results);
        }

        if (state.UsbSelectiveSuspendAc is null && state.UsbSelectiveSuspendDc is null)
        {
            results.Add(CheckResult.Skipped(Id + ".suspend", "Отключение USB-портов для экономии",
                "Параметр недоступен в текущей схеме электропитания"));
        }
        else
        {
            var ac = state.UsbSelectiveSuspendAc;
            var dc = state.UsbSelectiveSuspendDc;

            // 0 = запрещено (хорошо), 1 = разрешено (плохо)
            var bad = ac == 1 || dc == 1;

            if (bad)
            {
                var parts = new List<string>();
                if (ac == 1) parts.Add("от сети: разрешено");
                if (dc == 1) parts.Add("от батареи: разрешено");

                results.Add(new CheckResult
                {
                    Id = Id + ".suspend",
                    Title = "Отключение USB-портов для экономии",
                    Severity = Severity.Warning,
                    Detail = "Разрешено (" + string.Join(", ", parts) + ")",
                    Why = "Windows может обесточивать USB-устройства при простое. Мышь после этого " +
                          "выходит из спящего режима с задержкой в первые миллисекунды движения — " +
                          "это чувствуется как «первый флик не туда».",
                    Fixes = new[]
                    {
                        new FixAction("power.usb.suspend.off",
                            "Запретить отключение USB-портов", FixRisk.Safe,
                            "Обратимо. Расход энергии на настольном ПК не имеет значения.")
                    }
                });
            }
            else
            {
                results.Add(CheckResult.Ok(Id + ".suspend", "Отключение USB-портов для экономии",
                    "Запрещено (правильно)"));
            }
        }

        // Схема питания: на настольном ПК нас интересует, не «Экономия энергии» ли активна.
        // Определяем её по GUID, а не по названию: названия локализованы, GUID — нет.
        if (!string.IsNullOrEmpty(state.SchemeGuid))
        {
            if (PowerConfigReader.IsPowerSaverScheme(state.SchemeGuid))
            {
                results.Add(CheckResult.Warn(Id + ".scheme", "Схема электропитания",
                    $"Активна схема «{state.SchemeName ?? "Экономия энергии"}»",
                    "Схема экономии ограничивает частоту процессора и агрессивно усыпляет устройства."));
            }
            else
            {
                var known = PowerConfigReader.DescribeScheme(state.SchemeGuid);
                var shown = state.SchemeName ?? known ?? "неизвестная схема";

                results.Add(CheckResult.Ok(Id + ".scheme", "Схема электропитания",
                    $"«{shown}» — подходит"));
            }
        }
        else if (!string.IsNullOrEmpty(state.SchemeName))
        {
            // GUID не прочитался — остаётся проверка по названию. Она работает
            // только на русской и английской Windows, поэтому это запасной путь.
            var isPowerSaver = state.SchemeName.Contains("эконом", StringComparison.OrdinalIgnoreCase)
                            || state.SchemeName.Contains("power saver", StringComparison.OrdinalIgnoreCase);

            results.Add(isPowerSaver
                ? CheckResult.Warn(Id + ".scheme", "Схема электропитания",
                    $"Активна схема «{state.SchemeName}»",
                    "Схема экономии ограничивает частоту процессора и агрессивно усыпляет устройства.")
                : CheckResult.Ok(Id + ".scheme", "Схема электропитания",
                    $"«{state.SchemeName}» — подходит"));
        }

        return Task.FromResult<IReadOnlyList<CheckResult>>(results);
    }
}
