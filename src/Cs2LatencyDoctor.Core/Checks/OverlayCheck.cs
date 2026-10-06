using System.Diagnostics;

namespace Cs2LatencyDoctor.Core.Checks;

/// <summary>Программа, которая умеет рисовать поверх игры.</summary>
/// <param name="ProcessNames">Имена процессов без расширения.</param>
/// <param name="Title">Как называть в отчёте.</param>
/// <param name="Note">Чем именно мешает.</param>
/// <param name="BannedOnPlatforms">
/// Запрещена ли на соревновательных площадках. Задаётся явно, а не выводится из текста:
/// определение по словам в описании путает похожие программы, и цена ошибки —
/// ложное предупреждение о блокировке аккаунта.
/// </param>
public sealed record OverlayApp(
    string[] ProcessNames,
    string Title,
    string Note,
    bool BannedOnPlatforms);

/// <summary>
/// Программы, которые рисуют оверлей поверх игры или пишут видео с экрана.
///
/// Почему это важно именно для CS2 и именно сейчас. С июня 2025 года официальные
/// серверы Valve требуют флаг -allow_third_party_software для сторонних оверлеев,
/// а площадки вроде FACEIT и ESEA их прямо запрещают: за включённый оверлей там
/// блокируют аккаунт. Плюс сам по себе оверлей встраивается в отрисовку кадра
/// и добавляет к времени кадра — то есть работает против той самой отзывчивости,
/// которую мы ищем.
/// </summary>
public sealed class OverlayCheck : IDiagnosticCheck
{
    public string Id => "overlay.programs";
    public string Title => "Оверлеи и программы захвата";
    public bool RequiresAdmin => false;

    /// <summary>Что ищем. Список проверенный: только программы, которые правда рисуют поверх игры.</summary>
    public static readonly OverlayApp[] KnownOverlays =
    {
        new(new[] { "Overwolf", "OverwolfBrowser", "OverwolfHelper" }, "Overwolf",
            "Ставит свои оверлеи во многие игры и постоянно висит в памяти.", true),

        new(new[] { "Discord" }, "Discord",
            "Оверлей Discord встраивается в отрисовку кадра.", true),

        new(new[] { "obs64", "obs32" }, "OBS Studio",
            "Захват экрана и кодирование видео отбирают ресурсы процессора и видеокарты. " +
            "Если вы не пишете видео прямо сейчас, программу лучше закрыть.", false),

        new(new[] { "NVIDIA Share", "NVIDIA Overlay", "NVIDIA GeForce Experience" },
            "GeForce Experience",
            "Мгновенный повтор и оверлей работают постоянно, даже когда вы их не открываете.", true),

        new(new[] { "Medal", "MedalEncoder" }, "Medal.tv",
            "Постоянно пишет видео с экрана, чтобы вы могли сохранить момент.", true),

        new(new[] { "Outplayed", "OutplayedClient" }, "Outplayed",
            "Записывает видео с экрана постоянно.", true),

        new(new[] { "RTSS", "RivaTuner Statistics Server" }, "RivaTuner (RTSS)",
            "Показывает счётчик кадров и ограничивает частоту, вмешиваясь в отрисовку.", true),

        new(new[] { "MSIAfterburner" }, "MSI Afterburner",
            "Сам по себе не мешает, но его счётчик работает через RivaTuner, " +
            "а тот вмешивается в отрисовку кадра.", false),

        new(new[] { "GameBar", "GameBarPresenceWriter", "XboxGameOverlay" }, "Xbox Game Bar",
            "Штатный оверлей Windows. Держит фон захвата и мешает эксклюзивному " +
            "полноэкранному режиму.", false),

        new(new[] { "RadeonSoftware" }, "AMD Radeon Software",
            "Оверлей и запись работают в фоне, даже когда вы их не открываете.", true),

        new(new[] { "EpicGamesLauncher" }, "Epic Games Launcher",
            "Держит оверлей лаунчера поверх игры, даже если игра запущена из Steam.", true),

        new(new[] { "fraps" }, "Fraps",
            "Старый счётчик кадров. Вмешивается в отрисовку и роняет частоту кадров.", false)
    };

    public Task<IReadOnlyList<CheckResult>> RunAsync(DiagnosticContext context, CancellationToken ct)
    {
        var results = new List<CheckResult>();
        context.Progress("Ищу оверлеи и программы захвата…");

        var running = new List<(OverlayApp App, int ProcessCount, double MemoryMb)>();

        foreach (var app in KnownOverlays)
        {
            if (ct.IsCancellationRequested) break;

            var processes = app.ProcessNames
                .SelectMany(name =>
                {
                    try { return Process.GetProcessesByName(name); }
                    catch { return Array.Empty<Process>(); }
                })
                .ToList();

            if (processes.Count == 0) continue;

            double memoryMb;

            try { memoryMb = processes.Sum(p => p.WorkingSet64) / 1024.0 / 1024.0; }
            catch { memoryMb = 0; }

            foreach (var process in processes)
            {
                try { process.Dispose(); } catch { /* не критично */ }
            }

            running.Add((app, processes.Count, Math.Round(memoryMb)));
        }

        if (running.Count == 0)
        {
            results.Add(new CheckResult
            {
                Id = Id,
                Title = Title,
                Severity = Severity.Ok,
                Detail = $"Ни одна из {KnownOverlays.Length} известных программ с оверлеем не запущена",
                Why = "Оверлей встраивается в отрисовку кадра и добавляет к времени кадра. " +
                      "Проверено: сейчас поверх игры ничего не рисуется."
            });

            return Task.FromResult<IReadOnlyList<CheckResult>>(results);
        }

        // Разделяем на две группы: то, за что блокируют на площадках, и то, что просто мешает.
        var banned = running.Where(r => r.App.BannedOnPlatforms).ToList();
        var others = running.Where(r => !r.App.BannedOnPlatforms).ToList();

        foreach (var (app, count, memoryMb) in banned)
        {
            var processText = count == 1 ? "запущен" : $"запущено процессов: {count}";

            results.Add(CheckResult.Warn(Id + "." + Slug(app.Title), app.Title,
                $"{processText}, занимает {memoryMb:0} МБ",
                "Сторонние оверлеи запрещены на соревновательных площадках " +
                "(FACEIT, ESEA): за включённый оверлей там блокируют аккаунт. " +
                "На официальных серверах Valve для сторонних оверлеев нужен флаг " +
                "-allow_third_party_software, и он снижает доверие к аккаунту при подборе игроков.",
                new[]
                {
                    new FixAction("overlay." + Slug(app.Title) + ".close",
                        "Закрыть перед матчем", FixRisk.ManualOnly,
                        "Программа не закрывает чужие приложения: это может потерять ваши данные."),
                    new FixAction("overlay." + Slug(app.Title) + ".disable-overlay",
                        "Отключить оверлей в настройках программы", FixRisk.ManualOnly,
                        "Настройка внутри самой программы.")
                },
                $"Что делать: 1) закройте {app.Title} перед матчем — это самое простое; " +
                $"2) если он нужен для связи, отключите именно оверлей в его настройках, " +
                $"сама программа может остаться запущенной; " +
                $"3) {app.Note}"));
        }

        foreach (var (app, count, memoryMb) in others)
        {
            var processText = count == 1 ? "запущен" : $"запущено процессов: {count}";

            results.Add(CheckResult.Info(Id + "." + Slug(app.Title), app.Title,
                $"{processText}, занимает {memoryMb:0} МБ",
                app.Note,
                new[]
                {
                    new FixAction("overlay." + Slug(app.Title) + ".close",
                        "Закрыть, если не используется", FixRisk.ManualOnly,
                        "Программа не закрывает чужие приложения.")
                },
                "Программа не закрывает чужие приложения: это может привести к потере " +
                "несохранённых данных. Что делать: закройте вручную, если прямо сейчас " +
                "не пишете видео и не пользуетесь оверлеем."));
        }

        return Task.FromResult<IReadOnlyList<CheckResult>>(results);
    }

    /// <summary>Устойчивый идентификатор из названия: пробелы и точки в идентификаторах неудобны.</summary>
    private static string Slug(string title) =>
        new(title.ToLowerInvariant().Select(c => char.IsLetterOrDigit(c) ? c : '-').ToArray());
}
