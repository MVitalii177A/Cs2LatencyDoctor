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
                    "Автоматически нельзя — нужно физическое действие.") }));
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
                "битого кабеля или ограничения порта. Стоит проверить кабель."));
        }

        // --- Настройки, добавляющие задержку ---
        var hostile = NetworkAdapterReader.ReadLatencyHostileSettings(primary.Description);

        if (hostile.Count == 0)
        {
            results.Add(CheckResult.Ok(Id + ".tweaks", "Энергосбережение и модерация прерываний",
                "Все известные параметры уже выставлены правильно"));
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

            results.Add(worst
                ? CheckResult.Problem(Id + ".tweaks", "Энергосбережение и модерация прерываний", detail, why, fixList)
                : CheckResult.Warn(Id + ".tweaks", "Энергосбережение и модерация прерываний", detail, why, fixList));
        }

        // --- Ошибки и отброшенные пакеты ---
        if (primary.ReceivedDiscarded > 0 || primary.ReceivedErrors > 0)
        {
            results.Add(CheckResult.Info(Id + ".errors", "Ошибки приёма",
                $"Отброшено: {primary.ReceivedDiscarded}, ошибок: {primary.ReceivedErrors}",
                "Ненулевые значения говорят о проблемах кабеля, порта или перегрузке буферов."));
        }

        return Task.FromResult<IReadOnlyList<CheckResult>>(results);
    }
}
