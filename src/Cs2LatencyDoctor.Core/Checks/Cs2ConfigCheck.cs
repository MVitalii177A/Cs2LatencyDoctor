using Cs2LatencyDoctor.Core.Windows;

namespace Cs2LatencyDoctor.Core.Checks;

/// <summary>
/// Проверка конфигурации CS2: режим экрана, VSync, Reflex, лимит FPS и параметры запуска.
/// Читаем файлы игры напрямую — они лежат в открытом виде и не требуют запуска CS2.
/// </summary>
public sealed class Cs2ConfigCheck : IDiagnosticCheck
{
    public string Id => "cs2.config";
    public string Title => "Настройки Counter-Strike 2";
    public bool RequiresAdmin => false;

    public Task<IReadOnlyList<CheckResult>> RunAsync(DiagnosticContext context, CancellationToken ct)
    {
        var results = new List<CheckResult>();
        context.Progress("Ищу установленную CS2…");

        var install = Cs2Locator.Find();
        if (install is null)
        {
            results.Add(CheckResult.Skipped(Id, Title,
                "CS2 не найдена (Steam или папка игры не обнаружены)"));
            return Task.FromResult<IReadOnlyList<CheckResult>>(results);
        }

        context.Cs2Path = install.GameFolder;
        context.SteamUserId = install.SteamUserId;
        context.Progress("Читаю настройки видео CS2…");

        // --- Режим экрана ---
        var video = install.VideoSettings;
        if (video is null)
        {
            results.Add(CheckResult.Skipped(Id + ".display", "Режим экрана CS2",
                "Файл cs2_video.txt не найден — игра ещё не запускалась"));
        }
        else
        {
            var (fullscreen, noWindowBorder) = (video.Fullscreen, video.NoWindowBorder);

            if (fullscreen == 1 && noWindowBorder == 0)
            {
                results.Add(CheckResult.Ok(Id + ".display", "Режим экрана CS2",
                    "Полноэкранный (exclusive) — оптимально"));
            }
            else if (fullscreen == 1 && noWindowBorder == 1)
            {
                results.Add(CheckResult.Info(Id + ".display", "Режим экрана CS2",
                    "Полноэкранный оконный (borderless)",
                    "В этом режиме кадр проходит через композитор Windows: это добавляет ориентировочно " +
                    "1–3 кадра задержки (на 180 Гц это 5–16 мс) и ухудшает работу G-Sync.",
                    new[]
                    {
                        new FixAction("cs2.display.exclusive",
                            "Переключить CS2 в exclusive fullscreen", FixRisk.Safe,
                            "Правится в файле настроек игры. Требуется, чтобы CS2 была закрыта.")
                    }));
            }
            else
            {
                results.Add(CheckResult.Warn(Id + ".display", "Режим экрана CS2",
                    "Оконный режим",
                    "Оконный режим — худший вариант по задержке.",
                    new[]
                    {
                        new FixAction("cs2.display.exclusive",
                            "Переключить CS2 в exclusive fullscreen", FixRisk.Safe, null)
                    }));
            }

            // --- VSync ---
            if (video.VSync == 1)
            {
                results.Add(CheckResult.Warn(Id + ".vsync", "Вертикальная синхронизация CS2",
                    "Включена",
                    "VSync добавляет до одного кадра задержки и сильно вредит отзывчивости прицела.",
                    new[] { new FixAction("cs2.vsync.off", "Выключить VSync в CS2", FixRisk.Safe, null) }));
            }
            else
            {
                results.Add(CheckResult.Ok(Id + ".vsync", "Вертикальная синхронизация CS2", "Выключена"));
            }

            // --- Reflex / низкая задержка ---
            switch (video.LowLatency)
            {
                case 2:
                    results.Add(CheckResult.Ok(Id + ".reflex", "NVIDIA Reflex", "Включён (Boost)"));
                    break;
                case 1:
                    results.Add(CheckResult.Info(Id + ".reflex", "NVIDIA Reflex", "Включён",
                        "Режим «Boost» дополнительно ограничивает очередь кадров — обычно это лучше."));
                    break;
                default:
                    results.Add(CheckResult.Warn(Id + ".reflex", "NVIDIA Reflex", "Выключен",
                        "Reflex синхронизирует начало отрисовки кадра с готовностью GPU и заметно " +
                        "снижает задержку ввода — при условии, что загрузка GPU высокая.",
                        new[] { new FixAction("cs2.reflex.on", "Включить Reflex + Boost", FixRisk.Safe, null) }));
                    break;
            }

            // --- Сглаживание: влияет на загрузку GPU, а значит на очередь кадров ---
            if (video.Msaa >= 8)
            {
                results.Add(CheckResult.Info(Id + ".msaa", "Сглаживание (MSAA)",
                    $"{video.Msaa}x",
                    "Высокое сглаживание повышает время кадра и удлиняет очередь GPU. " +
                    "В CS2 его обычно держат на 2x–4x или выключают ради максимального FPS."));
            }
        }

        return Task.FromResult<IReadOnlyList<CheckResult>>(results);
    }
}
