using Cs2LatencyDoctor.Core.Windows;

namespace Cs2LatencyDoctor.Core.Checks;

/// <summary>
/// Проверка настроек сетевого адаптера, которые добавляют задержку и джиттер.
/// Самый результативный пункт: именно модерация прерываний давала 19 всплесков из 200.
/// </summary>
public sealed class NetworkAdapterCheck : IDiagnosticCheck
{
    public string Id => "net.adapter";
    public string Title => "Настройки сетевого адаптера";
    public bool RequiresAdmin => false;

    public Task<IReadOnlyList<CheckResult>> RunAsync(DiagnosticContext context, CancellationToken ct)
    {
        var results = new List<CheckResult>();
        context.Progress("Читаю настройки сетевого адаптера…");

        var adapters = NetworkAdapterReader.GetActiveAdapters();
        if (adapters.Count == 0)
        {
            results.Add(CheckResult.Skipped(Id, Title, "Активных сетевых адаптеров не найдено"));
            return Task.FromResult<IReadOnlyList<CheckResult>>(results);
        }

        // Основной адаптер берём из контекста (определён проверкой маршрута), иначе первый проводной.
        var primary = adapters.FirstOrDefault(a => a.Name == context.PrimaryAdapterName)
                      ?? adapters.FirstOrDefault(a => !a.IsWireless)
                      ?? adapters[0];

        // --- Проводное или Wi-Fi ---
        if (primary.IsWireless)
        {
            results.Add(CheckResult.Warn(Id + ".wireless", "Тип подключения",
                $"Активно беспроводное подключение: {primary.Name} ({primary.Description})",
                "Wi-Fi добавляет задержку и, что важнее, джиттер: ретрансляции, соседние сети, " +
                "переключение каналов. Для CS2 кабель — самое дешёвое и самое сильное улучшение.",
                new[] { new FixAction("net.use.cable", "Подключить кабель Ethernet", FixRisk.ManualOnly,
                    "Автоматически нельзя — нужно физическое действие.") },
                "Программа не может починить это сама: нужен физический кабель. Что делать: " +
                "подключите компьютер к роутеру кабелем. Если это невозможно — играйте на частоте " +
                "5 ГГц (не 2.4), сядьте ближе к роутеру и уберите между ними стены и металл. " +
                "Wi-Fi всегда будет давать всплески задержки, и настройками Windows это не лечится."));
        }
        else
        {
            results.Add(CheckResult.Ok(Id + ".wireless", "Тип подключения",
                $"Проводное: {primary.Name} ({primary.Description}), {primary.LinkSpeedText}"));
        }

        // --- Скорость линка ---
        if (!primary.IsWireless && primary.LinkSpeedBps > 0 && !primary.IsGigabitOrFaster)
        {
            results.Add(CheckResult.Info(Id + ".linkspeed", "Скорость линка",
                $"{primary.LinkSpeedText}",
                "Для CS2 пропускной способности хватает с запасом, но 100 Мбит/с часто признак " +
                "битого кабеля или ограничения порта. Стоит проверить кабель.",
                recommendation: "Программа не меняет скорость линка: обычно это признак железа. " +
                "Что делать: проверьте кабель (перегните, замените на заведомо рабочий), попробуйте " +
                "другой порт на роутере. Если кабель исправен, а скорость осталась 100 Мбит/с — " +
                "порт роутера или сетевой карты ограничен, и это уже не настройка."));
        }

        // --- Настройки, добавляющие задержку ---
        var hostile = NetworkAdapterReader.ReadLatencyHostileSettings(primary.Description);

        if (hostile.Count == 0)
        {
            // Проверяем, есть ли у этого адаптера вообще известные нам параметры.
            // Если нет — это не «всё хорошо», а «мы здесь бессильны», и об этом надо сказать.
            var knownCount = NetworkAdapterReader.KnownKeywordCount(primary.Description);

            if (knownCount == 0)
            {
                results.Add(new CheckResult
                {
                    Id = Id + ".tweaks",
                    Title = "Энергосбережение и модерация прерываний",
                    Severity = Severity.Info,
                    Detail = $"Адаптер «{primary.Description}» не предоставляет ни одного из известных " +
                             $"параметров задержки ({knownCount} из {NetworkAdapterReader.LatencyHostileKeywords.Count} найдено)",
                    Why = "У сетевых карт разных производителей настройки называются по-разному. " +
                          "Реализованы те, что встречаются у Realtek и части Intel — это самые частые случаи.",
                    NoHelpReason = NoHelpReason.HardwareNotSupported,
                    Recommendation =
                        "Программа не может ничего исправить на этой сетевой карте: у её драйвера нет " +
                        "известных нам параметров задержки. Что делать: " +
                        "1) откройте Диспетчер устройств → Сетевые адаптеры → двойной щелчок по адаптеру → " +
                        "вкладка «Дополнительно» и посмотрите, есть ли там пункты про энергосбережение, " +
                        "Green Ethernet, EEE или снижение энергопотребления — их стоит выключить вручную; " +
                        "2) проверьте утилиту производителя (Intel PROSet, Killer Control Center и подобные) " +
                        "и отключите в ней энергосбережение и приоритизацию трафика; " +
                        "3) остальные проверки этой программы работают независимо от сетевой карты."
                });
            }
            else
            {
                results.Add(CheckResult.Ok(Id + ".tweaks", "Энергосбережение и модерация прерываний",
                    $"Все известные параметры уже выставлены правильно ({knownCount} проверено)"));
            }
        }
        else
        {
            var names = hostile.Keys
                .Select(k => NetworkAdapterReader.LatencyHostileKeywords.TryGetValue(k, out var n) ? n : k)
                .ToList();

            var worst = hostile.ContainsKey("*InterruptModeration");

            var detail = "Включены: " + string.Join(", ", names) +
                         $" (адаптер: {primary.Description})";

            var why = worst
                ? "Модерация прерываний заставляет драйвер намеренно копить пакеты и отдавать их пачкой — " +
                  "это экономит CPU, но добавляет задержку и рывки. Остальные пункты переводят сетевую карту " +
                  "в энергосберегающие режимы, из-за которых отклик становится неровным."
                : "Эти параметры переводят сетевую карту в энергосберегающие режимы, из-за которых " +
                  "отклик становится неровным.";

            var fixList = hostile.Keys
                .Select(k => new FixAction("net.adapter." + k,
                    "Отключить: " + (NetworkAdapterReader.LatencyHostileKeywords.TryGetValue(k, out var n) ? n : k),
                    FixRisk.Safe, "Обратимо, значение сохраняется перед изменением."))
                .ToList();

            // Случай «мы можем помочь»: говорим, что именно сделаем, и что остаётся человеку.
            const string fixNote =
                "Программа исправит это сама — нажмите «Применить исправления». " +
                "После этого сетевой адаптер нужно перезапустить (программа предложит). " +
                "Что сделать вам: если у вас установлена утилита производителя сетевой карты " +
                "(Intel PROSet, Killer, MSI LAN Manager, ASUS GameFirst и подобные) — отключите в ней " +
                "энергосбережение и «оптимизацию трафика»: такие программы умеют возвращать эти " +
                "настройки обратно.";

            results.Add(worst
                ? CheckResult.Problem(Id + ".tweaks", "Энергосбережение и модерация прерываний",
                    detail, why, fixList, fixNote)
                : CheckResult.Warn(Id + ".tweaks", "Энергосбережение и модерация прерываний",
                    detail, why, fixList, fixNote));
        }

        // --- Ошибки и отброшенные пакеты ---
        if (primary.ReceivedDiscarded > 0 || primary.ReceivedErrors > 0)
        {
            results.Add(CheckResult.Info(Id + ".errors", "Ошибки приёма",
                $"Отброшено: {primary.ReceivedDiscarded}, ошибок: {primary.ReceivedErrors}",
                "Ненулевые значения говорят о проблемах кабеля, порта или перегрузке буферов.",
                recommendation: "Программа не чинит физику. Что делать: замените сетевой кабель, " +
                "попробуйте другой порт на роутере. Если ошибки остаются — проблема в порту роутера " +
                "или сетевой карты."));
        }

        return Task.FromResult<IReadOnlyList<CheckResult>>(results);
    }
}
