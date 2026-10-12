using System.Diagnostics;
using Cs2LatencyDoctor.Core.Background;

namespace Cs2LatencyDoctor.Core.Checks;

/// <summary>
/// Сколько соединений держит компьютер и не переполнит ли он таблицу роутера.
///
/// Зачем эта проверка. Роутер ведёт таблицу соответствий между внутренними
/// и внешними адресами. Каждое открытое соединение занимает в ней строку.
/// Когда строки заканчиваются, роутер перестаёт пропускать трафик — и связь
/// пропадает сразу у всех устройств, а не только у игры.
///
/// Это не теория. Наблюдение за реальным сбоем показало:
///     13:38  запущена программа, держащая десятки соединений
///     13:46  пропал интернет
///     14:08  роутер перестал отвечать (559 соединений)
///     14:10  связь вернулась
///     Итого: 24 минуты без интернета
///
/// Тогда программа показывала «это влияет на задержку». На деле связь
/// пропала полностью на 24 минуты — это разные вещи, и человек должен
/// узнать о риске заранее, а не после.
///
/// Чего эта проверка не может. Точный размер таблицы у конкретного роутера
/// узнать нельзя: производители его не публикуют, а универсального способа
/// запросить его нет. Поэтому пороги взяты по наблюдениям и объяснены
/// в отчёте как ориентир, а не как измеренная величина.
/// </summary>
public sealed class ConnectionTableCheck : IDiagnosticCheck
{
    public string Id => "net.connections";
    public string Title => "Нагрузка на таблицу соединений роутера";

    /// <summary>Права не нужны: таблицу соединений видно и обычному пользователю.</summary>
    public bool RequiresAdmin => false;

    /// <summary>
    /// Ниже этого числа беспокоиться не о чем.
    /// Обычный рабочий стол с браузером и мессенджерами держит 70–200 соединений.
    /// </summary>
    private const int Comfortable = 300;

    /// <summary>С этого числа начинается заметный риск для домашних роутеров.</summary>
    private const int Risky = 450;

    /// <summary>Столько было в момент наблюдавшегося сбоя — связь пропала.</summary>
    private const int ObservedFailure = 559;

    public Task<IReadOnlyList<CheckResult>> RunAsync(DiagnosticContext context, CancellationToken ct)
    {
        var results = new List<CheckResult>();

        context.Progress("Считаю сетевые соединения…");

        var byProcess = ConnectionCounter.CountByProcess();
        var total = byProcess.Values.Sum();

        if (total == 0)
        {
            results.Add(CheckResult.Skipped(Id, Title,
                "Системная таблица соединений недоступна",
                NoHelpReason.BlockedBySystem,
                "Программа не смогла прочитать таблицу сетевых соединений Windows. " +
                "Обычно это значит, что доступ закрыт антивирусом или политикой безопасности. " +
                "Что делать: проверку можно пропустить — остальные проверки работают. " +
                "Если хотите увидеть картину вручную, откройте командную строку от администратора " +
                "и введите: netstat -ano | find /c \"TCP\""));
            return Task.FromResult<IReadOnlyList<CheckResult>>(results);
        }

        // --- общая картина ---
        //
        // Берём состояния только по активным соединениям: иначе цифры в отчёте
        // не сойдутся (например, «всего 68, а закрывается 113»).
        var byState = ConnectionCounter.CountByStateActive();

        var working = byState.TryGetValue("работает", out var w) ? w : 0;
        var forming = byState.TryGetValue("устанавливается", out var f) ? f : 0;

        var parts = new List<string>();
        if (working > 0) parts.Add($"работают {working}");
        if (forming > 0) parts.Add($"устанавливаются {forming}");

        var stateText = parts.Count > 0 ? string.Join(", ", parts) : "состояния не определены";

        // Отдельно скажем про закрытые и слушающие порты: в счёт они не входят,
        // но человек видит их в диспетчере и может удивиться разнице.
        var allStates = ConnectionCounter.CountByStateAll();
        var hanging = allStates.TryGetValue("закрыто (висит)", out var h) ? h : 0;
        var listening = allStates.TryGetValue("порт слушается", out var l) ? l : 0;

        var notes = new List<string>();

        if (hanging > 0)
            notes.Add($"уже закрыты и ждут очистки: {hanging} — места в роутере они не занимают");

        if (listening > 0)
            notes.Add($"открыты и ждут входящих: {listening} — это не соединения, роутер ими не занят");

        var hangingNote = notes.Count > 0 ? ". Не входят в счёт — " + string.Join("; ", notes) : string.Empty;

        // --- кто держит больше всех ---
        var topText = DescribeTopTalkers(byProcess, total);

        var metrics = new Dictionary<string, double>
        {
            ["connections.total"] = total,
            ["connections.working"] = working,
            ["connections.forming"] = forming,
            ["connections.listening"] = listening,
            ["connections.hanging"] = hanging
        };

        // --- оценка ---
        if (total < Comfortable)
        {
            results.Add(new CheckResult
            {
                Id = Id,
                Title = Title,
                Severity = Severity.Ok,
                Detail = $"Соединений: {total} — это нормально ({stateText}){hangingNote}",
                Why = "Роутер свободно ведёт таблицу при таком числе соединений. " +
                      "Запас есть, обрывов по этой причине быть не должно.",
                Metrics = metrics
            });
        }
        else if (total < Risky)
        {
            results.Add(new CheckResult
            {
                Id = Id,
                Title = Title,
                Severity = Severity.Info,
                Detail = $"Соединений: {total} — близко к пределу домашнего роутера ({stateText}){hangingNote}",
                Why = "Домашние роутеры уверенно ведут примерно 300–500 соединений. " +
                      "Приближение к верхней границе само по себе игры не портит, но если " +
                      "связь когда-нибудь пропадала сразу на всех устройствах — причина может быть в этом.",
                Recommendation = TopRecommendation(topText),
                Metrics = metrics
            });
        }
        else if (total < ObservedFailure)
        {
            results.Add(new CheckResult
            {
                Id = Id,
                Title = Title,
                Severity = Severity.Warning,
                Detail = $"Соединений: {total} — высокая нагрузка на роутер ({stateText}){hangingNote}",
                Why = "При таком числе соединений домашние роутеры начинают сбоить: " +
                      "связь может пропасть сразу у всех устройств и вернуться только после " +
                      "перезагрузки роутера. В наблюдавшихся случаях это выглядело так: " +
                      "интернет пропадает, роутер не отвечает, через несколько минут всё возвращается.",
                Recommendation = TopRecommendation(topText),
                Metrics = metrics
            });
        }
        else
        {
            results.Add(new CheckResult
            {
                Id = Id,
                Title = Title,
                Severity = Severity.Problem,
                Detail = $"Соединений: {total} — столько же было при наблюдавшемся обрыве связи ({stateText}){hangingNote}",
                Why = "В зафиксированном случае при 559 соединениях роутер перестал отвечать " +
                      "и интернет пропал на 24 минуты — до тех пор, пока нагрузка не спала. " +
                      "Если такое случится во время матча, матч будет потерян. " +
                      "Это не про задержку: связь пропадает полностью.",
                Recommendation = TopRecommendation(topText),
                Metrics = metrics
            });
        }

        // --- отдельно: главный виновник нагрузки ---
        if (byProcess.Count > 0)
        {
            var heaviest = byProcess.OrderByDescending(p => p.Value).First();
            var heavyName = SafeName(heaviest.Key);
            var heavyShare = total > 0 ? heaviest.Value * 100 / total : 0;

            // Показываем отдельной строкой, только если программа действительно
            // держит заметную долю — иначе это шум в отчёте.
            if (heaviest.Value >= 25 && heavyShare >= 15)
            {
                results.Add(new CheckResult
                {
                    Id = Id + ".top",
                    Title = "Кто держит больше всего соединений",
                    Severity = heaviest.Value >= 50 ? Severity.Info : Severity.Ok,
                    Detail = $"{heavyName}: {heaviest.Value} соединений — это {heavyShare}% от всех",
                    Why = "Программы, которые обмениваются данными с десятками чужих компьютеров " +
                          "одновременно (пиринговые сети, торрент-клиенты, онлайн-кинотеатры " +
                          "с раздачей), держат много соединений постоянно. Именно они чаще всего " +
                          "переполняют таблицу роутера.",
                    Recommendation = heavyName.Contains("неизвестн", StringComparison.OrdinalIgnoreCase)
                        ? "Закройте эту программу на время игры и посмотрите, прекратятся ли обрывы."
                        : null,
                    Metrics = new Dictionary<string, double>
                    {
                        ["connections.top"] = heaviest.Value,
                        ["connections.top.share"] = heavyShare
                    }
                });
            }
        }

        return Task.FromResult<IReadOnlyList<CheckResult>>(results);
    }

    /// <summary>
    /// Совет по снижению нагрузки. Одинаков для всех уровней, потому что
    /// способ один: убрать программу, которая держит много соединений.
    /// </summary>
    private static string TopRecommendation(string topText) =>
        "Что делать: закройте программы, которые держат много сетевых соединений — " +
        "пиринговые клиенты, торрент-клиенты, онлайн-кинотеатры с раздачей. " +
        "Перед игрой их лучше выключать полностью (правый щелчок по значку в трее → «Выход»), " +
        "а не сворачивать: свёрнутая программа продолжает держать соединения. " +
        (string.IsNullOrEmpty(topText) ? string.Empty : "Больше всех сейчас держит: " + topText + ". ");

    /// <summary>Перечень главных «едоков» соединений — коротко, для строки отчёта.</summary>
    private static string DescribeTopTalkers(Dictionary<int, int> byProcess, int total)
    {
        try
        {
            var top = byProcess
                .OrderByDescending(p => p.Value)
                .Take(4)
                .Select(p => SafeName(p.Key) + " (" + p.Value + ")")
                .ToList();

            return top.Count > 0 ? string.Join(", ", top) : string.Empty;
        }
        catch
        {
            return string.Empty;
        }
    }

    /// <summary>
    /// Имя программы по идентификатору процесса. Если процесса уже нет или
    /// доступ закрыт — говорим об этом прямо, а не показываем пустоту.
    /// </summary>
    private static string SafeName(int processId)
    {
        try
        {
            using var process = Process.GetProcessById(processId);
            return process.ProcessName;
        }
        catch
        {
            return "неизвестная программа (id " + processId + ")";
        }
    }
}
