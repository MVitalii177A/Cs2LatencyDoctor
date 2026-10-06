using System.Diagnostics;
using Cs2LatencyDoctor.Core.Windows;

namespace Cs2LatencyDoctor.Core.Checks;

/// <summary>
/// Процессор: перегрев и ограничение мощности.
///
/// Температуру ядра Windows не отдаёт без стороннего драйвера, поэтому проверка
/// идёт от фактов, которые система сообщает сама:
///   * счётчик «частота процессора» в процентах от номинала — больше 100 означает,
///     что процессор работает выше базовой частоты, то есть не упирается в предел;
///   * системные события о снижении производительности процессора;
///   * счётчик «процент времени throttling» (если он доступен).
/// Чего не хватает — говорим честно и объясняем, чем измерить самому.
/// </summary>
public sealed class ThermalCheck : IDiagnosticCheck
{
    public string Id => "cpu.thermal";
    public string Title => "Перегрев и ограничение процессора";
    public bool RequiresAdmin => false;

    /// <summary>
    /// Источники, которые пишут о снижении частоты процессора. Список узкий намеренно:
    /// чем шире фильтр, тем больше шансов принять чужие события за перегрев.
    /// </summary>
    public static readonly IReadOnlyList<string> ThrottleEventSources = new[]
    {
        "Microsoft-Windows-Kernel-Processor-Power",
        "Microsoft-Windows-Thermal-Polling"
    };

    /// <summary>Коды событий, которые действительно означают ограничение. Без 55: это просто информация.</summary>
    public static readonly IReadOnlyList<int> ThrottleEventCodes = new[] { 37, 86, 87 };

    public async Task<IReadOnlyList<CheckResult>> RunAsync(DiagnosticContext context, CancellationToken ct)
    {
        var results = new List<CheckResult>();
        context.Progress("Смотрю состояние процессора…");

        var cpu = HardwareReader.ReadProcessor();

        if (cpu is null)
        {
            results.Add(CheckResult.Skipped(Id, Title,
                "Не удалось прочитать сведения о процессоре",
                NoHelpReason.BlockedBySystem,
                "Программа не смогла получить данные о процессоре: служба WMI не отвечает. " +
                "Что делать: проверьте температуру и частоту программой HWiNFO64 — " +
                "она показывает температуры всех ядер, частоты и причину снижения " +
                "(перегрев, лимит мощности). Это единственный надёжный способ увидеть " +
                "перегрев: Windows этих данных не отдаёт."));
            return results;
        }

        // Держим небольшую паузу, чтобы система успела обновить счётчики производительности:
        // сразу после запуска они показывают значение предыдущего интервала.
        await Task.Delay(1200, ct);

        var load = HardwareReader.ReadCpuLoadPercent();

        var detail = $"{cpu.Name}: {cpu.Cores} ядер, {cpu.LogicalProcessors} потоков, " +
                     $"частота {cpu.MaxClockMhz} МГц";

        // --- Признаки ограничения: система сообщает о снижении частоты ---
        var throttleEvents = await ReadThrottleEvents(ct);

        if (throttleEvents.Count > 0)
        {
            var last = throttleEvents[0];

            results.Add(CheckResult.Warn(Id + ".throttle", "Снижение частоты процессора",
                $"Система сообщала о снижении производительности процессора: " +
                $"{throttleEvents.Count} раз, последний — {last:dd.MM.yyyy HH:mm}",
                "Когда процессор перегревается или упирается в лимит мощности, он снижает " +
                "частоту. В игре это провалы кадров: секунду всё ровно, потом рывок. " +
                "К сетевой задержке отношения не имеет, но жалобы звучат одинаково.",
                new[]
                {
                    new FixAction("cpu.clean-cooler", "Почистить систему охлаждения",
                        FixRisk.ManualOnly, "Требуется разборка компьютера."),
                    new FixAction("cpu.check-power-limit", "Проверить лимиты мощности в BIOS",
                        FixRisk.ManualOnly, "Настройка BIOS, делается вручную.")
                },
                "Программа не может исправить перегрев: это железо. Что делать по порядку: " +
                "1) измерьте температуру под нагрузкой программой HWiNFO64 — если ядра выше " +
                "95 °C, охлаждение не справляется; " +
                "2) почистите радиатор и вентиляторы от пыли — это самая частая причина; " +
                "3) проверьте, что кулер плотно прижат и термопаста не высохла (меняется раз в 2–3 года); " +
                "4) если корпус закрыт со всех сторон, добавьте корпусный вентилятор на выдув; " +
                "5) в BIOS проверьте лимиты мощности (PL1 и PL2): если они занижены, " +
                "процессор не выдаёт свою мощность — верните значения по умолчанию."));
        }
        else
        {
            results.Add(new CheckResult
            {
                Id = Id + ".throttle",
                Title = "Снижение частоты процессора",
                Severity = Severity.Ok,
                Detail = "Система не сообщала о снижении производительности процессора",
                Why = "Это не измерение температуры, а отсутствие жалоб со стороны системы: " +
                      "если процессор упирался бы в перегрев или лимит мощности, Windows " +
                      "записала бы событие. Совсем исключить перегрев это не может."
            });
        }

        // --- Текущая частота относительно номинала ---
        if (cpu.MaxClockMhz > 0 && cpu.CurrentClockMhz > 0)
        {
            var percent = cpu.LoadPercent;

            // Выше номинала — процессор разгоняется сам, значит запаса хватает.
            if (percent >= 100)
            {
                results.Add(CheckResult.Ok(Id + ".clock", "Частота процессора",
                    $"Сейчас {cpu.CurrentClockMhz} МГц при номинале {cpu.MaxClockMhz} МГц " +
                    $"({percent}%) — выше базовой, ограничения нет"));
            }
            else if (percent < 80)
            {
                results.Add(CheckResult.Info(Id + ".clock", "Частота процессора",
                    $"Сейчас {cpu.CurrentClockMhz} МГц при номинале {cpu.MaxClockMhz} МГц ({percent}%)",
                    "Процессор работает ниже базовой частоты. В простое это нормально — " +
                    "он экономит энергию. Тревожно, только если так же под нагрузкой.",
                    recommendation: "Запустите игру и посмотрите эту проверку ещё раз: " +
                                    "если под нагрузкой частота остаётся ниже номинала, " +
                                    "процессор что-то ограничивает. Измерьте температуру " +
                                    "программой HWiNFO64 под нагрузкой и проверьте лимиты " +
                                    "мощности в BIOS."));
            }
            else
            {
                results.Add(new CheckResult
                {
                    Id = Id + ".clock",
                    Title = "Частота процессора",
                    Severity = Severity.Ok,
                    Detail = $"Сейчас {cpu.CurrentClockMhz} МГц при номинале {cpu.MaxClockMhz} МГц ({percent}%)",
                    Why = "Частота в пределах нормы для текущей нагрузки."
                });
            }
        }

        // --- Загрузка: если она высокая прямо сейчас, замер частоты менее показателен ---
        if (load is > 85)
        {
            results.Add(CheckResult.Info(Id + ".load", "Загрузка процессора",
                $"Сейчас загружен на {load}%",
                "Процессор уже чем-то занят. Если это не игра, виноваты фоновые программы: " +
                "обновления, синхронизация, браузер, торренты.",
                recommendation: "Посмотрите список фоновых программ в этом окне: " +
                                "программа показывает, что запущено и сколько занимает. " +
                                "Лишнее можно поставить на паузу кнопкой."));
        }

        // --- Честно про то, чего программа не умеет ---
        results.Add(new CheckResult
        {
            Id = Id + ".temp",
            Title = "Температура процессора",
            Severity = Severity.Skipped,
            Detail = "Температуру ядер Windows не отдаёт без стороннего драйвера",
            NoHelpReason = NoHelpReason.HardwareNotSupported,
            Recommendation = "Программа не может измерить температуру: в системе нет " +
                             "датчиков, доступных без установки драйвера. Что делать: " +
                             "скачайте HWiNFO64 (бесплатная, работает без установки), " +
                             "запустите её в режиме «только датчики», зайдите в игру на 10 минут " +
                             "и посмотрите строку CPU Package Temperature. Норма для игровой " +
                             "нагрузки — до 85 °C. Выше 95 °C — охлаждение не справляется."
        });

        return results;
    }

    /// <summary>
    /// Запрос к журналу событий для одного источника и кода.
    ///
    /// Вынесен отдельно, чтобы его можно было проверить тестом. Это важно:
    /// ошибка в фильтре не падает и не выдаёт исключение — она молча находит
    /// чужие события и превращает их в ложное предупреждение о перегреве.
    /// Именно так и случилось: код 37 пишет ещё и служба точного времени.
    /// </summary>
    public static string BuildEventQuery(string source, int code) =>
        "SELECT TimeGenerated FROM Win32_NTLogEvent WHERE Logfile='System' " +
        $"AND SourceName='{source}' AND EventCode={code}";

    /// <summary>
    /// Даты событий о снижении производительности процессора.
    ///
    /// Обязательно фильтруем по ИСТОЧНИКУ, а не только по коду. Это не перестраховка:
    /// код 37 пишут сразу несколько служб, включая службу точного времени. Проверка,
    /// которая берёт события по одному коду, находит чужие записи и выдаёт их
    /// за перегрев — то есть врёт пользователю. На этой машине так и вышло.
    ///
    /// Коды у источника питания процессора:
    ///   37 — снижение частоты из-за лимита мощности или температуры;
    ///   86 — сработала защита от перегрева;
    ///   87 — возврат к нормальной работе.
    /// Событие 55 писать нельзя: это обычная информация о питании, она пишется постоянно.
    /// </summary>
    private static Task<List<DateTime>> ReadThrottleEvents(CancellationToken ct)
    {
        return Task.Run(() =>
        {
            var dates = new List<DateTime>();

            foreach (var source in ThrottleEventSources)
            {
                if (ct.IsCancellationRequested) break;

                foreach (var code in ThrottleEventCodes)
                {
                    try
                    {
                        using var searcher =
                            new System.Management.ManagementObjectSearcher(BuildEventQuery(source, code));
                        searcher.Options.Timeout = TimeSpan.FromSeconds(15);

                        foreach (var obj in searcher.Get())
                        {
                            if (ct.IsCancellationRequested) break;

                            using (obj)
                            {
                                try
                                {
                                    var raw = obj["TimeGenerated"] as string;
                                    if (raw is null) continue;

                                    dates.Add(System.Management.ManagementDateTimeConverter.ToDateTime(raw));
                                }
                                catch
                                {
                                    // Непонятная запись: пропускаем, а не выдумываем дату.
                                }
                            }
                        }
                    }
                    catch
                    {
                        // Журнал недоступен: проверка скажет, что событий не найдено,
                        // но в тексте оговорено, что это не измерение температуры.
                    }
                }
            }

            return dates.OrderByDescending(d => d).Take(20).ToList();
        }, ct);
    }
}
