using Cs2LatencyDoctor.Core.Windows;

namespace Cs2LatencyDoctor.Core.Checks;

/// <summary>
/// Проверка параметров запуска CS2 в Steam.
///
/// Люди годами носят там наборы флагов из старых гайдов. Часть из них безвредна,
/// часть вредит: -high поднимает приоритет всему процессу целиком вместе с рабочими
/// потоками движка, -dev включает режим разработчика, а +fps_max 0 снимает лимит
/// кадров и удлиняет очередь GPU.
/// </summary>
public sealed class Cs2LaunchOptionsCheck : IDiagnosticCheck
{
    public string Id => "cs2.launch-options";
    public string Title => "Параметры запуска CS2 в Steam";

    public bool RequiresAdmin => false;

    public Task<IReadOnlyList<CheckResult>> RunAsync(DiagnosticContext context, CancellationToken ct)
    {
        var results = new List<CheckResult>();
        context.Progress("Читаю параметры запуска CS2…");

        var install = context.GetCs2Installation();
        if (install?.SteamUserId is null)
        {
            results.Add(CheckResult.Skipped(Id, Title,
                "Не удалось определить папку пользователя Steam",
                NoHelpReason.NotEnoughData,
                "Программа не может прочитать параметры запуска: не нашла профиль Steam. " +
                "Что делать: проверьте параметры вручную — Steam → библиотека → " +
                "правый щелчок по Counter-Strike 2 → Свойства → «Параметры запуска». " +
                "Если там есть -high, -dev или +fps_max 0, их стоит убрать."));
            return Task.FromResult<IReadOnlyList<CheckResult>>(results);
        }

        var options = Cs2LaunchOptionsReader.Read(install.SteamRoot, install.SteamUserId);
        if (options is null)
        {
            results.Add(CheckResult.Skipped(Id, Title,
                "Файл настроек Steam не читается",
                NoHelpReason.BlockedBySystem,
                "Программа не смогла открыть файл настроек Steam — обычно это закрытый доступ " +
                "или антивирус. Что делать: проверьте параметры запуска вручную: " +
                "Steam → правый щелчок по CS2 → Свойства → «Параметры запуска». " +
                "Полезен там только -allow_third_party_software; -high, -dev и +fps_max 0 лишние."));
            return Task.FromResult<IReadOnlyList<CheckResult>>(results);
        }

        // ---------------------------------------------------------- вредные флаги
        var harmful = new List<(string Flag, string Problem, string Advice)>();

        if (options.Contains("-high"))
        {
            harmful.Add(("-high",
                "поднимает приоритет всего процесса целиком, включая служебные потоки движка. " +
                "Планировщик Windows и сам CS2 распределяют приоритеты лучше.",
                "уберите -high из параметров запуска"));
        }

        if (options.Contains("-dev") || options.Contains("-console"))
        {
            var flag = options.Contains("-dev") ? "-dev" : "-console";
            harmful.Add((flag,
                "режим разработчика. Для игры он не нужен, а часть серверов и античитов " +
                "к такому запуску относится настороженно.",
                $"уберите {flag} из параметров запуска"));
        }

        if (options.ContainsPrefix("+fps_max") && (options.Contains("0") || options.Raw.Contains("fps_max 0")))
        {
            harmful.Add(("+fps_max 0",
                "лимит кадров снят полностью. Видеокарта работает на пределе, очередь кадров " +
                "удлиняется, и задержка ввода растёт. Для 180 Гц достаточно 300–400 кадров.",
                "замените +fps_max 0 на +fps_max 400 (или настройте лимит в игре)"));
        }

        if (harmful.Count > 0)
        {
            var detail = "Найдены лишние параметры: " +
                         string.Join(", ", harmful.Select(h => h.Flag));

            var why = "Параметры запуска применяются при старте игры и влияют на весь сеанс. " +
                      "Эти флаги пришли из старых гайдов и в CS2 либо бесполезны, либо вредят.";

            var advice = "Программа не меняет параметры запуска: файл настроек Steam она не трогает, " +
                         "потому что Steam может перезаписать правку и потерять ваши настройки. " +
                         "Что делать вручную: Steam → библиотека → правый щелчок по Counter-Strike 2 → " +
                         "Свойства → поле «Параметры запуска». " +
                         string.Join("; ", harmful.Select(h => h.Advice)) + ". " +
                         "Полезным там остаётся только -allow_third_party_software " +
                         "(он нужен для FACEIT и подобных площадок).";

            results.Add(CheckResult.Warn(Id + ".harmful", Title, detail, why,
                harmful.Select(h => new FixAction("cs2.launch." + h.Flag.TrimStart('-', '+'),
                    "Убрать " + h.Flag, FixRisk.ManualOnly,
                    "Правится только в свойствах игры в Steam.")).ToList(),
                advice));
        }
        else
        {
            results.Add(CheckResult.Ok(Id + ".harmful", Title,
                options.IsEmpty ? "Параметры запуска пусты — это нормально" : "Лишних параметров нет"));
        }

        // ------------------------------------------- отдельно про -allow_third_party_software
        if (!options.IsEmpty && !options.Contains("-allow_third_party_software"))
        {
            results.Add(CheckResult.Info(Id + ".third-party", "Флаг для игровых площадок",
                "-allow_third_party_software отсутствует",
                "Этот флаг разрешает сторонним площадкам (FACEIT, ESEA и подобным) " +
                "подключаться к игре. Без него на них не поиграть.",
                recommendation: "Ничего делать не нужно, если вы не играете на FACEIT и подобных " +
                "площадках. Если играете — добавьте флаг: Steam → Свойства CS2 → " +
                "«Параметры запуска» → допишите -allow_third_party_software. " +
                "Он немного снижает доверие аккаунта в подборе игроков Valve, поэтому " +
                "в матчмейкинге он не обязателен."));
        }

        return Task.FromResult<IReadOnlyList<CheckResult>>(results);
    }
}
