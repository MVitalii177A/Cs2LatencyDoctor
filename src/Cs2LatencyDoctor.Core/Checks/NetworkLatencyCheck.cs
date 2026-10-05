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

            results.Add(LatencyEvaluator.Evaluate(gatewayProbe, Id + ".gateway", "до роутера", gatewayWhy));
        }
        else
        {
            results.Add(CheckResult.Skipped(Id + ".gateway", "Задержка до роутера",
                "Шлюз по умолчанию не найден"));
        }

        // --------------------------------------------------------- до интернета
        // Здесь сразу TCP: во-первых, ICMP до внешних узлов режут чаще всего,
        // во-вторых, TCP-путь — это ровно тот путь, по которому идёт игровой трафик.
        context.Progress($"Замеряю задержку до внешнего узла ({ExternalTarget})…");

        var wanProbe = await TcpConnectProbe.RunAsync(
            ExternalTarget, context.ProbeSeconds, 0, null, ct);

        if (wanProbe.IsInconclusive)
        {
            context.Progress($"TCP не прошёл, пробую ICMP до {ExternalTarget}…");
            var viaIcmp = await LatencyProbe.RunAsync(ExternalTarget, context.ProbeSeconds, 0, null, ct);
            if (!viaIcmp.IsInconclusive) wanProbe = viaIcmp;
        }

        results.Add(LatencyEvaluator.Evaluate(
            LatencyEvaluator.ApplyWanThreshold(wanProbe), Id + ".wan", "до интернета", wanWhy));

        return results;
    }
}
