using Cs2LatencyDoctor.Core.Windows;

namespace Cs2LatencyDoctor.Core.Checks;

/// <summary>
/// Проверка частоты обновления: и монитора, и игры.
///
/// Это одна из самых обидных потерь: человек купил 180-герцовый монитор,
/// а игра или Windows работают на 60. Задержка ввода при этом втрое выше,
/// и никакие настройки сети этого не компенсируют.
/// </summary>
public sealed class Cs2RefreshRateCheck : IDiagnosticCheck
{
    public string Id => "cs2.refresh";
    public string Title => "Частота обновления монитора и игры";
    public bool RequiresAdmin => false;

    public Task<IReadOnlyList<CheckResult>> RunAsync(DiagnosticContext context, CancellationToken ct)
    {
        var results = new List<CheckResult>();

        context.Progress("Читаю возможности монитора…");
        var monitor = MonitorCapabilityReader.Read();

        if (monitor is null || !monitor.HasData)
        {
            results.Add(CheckResult.Skipped(Id + ".monitor", "Возможности монитора",
                "Не удалось прочитать список поддерживаемых частот из EDID монитора",
                NoHelpReason.BlockedBySystem,
                "Программа не смогла узнать, какие частоты поддерживает монитор: данные EDID " +
                "недоступны. Что делать: посмотрите частоту вручную — " +
                "Параметры → Система → Дисплей → Дополнительные параметры дисплея → " +
                "«Частота обновления». Сравните с тем, что написано в характеристиках монитора. " +
                "В игре частота задаётся там же, где разрешение: Настройки → Видео."));
            return Task.FromResult<IReadOnlyList<CheckResult>>(results);
        }

        // --- Рабочий стол работает не на максимальной частоте монитора ---
        if (monitor.DesktopBelowMaximum)
        {
            results.Add(CheckResult.Warn(Id + ".desktop", "Частота обновления рабочего стола",
                $"Монитор умеет {monitor.MaxRefreshRate:0.##} Гц, а в Windows выставлено " +
                $"{monitor.CurrentDesktopRefreshRate} Гц",
                "Пока рабочий стол работает на пониженной частоте, игра в полноэкранном режиме " +
                "тоже получит её: Windows отдаёт игре текущий режим вывода. " +
                "На 60 Гц задержка между кадрами в три раза больше, чем на 180 — " +
                "это чувствуется как вязкий прицел.",
                new[]
                {
                    new FixAction("display.refresh.desktop", "Выставить максимальную частоту в Windows",
                        FixRisk.ManualOnly, "Правится только через настройки дисплея Windows.")
                },
                "Программа не меняет режим вывода: это делается в настройках Windows, и после смены " +
                "экрана может мигнуть. Что делать: правый щелчок по рабочему столу → " +
                "«Параметры экрана» → «Дополнительные параметры дисплея» → в списке " +
                "«Частота обновления» выберите максимальную (у вас " +
                $"{monitor.MaxRefreshRate:0.##} Гц) → «Сохранить изменения»."));
        }
        else if (monitor.CurrentDesktopRefreshRate > 0)
        {
            results.Add(CheckResult.Ok(Id + ".desktop", "Частота обновления рабочего стола",
                $"Windows работает на {monitor.CurrentDesktopRefreshRate} Гц, " +
                $"монитор поддерживает до {monitor.MaxRefreshRate:0.##} Гц"));
        }

        // --- Что записано в настройках самой игры ---
        var install = context.GetCs2Installation();
        var video = install?.VideoSettings;

        if (video is null)
        {
            results.Add(CheckResult.Skipped(Id + ".game", "Частота обновления в CS2",
                "Файл настроек видео CS2 ещё не создан",
                NoHelpReason.NotEnoughData,
                "Программа не может проверить, на какой частоте работает игра: CS2 ещё не создала " +
                "файл настроек. Что делать: запустите игру один раз (до главного меню) и закройте её, " +
                "затем повторите проверку."));
            return Task.FromResult<IReadOnlyList<CheckResult>>(results);
        }

        var gameRate = video.RefreshDenominator > 0
            ? (double)video.RefreshNumerator / video.RefreshDenominator
            : 0;

        if (gameRate <= 0)
        {
            // В файле 0/0 — игра берёт частоту рабочего стола. Это нормально,
            // если рабочий стол уже на максимуме, и опасно, если нет.
            var desktopOk = !monitor.DesktopBelowMaximum && monitor.CurrentDesktopRefreshRate > 0;

            results.Add(new CheckResult
            {
                Id = Id + ".game",
                Title = "Частота обновления в CS2",
                Severity = desktopOk ? Severity.Ok : Severity.Warning,
                Detail = "Игра не задаёт частоту явно и берёт её из Windows (" +
                         (monitor.CurrentDesktopRefreshRate > 0
                             ? $"{monitor.CurrentDesktopRefreshRate} Гц"
                             : "текущий режим") + ")",
                Why = "CS2 использует ту частоту, которая выставлена в Windows. Это нормально, " +
                      "пока рабочий стол работает на максимуме монитора.",
                NoHelpReason = desktopOk ? NoHelpReason.None : NoHelpReason.VendorLocked,
                Recommendation = desktopOk
                    ? "Отдельных действий не нужно: игра уже получает максимальную частоту. " +
                      "Если захотите задать её в игре явно — Настройки → Видео → «Частота обновления»."
                    : "Программа не задаёт частоту за игру: это настройка режима вывода, " +
                      "и игра перезапишет файл. Что делать: сначала поднимите частоту в Windows " +
                      "(Параметры экрана → Дополнительные параметры дисплея), " +
                      "затем в игре выберите максимальную в Настройки → Видео → «Частота обновления»."
            });

            return Task.FromResult<IReadOnlyList<CheckResult>>(results);
        }

        // Частота задана явно — сравниваем с возможностями монитора.
        if (monitor.HasData && monitor.MaxRefreshRate - gameRate > 1.5)
        {
            results.Add(CheckResult.Warn(Id + ".game", "Частота обновления в CS2",
                $"Игра работает на {gameRate:0.##} Гц, монитор поддерживает до " +
                $"{monitor.MaxRefreshRate:0.##} Гц",
                "Каждый кадр на 60 Гц занимает 16.7 мс, на 180 Гц — 5.6 мс. Разница в задержке " +
                "ввода заметна сразу, и она не лечится ни сетью, ни настройками Windows.",
                recommendation: "Программа не меняет эту настройку: игра перезапишет файл настроек " +
                "при выходе, поэтому правка в файле бессмысленна. Что делать: " +
                "Настройки → Видео → «Частота обновления» → выберите максимум " +
                $"({monitor.MaxRefreshRate:0.##} Гц). Если в списке нет нужной частоты — сначала " +
                "выставьте её в Windows: Параметры экрана → Дополнительные параметры дисплея."));
        }
        else
        {
            results.Add(CheckResult.Ok(Id + ".game", "Частота обновления в CS2",
                $"Игра работает на {gameRate:0.##} Гц — это максимум монитора"));
        }

        return Task.FromResult<IReadOnlyList<CheckResult>>(results);
    }
}
