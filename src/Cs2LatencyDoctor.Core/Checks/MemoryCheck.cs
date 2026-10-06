using Cs2LatencyDoctor.Core.Windows;

namespace Cs2LatencyDoctor.Core.Checks;

/// <summary>
/// Оперативная память: двухканальный режим и частота.
///
/// Зачем это в инструменте про задержки. Память в одном канале вместо двух режет
/// пропускную способность вдвое. В CS2 это не «низкий FPS», а неровные кадры:
/// процессор ждёт данные, и время кадра прыгает. Человек при этом ищет проблему
/// в сети, потому что ощущения похожие.
/// </summary>
public sealed class MemoryCheck : IDiagnosticCheck
{
    public string Id => "memory.dual-channel";
    public string Title => "Оперативная память";
    public bool RequiresAdmin => false;

    public Task<IReadOnlyList<CheckResult>> RunAsync(DiagnosticContext context, CancellationToken ct)
    {
        var results = new List<CheckResult>();
        context.Progress("Смотрю, как установлена память…");

        var modules = HardwareReader.ReadMemoryModules();

        if (modules.Count == 0)
        {
            results.Add(CheckResult.Skipped(Id, Title,
                "Не удалось прочитать сведения о планках памяти",
                NoHelpReason.BlockedBySystem,
                "Программа не смогла получить данные о памяти: служба WMI не отвечает или " +
                "доступ к ней ограничен. Что делать: посмотрите количество каналов в " +
                "«Диспетчер задач → Производительность → Память» — там внизу указано " +
                "«Каналы: 2» или «Каналы: 1». Если указан один канал при двух установленных " +
                "планках, проверьте, что они стоят в слотах через один (обычно A2 и B2), " +
                "а не рядом."));
            return Task.FromResult<IReadOnlyList<CheckResult>>(results);
        }

        var totalGb = modules.Sum(m => m.CapacityBytes) / 1_073_741_824.0;
        var slots = HardwareReader.ReadMemorySlotCount();
        var layout = string.Join(", ", modules.Select(m => $"{m.Slot} {m.CapacityText}"));

        // --- Двухканальный режим ---
        // Определяем по расположению планок: разные каналы обозначаются разными
        // контроллерами (Controller0/Controller1) либо буквами A/B в имени слота.
        var channels = modules
            .Select(m => DescribeChannel(m.Slot))
            .Where(c => c is not null)
            .Distinct()
            .ToList();

        if (modules.Count >= 2 && channels.Count >= 2)
        {
            results.Add(new CheckResult
            {
                Id = Id + ".mode",
                Title = "Режим работы памяти",
                Severity = Severity.Ok,
                Detail = $"Двухканальный: {layout}",
                Why = "Планки стоят в разных каналах, поэтому память работает на полной " +
                      "пропускной способности. Одноканальный режим заметно снижает " +
                      "стабильность времени кадра в CS2."
            });
        }
        else if (modules.Count >= 2)
        {
            results.Add(CheckResult.Warn(Id + ".mode", "Режим работы памяти",
                $"Похоже на одноканальный режим: {layout}",
                "Когда обе планки стоят в слотах одного канала, пропускная способность " +
                "памяти падает примерно вдвое. Процессор ждёт данные, и время кадра " +
                "становится неровным — в игре это ощущается как подёргивания, " +
                "хотя задержка сети ни при чём.",
                new[]
                {
                    new FixAction("memory.dual-channel", "Переставить планки в разные каналы",
                        FixRisk.ManualOnly, "Требуется открыть корпус — программа этого не делает.")
                },
                "Программа не может переставить планки: это физическое действие. Что делать: " +
                "1) выключите компьютер и выньте кабель питания; " +
                "2) посмотрите в инструкции к материнской плате, какие слоты нужно занять " +
                "для двухканального режима — обычно это второй и четвёртый слот от процессора " +
                "(A2 и B2), а не два соседних; " +
                "3) переставьте планки и включите компьютер; " +
                "4) запустите проверку снова — режим должен определиться как двухканальный."));
        }
        else
        {
            results.Add(new CheckResult
            {
                Id = Id + ".mode",
                Title = "Режим работы памяти",
                Severity = Severity.Info,
                Detail = $"Установлена одна планка: {layout}",
                Why = "Одна планка всегда работает в одноканальном режиме: пропускная " +
                      "способность вдвое ниже, чем у двух. В CS2 это влияет на ровность " +
                      "времени кадра, особенно на процессорах без большого кэша.",
                Recommendation = "Программа ничего не может сделать: нужна вторая планка. " +
                                 "Что делать: если собираетесь добавлять память, берите планку " +
                                 "такого же объёма, как уже стоят, и ставьте её в парный слот — " +
                                 "тогда включится двухканальный режим. Смешивать разные планки " +
                                 "можно, но тогда двухканальный режим может не включиться."
            });
        }

        // --- Слоты ---
        if (slots > 0)
        {
            var free = slots - modules.Count;

            results.Add(new CheckResult
            {
                Id = Id + ".slots",
                Title = "Занятые слоты памяти",
                Severity = Severity.Info,
                Detail = free > 0
                    ? $"Занято {modules.Count} из {slots}, свободно {free}. Всего {totalGb:0} ГБ"
                    : $"Занято все {slots} слотов. Всего {totalGb:0} ГБ",
                Why = free > 0
                    ? "Свободные слоты позволяют добавить память, не выбрасывая уже установленные планки."
                    : "Свободных слотов нет: для увеличения памяти придётся менять планки."
            });
        }

        // --- Частота ---
        var slowed = modules
            .Where(m => m.RatedSpeedMhz > 0 && m.ActualSpeedMhz > 0 &&
                        m.ActualSpeedMhz < m.RatedSpeedMhz - 200)
            .ToList();

        if (slowed.Count > 0)
        {
            var sample = slowed[0];

            results.Add(CheckResult.Info(Id + ".speed", "Частота памяти",
                $"Планки рассчитаны на {sample.RatedSpeedMhz} МГц, работают на {sample.ActualSpeedMhz} МГц",
                "Память работает медленнее, чем может. Обычная причина — в BIOS выключен " +
                "профиль разгона памяти (XMP или EXPO): по умолчанию плата выставляет " +
                "безопасную минимальную частоту.",
                recommendation: "Программа не меняет настройки BIOS: это опасно и делается " +
                                 "только вручную. Что делать: 1) войдите в BIOS (обычно Del или F2 " +
                                 "при включении); 2) найдите профиль памяти — он называется XMP " +
                                 "(Intel) или EXPO (AMD); 3) включите его и сохраните настройки; " +
                                 "4) если компьютер не загрузится, сбросьте BIOS — обычно есть " +
                                 "кнопка сброса или перемычка; 5) после включения проверьте частоту " +
                                 "этой проверкой снова. Учтите: выигрыш в CS2 от частоты памяти " +
                                 "небольшой — это не решит проблему с сетью."));
        }
        else if (modules.Any(m => m.RatedSpeedMhz > 0))
        {
            var sample = modules.First(m => m.RatedSpeedMhz > 0);

            results.Add(CheckResult.Ok(Id + ".speed", "Частота памяти",
                $"Работает на заявленной частоте {sample.ActualSpeedMhz} МГц"));
        }

        return Task.FromResult<IReadOnlyList<CheckResult>>(results);
    }

    /// <summary>
    /// Канал, к которому относится слот. Возвращает null, если понять не удалось:
    /// выдумывать канал нельзя, на этом строится вывод о двухканальности.
    /// </summary>
    public static string? DescribeChannel(string slot)
    {
        if (string.IsNullOrWhiteSpace(slot)) return null;

        // Формат WMI на большинстве плат: Controller0-DIMMA2, Controller1-DIMMB2.
        var controllerIndex = slot.IndexOf("Controller", StringComparison.OrdinalIgnoreCase);
        if (controllerIndex >= 0)
        {
            var rest = slot[(controllerIndex + "Controller".Length)..];
            var digit = rest.TakeWhile(char.IsDigit).ToArray();

            if (digit.Length > 0) return "controller" + new string(digit);
        }

        // Запасной вариант: буква канала в имени слота (DIMMA1 / DIMMB1).
        foreach (var letter in new[] { "A", "B", "C", "D" })
        {
            if (slot.Contains("DIMM" + letter, StringComparison.OrdinalIgnoreCase))
                return "channel" + letter;
        }

        return null;
    }
}
