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

        if (monitor is null || !monitor.HasData || monitor.GameMonitor is null)
        {
            results.Add(CheckResult.Skipped(Id + ".monitor", "Возможности монитора",
                "Не удалось прочитать режимы подключённых экранов",
                NoHelpReason.BlockedBySystem,
                "Программа не смогла узнать, какие частоты поддерживают экраны. " +
                "Что делать: посмотрите частоту вручную — " +
                "Параметры → Система → Дисплей → Дополнительные параметры дисплея → " +
                "«Частота обновления». Сравните с тем, что написано в характеристиках монитора. " +
                "В игре частота задаётся там же, где разрешение: Настройки → Видео."));
            return Task.FromResult<IReadOnlyList<CheckResult>>(results);
        }

        var screen = monitor.GameMonitor;

        // --- Экран, на котором запустится игра ---
        //
        // Сравниваем частоту с максимумом ЭТОГО экрана, а не с самым быстрым
        // в системе. У 60-герцового монитора 60 Гц — это норма, а не потеря.
        // Раньше программа брала самый быстрый монитор и общую частоту системы,
        // из-за чего на двух экранах (144 Гц и 60 Гц) писала «монитор умеет 144,
        // а выставлено 60» — хотя игра пошла бы на основной экран, где всё верно.
        results.Add(CheckResult.Ok(Id + ".monitor", "Подключённые экраны",
            monitor.DescribeAll()));

        if (screen.BelowMaximum)
        {
            results.Add(CheckResult.Warn(Id + ".desktop", "Частота обновления рабочего стола",
                $"Экран для игры ({screen.DeviceName}, {screen.MonitorName}) умеет " +
                $"{screen.MaxRefreshRate} Гц, а в Windows выставлено {screen.CurrentRefreshRate} Гц",
                "Пока рабочий стол работает на пониженной частоте, игра в полноэкранном режиме " +
                "тоже получит её: Windows отдаёт игре текущий режим вывода. " +
                $"На {screen.CurrentRefreshRate} Гц задержка между кадрами в " +
                $"{screen.MaxRefreshRate / Math.Max(1, screen.CurrentRefreshRate)} раза больше, " +
                "чем на максимуме — это чувствуется как вязкий прицел.",
                new[]
                {
                    new FixAction("display.refresh.desktop", "Выставить максимальную частоту в Windows",
                        FixRisk.ManualOnly, "Правится только через настройки дисплея Windows.")
                },
                "Программа не меняет режим вывода: это делается в настройках Windows, и после смены " +
                "экрана может мигнуть. Что делать: правый щелчок по рабочему столу → " +
                "«Параметры экрана» → «Дополнительные параметры дисплея» → в списке " +
                $"«Частота обновления» выберите {screen.MaxRefreshRate} Гц → «Сохранить изменения». " +
                "Менять надо для того экрана, который помечен как основной: именно на нём " +
                "запустится игра."));
        }
        else if (screen.CurrentRefreshRate > 0)
        {
            results.Add(CheckResult.Ok(Id + ".desktop", "Частота обновления рабочего стола",
                $"Экран для игры работает на {screen.CurrentRefreshRate} Гц из {screen.MaxRefreshRate} " +
                "возможных — это максимум"));
        }

        // --- Если есть другие экраны на пониженной частоте, скажем об этом ---
        //
        // Это не ошибка: у второго монитора своя максимальная частота.
        // Но если человек ждёт от него большего, он должен знать, что происходит.
        var others = monitor.Monitors.Where(m => !ReferenceEquals(m, screen)).ToList();

        if (others.Count > 0)
        {
            var slower = others.Where(m => m.BelowMaximum).ToList();

            results.Add(CheckResult.Info(Id + ".others", "Другие подключённые экраны",
                string.Join("; ", others.Select(m => m.Describe())),
                slower.Count > 0
                    ? "Некоторые экраны работают ниже своей максимальной частоты. " +
                      "Игра на них не запустится, поэтому на игру это не влияет — " +
                      "но если вы перенесёте окно игры на такой экран, частота будет ниже."
                    : null,
                recommendation: slower.Count > 0
                    ? "Если хотите поднять частоту и на этих экранах: правый щелчок по рабочему столу → " +
                      "«Параметры экрана» → выберите нужный экран → «Дополнительные параметры дисплея» → " +
                      "«Частота обновления». Учтите: у каждого монитора свой максимум, " +
                      "и 60 Гц для 60-герцового — это норма."
                    : null));
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
            var desktopOk = !screen.BelowMaximum && screen.CurrentRefreshRate > 0;

            results.Add(new CheckResult
            {
                Id = Id + ".game",
                Title = "Частота обновления в CS2",
                Severity = desktopOk ? Severity.Ok : Severity.Warning,
                Detail = "Игра не задаёт частоту явно и берёт её из Windows (" +
                         (screen.CurrentRefreshRate > 0
                             ? $"{screen.CurrentRefreshRate} Гц"
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

        // Частота задана явно — сравниваем с возможностями ЭТОГО экрана.
        // Игра запускается на основном экране, поэтому и сравнивать надо с ним,
        // а не с самым быстрым монитором в системе.
        if (screen.MaxRefreshRate > 0 && screen.MaxRefreshRate - gameRate > 1.5)
        {
            results.Add(CheckResult.Warn(Id + ".game", "Частота обновления в CS2",
                $"Игра работает на {gameRate:0.##} Гц, а экран для игры " +
                $"({screen.MonitorName}) поддерживает до {screen.MaxRefreshRate} Гц",
                "Каждый кадр на 60 Гц занимает 16.7 мс, на 180 Гц — 5.6 мс. Разница в задержке " +
                "ввода заметна сразу, и она не лечится ни сетью, ни настройками Windows.",
                recommendation: "Программа не меняет эту настройку: игра перезапишет файл настроек " +
                "при выходе, поэтому правка в файле бессмысленна. Что делать: " +
                "Настройки → Видео → «Частота обновления» → выберите " +
                $"{screen.MaxRefreshRate} Гц. Если в списке нет нужной частоты — сначала " +
                "выставьте её в Windows: Параметры экрана → Дополнительные параметры дисплея."));
        }
        else if (screen.MaxRefreshRate > 0)
        {
            results.Add(CheckResult.Ok(Id + ".game", "Частота обновления в CS2",
                $"Игра работает на {gameRate:0.##} Гц — это максимум экрана"));
        }

        return Task.FromResult<IReadOnlyList<CheckResult>>(results);
    }
}
