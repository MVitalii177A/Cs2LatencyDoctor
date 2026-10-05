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
                "CS2 не найдена (Steam или папка игры не обнаружены)",
                NoHelpReason.NotEnoughData,
                "Программа не может проверить настройки игры, потому что не нашла её на дисках. " +
                "Что делать: если CS2 у вас установлена, запустите её один раз через Steam — " +
                "программа ищет игру по установленному Steam и списку библиотек. " +
                "Если игра стоит в нестандартном месте, добавьте её папку как библиотеку в Steam " +
                "(Настройки → Накопители). Остальные проверки работают независимо от игры."));
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
                "Файл настроек видео ещё не создан — игра ни разу не запускалась",
                NoHelpReason.NotEnoughData,
                "Программа не может прочитать режим экрана, потому что CS2 ещё не создала файл " +
                "настроек. Что делать: запустите CS2 один раз (можно дойти до главного меню) и " +
                "закройте её, затем повторите проверку."));
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
                    },
                    "Программа переключит режим сама — нажмите «Применить исправления», закрыв CS2. " +
                    "Что сделать вам после этого: если разрешение в игре не совпадает с разрешением " +
                    "монитора, проверьте в панели NVIDIA (Дисплей → Регулировка размера и положения " +
                    "рабочего стола), что режим масштабирования = «Полноэкранный» и выполняется на GPU. " +
                    "Иначе по краям появятся чёрные полосы. И не меняйте режим экрана в настройках " +
                    "игры после этого — игра перезапишет файл."));
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
                    },
                    "Программа переключит режим сама (нужно закрыть CS2). Если полос по краям быть " +
                    "не должно, а они появились — включите в панели NVIDIA полноэкранное " +
                    "масштабирование на GPU."));
            }

            // --- VSync ---
            if (video.VSync == 1)
            {
                results.Add(CheckResult.Warn(Id + ".vsync", "Вертикальная синхронизация CS2",
                    "Включена",
                    "VSync добавляет до одного кадра задержки и сильно вредит отзывчивости прицела.",
                    new[] { new FixAction("cs2.vsync.off", "Выключить VSync в CS2", FixRisk.Safe, null) },
                    "VSync правится только в меню игры (Настройки → Видео → Ожидание вертикальной " +
                    "синхронизации = Выключено): файл настроек игра перезапишет при выходе, поэтому " +
                    "менять это в файле бессмысленно. Если вы включали VSync против разрывов картинки — " +
                    "вместо него используйте G-Sync/FreeSync вместе с Reflex, они дают ровную картинку " +
                    "без добавления задержки."));
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
                        new[] { new FixAction("cs2.reflex.on", "Включить Reflex + Boost", FixRisk.Safe, null) },
                        "Reflex включается в меню игры: Настройки → Видео → NVIDIA Reflex Low Latency → " +
                        "«Включено + Boost». Через файл это не сделать — игра перезапишет настройку. " +
                        "Если у вас видеокарта AMD, вместо Reflex используйте Anti-Lag в драйвере."));
                    break;
            }

            // --- Сглаживание: влияет на загрузку GPU, а значит на очередь кадров ---
            if (video.Msaa >= 8)
            {
                results.Add(CheckResult.Info(Id + ".msaa", "Сглаживание (MSAA)",
                    $"{video.Msaa}x",
                    "Высокое сглаживание повышает время кадра и удлиняет очередь GPU. " +
                    "В CS2 его обычно держат на 2x–4x или выключают ради максимального FPS.",
                    recommendation: "Программа не меняет настройки графики: это вопрос вашего вкуса " +
                    "и мощности видеокарты. Что делать: если FPS заметно ниже частоты монитора, " +
                    "снизьте сглаживание до 2x или выключите в Настройки → Видео → " +
                    "Multisampling Anti-Aliasing Mode."));
            }
        }

        return Task.FromResult<IReadOnlyList<CheckResult>>(results);
    }
}
