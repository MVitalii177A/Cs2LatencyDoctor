using Cs2LatencyDoctor.Core.Windows;

namespace Cs2LatencyDoctor.Core.Checks;

/// <summary>
/// Проверка планировщика Multimedia Class Scheduler Service (MMCSS) и сетевого троттлинга.
/// Это те настройки, которые Windows сама не выставляет оптимально для игр.
/// </summary>
public sealed class SchedulerCheck : IDiagnosticCheck
{
    private const string GamesTaskPath =
        @"SOFTWARE\Microsoft\Windows NT\CurrentVersion\Multimedia\SystemProfile\Tasks\Games";
    private const string SystemProfilePath =
        @"SOFTWARE\Microsoft\Windows NT\CurrentVersion\Multimedia\SystemProfile";

    /// <summary>Идентификатор исправления, которое правит эти параметры.</summary>
    private const string FixId = "scheduler.mmcss";

    public string Id => "scheduler.mmcss";
    public string Title => "Планировщик мультимедиа и сетевой троттлинг";
    public bool RequiresAdmin => false;

    public Task<IReadOnlyList<CheckResult>> RunAsync(DiagnosticContext context, CancellationToken ct)
    {
        var results = new List<CheckResult>();
        context.Progress("Проверяю планировщик и троттлинг сети…");

        var gpuPriority = RegistryValueReader.ReadDword(Microsoft.Win32.RegistryHive.LocalMachine,
            GamesTaskPath, "GPU Priority");
        var priority = RegistryValueReader.ReadDword(Microsoft.Win32.RegistryHive.LocalMachine,
            GamesTaskPath, "Priority");
        var category = RegistryValueReader.ReadString(Microsoft.Win32.RegistryHive.LocalMachine,
            GamesTaskPath, "Scheduling Category");
        var sfio = RegistryValueReader.ReadString(Microsoft.Win32.RegistryHive.LocalMachine,
            GamesTaskPath, "SFIO Priority");
        var throttling = RegistryValueReader.ReadDword(Microsoft.Win32.RegistryHive.LocalMachine,
            SystemProfilePath, "NetworkThrottlingIndex");

        // --- MMCSS: категория планирования для игр ---
        // В одном исправлении пять параметров. Указываем подпункты, чтобы программа
        // меняла только те, на которые действительно жалуется проверка.
        var fixes = new List<FixAction>();
        if (!string.Equals(category, "High", StringComparison.OrdinalIgnoreCase))
        {
            fixes.Add(new FixAction("scheduler.mmcss.games", "Поднять категорию планирования игр",
                FixRisk.Safe,
                "Обратимо. Влияние умеренное — это не главный источник лага, но убирает лишнюю конкуренцию за CPU.",
                SubActionId: FixId + ".Scheduling Category"));
        }

        if (priority is null || priority < 6)
        {
            fixes.Add(new FixAction("scheduler.mmcss.games", "Поднять приоритет игр в планировщике",
                FixRisk.Safe,
                "Обратимо. Влияние умеренное — это не главный источник лага, но убирает лишнюю конкуренцию за CPU.",
                SubActionId: FixId + ".Priority"));
        }

        if (fixes.Count > 0)
        {
            results.Add(CheckResult.Info(Id + ".games", "Приоритет игр в планировщике",
                $"Категория: {category ?? "нет"}, приоритет: {priority?.ToString() ?? "нет"}, " +
                $"GPU: {gpuPriority?.ToString() ?? "нет"}, SFIO: {sfio ?? "нет"}",
                "Windows отдаёт игровым потокам приоритет по этой таблице. По умолчанию он средний.",
                fixes,
                "Программа исправит это сама — нажмите «Применить исправления». " +
                "Честно про эффект: это не главная причина лагов, а лишь уборка лишней конкуренции " +
                "за процессор. Если игру тормозит что-то другое, эта правка ничего не изменит."));
        }
        else
        {
            results.Add(CheckResult.Ok(Id + ".games", "Приоритет игр в планировщике",
                $"Категория {category}, приоритет {priority} — настроено"));
        }

        // --- NetworkThrottlingIndex: троттлинг сети для мультимедиа ---
        // Честно: на UDP-игру влияет слабо, поэтому это Info, а не Warning.
        if (throttling is not null && throttling != unchecked((int)0xFFFFFFFF))
        {
            results.Add(CheckResult.Info(Id + ".throttle", "Троттлинг сети для мультимедиа",
                $"NetworkThrottlingIndex = {throttling} (по умолчанию 10 пакетов/мс)",
                "Ограничивает обработку сетевых пакетов во время воспроизведения мультимедиа. " +
                "На UDP-трафик игры влияет слабо, но убрать ограничение безвредно.",
                new[] { new FixAction("scheduler.mmcss", "Отключить NetworkThrottlingIndex",
                    FixRisk.Safe, "Обратимо.",
                    SubActionId: FixId + ".NetworkThrottlingIndex") },
                "Программа сделает это сама при «Применить исправления». " +
                "Ожидать чуда не стоит: на игру это влияет слабо — правка убирает ограничение, " +
                "которое в основном касается пропускной способности, а не задержки."));
        }
        else
        {
            results.Add(CheckResult.Ok(Id + ".throttle", "Троттлинг сети для мультимедиа",
                "Отключён (0xFFFFFFFF)"));
        }

        return Task.FromResult<IReadOnlyList<CheckResult>>(results);
    }
}
