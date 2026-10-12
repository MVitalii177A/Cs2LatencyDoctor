using Cs2LatencyDoctor.Core.Windows;

namespace Cs2LatencyDoctor.Core.Checks;

/// <summary>
/// Проверка переменной синхронизации (G-Sync / FreeSync / Adaptive-Sync).
///
/// Зачем эта проверка. CS2 при запуске показывает окно «Рекомендации по настройкам»
/// с текстом: «Похоже, ваш дисплей поддерживает функцию NVIDIA G-Sync, но сейчас
/// она отключена». Игра узнаёт о поддержке от монитора, а вот включена ли
/// синхронизация на самом деле — видит уже по факту работы.
///
/// Если это окно появляется, а настройки игры при этом правильные, причина
/// почти всегда одна из двух: синхронизация выключена в панели управления
/// видеокартой либо на самом мониторе.
///
/// Что программа может и чего не может.
///
/// Может: узнать из данных монитора, поддерживает ли он переменную частоту
/// (это лежит в открытом виде), и проверить настройки игры, которые мешают
/// синхронизации работать.
///
/// Не может: прочитать, включена ли синхронизация в панели управления NVIDIA.
/// Эта настройка хранится в зашифрованном файле драйвера, и доступа к ней
/// из программы нет. Поэтому вместо «включено/выключено» программа даёт
/// готовую инструкцию, где именно посмотреть. Обещать проверку, которой нет,
/// нельзя — это правило проекта.
/// </summary>
public sealed class GSyncCheck : IDiagnosticCheck
{
    public string Id => "cs2.gsync";
    public string Title => "Переменная синхронизация (G-Sync)";
    public bool RequiresAdmin => false;

    public Task<IReadOnlyList<CheckResult>> RunAsync(DiagnosticContext context, CancellationToken ct)
    {
        var results = new List<CheckResult>();

        context.Progress("Проверяю поддержку G-Sync монитором…");

        var monitor = MonitorCapabilityReader.Read();

        if (monitor is null)
        {
            results.Add(CheckResult.Skipped(Id, Title,
                "Не удалось прочитать данные монитора",
                NoHelpReason.NotEnoughData,
                "Программа не смогла получить сведения о мониторе, поэтому не может сказать, " +
                "поддерживает ли он переменную синхронизацию. " +
                "Что делать: проверьте вручную — откройте панель управления NVIDIA " +
                "(правый щелчок по рабочему столу → «Панель управления NVIDIA»), раздел " +
                "«Дисплей» → «Настройка G-SYNC». Если этот раздел есть и галочка ставится — " +
                "поддержка есть."));
            return Task.FromResult<IReadOnlyList<CheckResult>>(results);
        }

        // --- поддержка со стороны монитора ---
        var supports = monitor.SupportsVariableRefreshRate;

        if (supports == false)
        {
            results.Add(CheckResult.Info(Id, Title,
                "Монитор не заявляет поддержку переменной частоты",
                "В данных монитора нет сведений о поддержке G-Sync/FreeSync. " +
                "Если игра показывает окно с рекомендацией включить G-Sync, а монитор " +
                "её не поддерживает — это ошибка определения со стороны игры, " +
                "и делать ничего не нужно: синхронизация просто не заработает.",
                recommendation: "Проверить можно так: панель управления NVIDIA → «Дисплей» → " +
                                "«Настройка G-SYNC». Если раздела нет — монитор не поддерживает."));
            return Task.FromResult<IReadOnlyList<CheckResult>>(results);
        }

        var monitorText = supports == true
            ? "Монитор поддерживает переменную частоту" +
              (monitor.VariableRefreshMinHz is not null && monitor.VariableRefreshMaxHz is not null
                  ? $": диапазон {monitor.VariableRefreshMinHz}–{monitor.VariableRefreshMaxHz} Гц"
                  : string.Empty)
            : "Поддержку переменной частоты определить не удалось";

        // Что мы знаем о мониторе наверняка: паспортные частоты из его данных.
        // Это полезно само по себе и не зависит от того, удалось ли прочитать
        // расширенный блок со сведениями о синхронизации.
        var monitorFacts = monitor.HasData
            ? $"Монитор: до {monitor.MaxRefreshRate:0.##} Гц, в Windows выставлено " +
              $"{monitor.CurrentDesktopRefreshRate} Гц."
            : null;

        // Почему не удалось определить поддержку — говорим прямо, а не молчим.
        var whyUnknown = supports is null
            ? "Причина: сведения о синхронизации производитель размещает в расширенном блоке " +
              "данных монитора. Windows хранит в реестре только первый блок (128 байт), " +
              "а расширенный туда не попадает — его можно прочитать лишь с правами " +
              "администратора. Поэтому программа не утверждает ни «есть», ни «нет»."
            : null;

        // --- настройки игры, которые мешают синхронизации ---
        var install = context.GetCs2Installation();
        var video = install?.VideoSettings;

        var blockers = new List<string>();

        if (video is not null)
        {
            if (video.Fullscreen == 1 && video.NoWindowBorder == 1)
                blockers.Add("игра идёт в оконном режиме без рамки — в нём синхронизация " +
                             "включается только при определённой настройке панели драйвера");

            if (video.VSync == 1)
                blockers.Add("в игре включена вертикальная синхронизация — вместе с G-Sync " +
                             "она добавляет лишний кадр задержки");
        }

        var metrics = new Dictionary<string, double>();
        if (monitor.VariableRefreshMaxHz is not null)
            metrics["gsync.monitor.maxhz"] = monitor.VariableRefreshMaxHz.Value;
        if (monitor.VariableRefreshMinHz is not null)
            metrics["gsync.monitor.minhz"] = monitor.VariableRefreshMinHz.Value;

        // --- итог ---
        if (blockers.Count > 0)
        {
            results.Add(new CheckResult
            {
                Id = Id,
                Title = Title,
                Severity = Severity.Warning,
                Detail = monitorText + ". " + (monitorFacts is not null ? monitorFacts + " " : string.Empty) +
                         "Но настройки игры мешают: " + string.Join("; ", blockers) + ".",
                Why = "Переменная синхронизация убирает разрывы картинки и рывки, не добавляя " +
                      "задержки — в отличие от обычной вертикальной синхронизации. " +
                      "Чтобы она работала, игра должна идти в полноэкранном режиме, а " +
                      "вертикальная синхронизация внутри игры должна быть выключена." +
                      (whyUnknown is not null ? " " + whyUnknown : string.Empty),
                Recommendation = "Что сделать: " + (video?.Fullscreen == 1 && video?.NoWindowBorder == 1
                                      ? "в игре переключите Настройки → Видео → Режим экрана на «Полноэкранный». "
                                      : string.Empty) +
                                 (video?.VSync == 1
                                      ? "выключите вертикальную синхронизацию в меню игры. "
                                      : string.Empty) +
                                 "Затем проверьте синхронизацию вне игры — где именно, написано ниже.",
                Metrics = metrics,
                Fixes = new[]
                {
                    new FixAction("cs2.gsync.panel", "Включить G-SYNC в панели управления NVIDIA",
                        FixRisk.ManualOnly,
                        "Настройка хранится в зашифрованном файле драйвера — программа не может её изменить.",
                        CannotFixNote: "Панель управления NVIDIA не даёт менять свои настройки " +
                                       "из других программ: они лежат в зашифрованном виде. " +
                                       "Включить можно только вручную.")
                }
            });
        }
        else
        {
            results.Add(new CheckResult
            {
                Id = Id,
                Title = Title,
                Severity = Severity.Info,
                Detail = monitorText + ". " + (monitorFacts is not null ? monitorFacts + " " : string.Empty) +
                         "Настройки игры синхронизации не мешают.",
                Why = "Настройки игры правильные: полноэкранный режим и выключенная вертикальная " +
                      "синхронизация — именно то, что нужно для работы переменной синхронизации. " +
                      "Но включена ли она в панели управления видеокартой, программа проверить " +
                      "не может: эта настройка хранится в зашифрованном файле драйвера." +
                      (whyUnknown is not null ? " " + whyUnknown : string.Empty),
                Recommendation = "Если игра показывает окно «ваш дисплей поддерживает NVIDIA G-Sync, " +
                                 "но сейчас она отключена» — значит, синхронизация выключена вне игры. " +
                                 "Проверьте два места: панель управления NVIDIA → «Дисплей» → " +
                                 "«Настройка G-SYNC» (галочка «Включить G-SYNC», режим «для оконного и " +
                                 "полноэкранного», затем «Применить») и меню самого монитора (кнопки на " +
                                 "корпусе) → включить Adaptive-Sync или FreeSync. Без второго пункта " +
                                 "первый не заработает: на мониторах LG синхронизация работает через FreeSync.",
                Metrics = metrics,
                Fixes = new[]
                {
                    new FixAction("cs2.gsync.panel", "Включить G-SYNC в панели управления NVIDIA",
                        FixRisk.ManualOnly,
                        "Настройка хранится в зашифрованном файле драйвера — программа не может её изменить.",
                        CannotFixNote: "Панель управления NVIDIA не даёт менять свои настройки " +
                                       "из других программ: они лежат в зашифрованном виде. " +
                                       "Включить можно только вручную.")
                }
            });
        }

        // --- отдельная строка: обновление драйвера сбрасывает настройку ---
        results.Add(CheckResult.Info(Id + ".driver",
            "Обновление драйвера сбрасывает G-SYNC",
            "После установки нового драйвера видеокарты настройки панели управления возвращаются " +
            "к значениям по умолчанию — переменная синхронизация оказывается выключенной.",
            "Наблюдалось: после обновления драйвера CS2 начала показывать окно о выключенном " +
            "G-Sync, хотя до обновления всё работало.",
            recommendation: "Если игра снова покажет это окно после обновления драйвера — " +
                            "проверьте панель управления NVIDIA заново. Это ожидаемое поведение " +
                            "драйвера, а не поломка."));

        return Task.FromResult<IReadOnlyList<CheckResult>>(results);
    }
}
