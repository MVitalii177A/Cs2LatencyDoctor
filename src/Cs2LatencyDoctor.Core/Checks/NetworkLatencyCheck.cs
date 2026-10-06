using System.Net.NetworkInformation;
using Cs2LatencyDoctor.Core.Windows;

namespace Cs2LatencyDoctor.Core.Checks;

/// <summary>Куда идёт трафик: шлюз (роутер), внешний адрес, основной адаптер.</summary>
public sealed class NetworkPath
{
    public string? Gateway { get; init; }
    public string? PrimaryAdapterName { get; init; }
    public string? PrimaryAdapterDescription { get; init; }
    public string? LocalAddress { get; init; }
    public bool HasDefaultRoute => !string.IsNullOrEmpty(Gateway);
}

public static class NetworkPathResolver
{
    /// <summary>
    /// Найти шлюз по умолчанию и адаптер, через который он доступен.
    /// Берём маршрут 0.0.0.0/0 с наименьшей метрикой; при равенстве предпочитаем проводной.
    /// </summary>
    public static NetworkPath Resolve()
    {
        string? gateway = null;
        string? adapterName = null;
        string? adapterDescription = null;
        string? localAddress = null;
        var bestScore = int.MaxValue;

        try
        {
            foreach (var nic in NetworkInterface.GetAllNetworkInterfaces())
            {
                if (nic.OperationalStatus != OperationalStatus.Up) continue;
                if (nic.NetworkInterfaceType is NetworkInterfaceType.Loopback
                    or NetworkInterfaceType.Tunnel) continue;

                var props = nic.GetIPProperties();
                var defaultGateway = props.GatewayAddresses
                    .FirstOrDefault(g => g.Address.AddressFamily ==
                        System.Net.Sockets.AddressFamily.InterNetwork);
                if (defaultGateway is null) continue;

                var index = 0;
                try { index = props.GetIPv4Properties()?.Index ?? 0; } catch { /* не критично */ }

                var score = index + (nic.NetworkInterfaceType == NetworkInterfaceType.Ethernet ? 0 : 1000);
                if (score >= bestScore) continue;

                bestScore = score;
                gateway = defaultGateway.Address.ToString();
                adapterName = nic.Name;
                adapterDescription = nic.Description;
                localAddress = props.UnicastAddresses
                    .FirstOrDefault(u => u.Address.AddressFamily ==
                        System.Net.Sockets.AddressFamily.InterNetwork)?.Address.ToString();
            }
        }
        catch
        {
            // вернём то, что успели собрать
        }

        return new NetworkPath
        {
            Gateway = gateway,
            PrimaryAdapterName = adapterName,
            PrimaryAdapterDescription = adapterDescription,
            LocalAddress = localAddress
        };
    }
}

/// <summary>
/// Замер задержки до роутера и до интернета.
///
/// Два участка разделены принципиально: проблемы до роутера — это твоя машина,
/// проблемы дальше — провайдер, и настройками Windows они не лечатся.
///
/// Способы замера идут по убыванию точности, и это не прихоть: ICMP часто
/// блокируется фаерволом или антивирусом, а пользователю нужен ответ, а не отговорка.
///   1. ICMP напрямую — точные миллисекунды.
///   2. TCP-подключение — работает почти всегда и идёт тем же путём, что игровой трафик.
///   3. ping.exe — видит только факт ответа, миллисекунды недоступны.
/// Если не сработало ничего — честно сообщаем, что измерить не удалось.
/// </summary>
public sealed class NetworkLatencyCheck : IDiagnosticCheck
{
    private readonly NetworkPath? _pathOverride;

    public NetworkLatencyCheck(NetworkPath? pathOverride = null) => _pathOverride = pathOverride;

    public string Id => "net.latency";
    public string Title => "Задержка и джиттер сети";
    public bool RequiresAdmin => false;

    /// <summary>Внешний адрес для проверки. 1.1.1.1 стабильно отвечает и по ICMP, и по TCP.</summary>
    public string ExternalTarget { get; init; } = "1.1.1.1";

    public async Task<IReadOnlyList<CheckResult>> RunAsync(DiagnosticContext context, CancellationToken ct)
    {
        var results = new List<CheckResult>();
        var path = _pathOverride ?? NetworkPathResolver.Resolve();

        const string gatewayWhy =
            "Это твой участок: машина — кабель — роутер. Провайдер здесь ни при чём. " +
            "Если тут есть всплески, виновата сетевая карта, её настройки или сам роутер.";

        const string wanWhy =
            "Это участок провайдера и магистрали. Настройками Windows он не лечится — " +
            "но важен как ориентир: если до роутера ровно, а тут пила, вопрос к провайдеру.";

        // Советы разные: до роутера виновата своя техника, дальше — провайдер.
        const string gatewayAdvice =
            "Программа может исправить настройки сетевой карты, но не всё зависит от неё. " +
            "Что делать по порядку: " +
            "1) нажмите «Применить исправления» — программа отключит энергосбережение и модерацию " +
            "прерываний и перезапустит адаптер; " +
            "2) если всплески остались, замените сетевой кабель и попробуйте другой порт роутера; " +
            "3) перезагрузите роутер (выключить из розетки на 30 секунд); " +
            "4) если ничего не помогло — всплески даёт сам роутер: проверьте в его настройках " +
            "энергосбережение, Wi-Fi-модуль (если играете по кабелю, его можно отключить) и " +
            "включённые функции «ускорения игр».";

        const string wanAdvice =
            "Программа не может исправить этот участок — он вне вашего компьютера. " +
            "Что делать: " +
            "1) сравните с проверкой «Задержка до роутера»: если там ровно, а здесь пила — " +
            "проблема у провайдера, и это уже не ваша техника; " +
            "2) позвоните провайдеру и назовите цифры из этой проверки (медиана, разброс, всплески): " +
            "с конкретными числами разговор идёт иначе, чем «интернет лагает»; " +
            "3) проверьте, не занят ли канал: торренты, обновления Windows, облачные синхронизаторы, " +
            "чужие устройства в сети. Программа умеет ставить их на паузу — кнопка «Фоновые программы»; " +
            "4) если провайдер предлагает «игровой тариф» с другим маршрутом — это единственное, " +
            "что реально меняет этот участок.";

        // ---------------------------------------------------------- до роутера
        if (!string.IsNullOrEmpty(path.Gateway))
        {
            context.GatewayAddress = path.Gateway;
            context.PrimaryAdapterName = path.PrimaryAdapterName;

            context.Progress($"Замеряю задержку до роутера ({path.Gateway})…");

            var gatewayProbe = await LatencyProbe.RunAsync(
                path.Gateway, context.ProbeSeconds, LatencyEvaluator.LocalSpikeThresholdMs, null, ct);

            // ICMP не прошёл — пробуем TCP. Роутер слушает 80/443 практически всегда.
            if (gatewayProbe.IsInconclusive)
            {
                context.Progress($"ICMP недоступен, замеряю до роутера через TCP ({path.Gateway})…");
                var viaTcp = await TcpConnectProbe.RunAsync(
                    path.Gateway, context.ProbeSeconds, LatencyEvaluator.LocalSpikeThresholdMs, null, ct);

                if (!viaTcp.IsInconclusive) gatewayProbe = viaTcp;
            }

            results.Add(LatencyEvaluator.Evaluate(gatewayProbe, Id + ".gateway", "до роутера",
                gatewayWhy, gatewayAdvice));
        }
        else
        {
            results.Add(CheckResult.Skipped(Id + ".gateway", "Задержка до роутера",
                "Шлюз по умолчанию не найден",
                NoHelpReason.OutsideThisPc,
                "Программа не может измерить задержку до роутера, потому что система не видит " +
                "шлюз по умолчанию. Что делать: проверьте, что сеть вообще подключена " +
                "(Параметры → Сеть и Интернет), и что адрес выдаётся автоматически. " +
                "Смотрите проверку «Задержка до интернета» — она работает независимо."));
        }

        // --------------------------------------------------------- до интернета
        // Замеряем несколько направлений, а не один адрес. Один адрес показывает качество
        // одного конкретного маршрута: игровой сервер может быть совсем в другую сторону
        // и работать иначе. Три независимых сети дают картину — плохо везде или в одном месте.
        var targets = ProbeTargets.External;
        var perTargetSeconds = Math.Max(3, context.ProbeSeconds / Math.Max(1, targets.Count));

        var probeResults = new List<(ProbeTarget Target, Windows.LatencyProbeResult Result)>();

        foreach (var target in targets)
        {
            if (ct.IsCancellationRequested) break;

            context.Progress($"Замеряю {target.Title} ({target.Host})…");

            // Здесь сразу TCP: ICMP до внешних узлов режут чаще всего, а TCP-путь —
            // это ровно тот путь, по которому идёт игровой трафик.
            var probe = await TcpConnectProbe.RunAsync(
                target.Host, perTargetSeconds, 0, null, ct);

            if (probe.IsInconclusive)
                probe = await LatencyProbe.RunAsync(target.Host, perTargetSeconds, 0, null, ct);

            probeResults.Add((target, probe));
        }

        var measured = probeResults.Where(p => !p.Result.IsInconclusive).ToList();

        if (measured.Count == 0)
        {
            // Ни один узел не ответил: говорим честно, а не выдаём это за потери.
            var first = probeResults.FirstOrDefault();

            results.Add(CheckResult.Skipped(Id + ".wan", "Задержка до интернета",
                "Ни один из проверенных узлов не ответил",
                NoHelpReason.BlockedBySystem,
                "Программа не смогла измерить задержку: соединения не проходят ни по ICMP, " +
                "ни по TCP. Это не значит, что интернета нет — чаще всего так ведёт себя " +
                "фаервол, антивирус или провайдер, который фильтрует такие подключения. " +
                "Что делать: 1) проверьте, открываются ли сайты в браузере; " +
                "2) временно отключите антивирус и повторите проверку; " +
                $"3) если браузер работает, а программа не может подключиться к {first.Target.Host}, " +
                "дело в фильтрации, а не в сети."));

            return results;
        }

        // Основной вердикт — по самому быстрому направлению: это лучший путь до сети,
        // который у машины сейчас есть.
        var best = measured.OrderBy(p => p.Result.MedianMs).First();

        results.Add(LatencyEvaluator.Evaluate(
            LatencyEvaluator.ApplyWanThreshold(best.Result), Id + ".wan", "до интернета",
            wanWhy, wanAdvice));

        // Сравнение направлений: показывает, ровно ли везде или где-то хуже.
        results.Add(BuildComparison(measured, perTargetSeconds));

        return results;
    }

    /// <summary>
    /// Сравнение направлений. Разброс между узлами в разных сетях — ориентир:
    /// если один сильно хуже остальных, дело в конкретном маршруте, а не в канале.
    /// </summary>
    private static CheckResult BuildComparison(
        List<(ProbeTarget Target, Windows.LatencyProbeResult Result)> measured, int secondsPerTarget)
    {
        var ordered = measured.OrderBy(p => p.Result.MedianMs).ToList();
        var best = ordered[0];
        var worst = ordered[^1];

        var spread = Math.Round(worst.Result.MedianMs - best.Result.MedianMs, 1);

        var text = string.Join(", ", ordered.Select(p => $"{p.Target.Title} {p.Result.MedianMs:0.#}"));
        var detail = $"Медианы: {text} мс. Разброс {spread:0.#} мс";

        var metrics = new Dictionary<string, double>
        {
            ["wan_nodes_measured"] = ordered.Count,
            ["wan_spread_ms"] = spread,
            ["wan_best_median_ms"] = best.Result.MedianMs,
            ["wan_worst_median_ms"] = worst.Result.MedianMs
        };

        // Порог 30 мс: узел, который хуже лучшего на столько, почти наверняка
        // находится в другом направлении, а не «медленнее» сам по себе.
        const double noticeableSpreadMs = 30.0;

        if (spread < noticeableSpreadMs)
        {
            return new CheckResult
            {
                Id = "net.latency.nodes",
                Title = "Задержка до разных узлов",
                Severity = Severity.Ok,
                Detail = detail,
                Why = $"Проверены три независимых направления по {secondsPerTarget} сек. " +
                      "Ровные значения означают, что канал работает одинаково во все стороны.",
                Metrics = metrics
            };
        }

        return new CheckResult
        {
            Id = "net.latency.nodes",
            Title = "Задержка до разных узлов",
            Severity = Severity.Info,
            Detail = detail,
            Why = $"Проверены три независимых направления по {secondsPerTarget} сек. " +
                  $"«{worst.Target.Title}» отвечает на {spread:0.#} мс медленнее, чем " +
                  $"«{best.Target.Title}»: это разные маршруты, и один из них длиннее.",
            Recommendation = "Ничего делать не нужно: узлы находятся в разных сетях, " +
                             "и разница между ними — нормальное явление. Важно другое: " +
                             "если бы плохо было во всех направлениях сразу, вопрос был бы " +
                             "к вашему каналу. Здесь канал работает ровно, а разница " +
                             "объясняется удалённостью узлов.",
            Metrics = metrics
        };
    }
}
