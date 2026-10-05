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
/// Замер задержки: до роутера и до внешнего адреса.
/// Разделение принципиально: проблемы до роутера — это твоя машина,
/// проблемы дальше — провайдер, и они не лечатся настройками Windows.
/// </summary>
public sealed class NetworkLatencyCheck : IDiagnosticCheck
{
    private readonly NetworkPath? _pathOverride;

    public NetworkLatencyCheck(NetworkPath? pathOverride = null) => _pathOverride = pathOverride;

    public string Id => "net.latency";
    public string Title => "Задержка и джиттер сети";
    public bool RequiresAdmin => false;

    /// <summary>Внешний адрес для проверки. 1.1.1.1 выбран потому, что стабильно отвечает на ICMP.</summary>
    public string ExternalTarget { get; init; } = "1.1.1.1";

    public async Task<IReadOnlyList<CheckResult>> RunAsync(DiagnosticContext context, CancellationToken ct)
    {
        var results = new List<CheckResult>();
        var path = _pathOverride ?? NetworkPathResolver.Resolve();

        if (!string.IsNullOrEmpty(path.Gateway))
        {
            context.GatewayAddress = path.Gateway;
            context.PrimaryAdapterName = path.PrimaryAdapterName;

            context.Progress($"Замеряю задержку до роутера ({path.Gateway})…");
            var gw = await LatencyProbe.RunAsync(
                path.Gateway, context.ProbeSeconds, LatencyEvaluator.LocalSpikeThresholdMs, null, ct);

            results.Add(LatencyEvaluator.Evaluate(gw, Id + ".gateway", "до роутера",
                "Это твой участок: машина — кабель — роутер. Провайдер здесь ни при чём. " +
                "Если тут есть всплески, виновата сетевая карта, её настройки или сам роутер."));
        }
        else
        {
            results.Add(CheckResult.Skipped(Id + ".gateway", "Задержка до роутера",
                "Шлюз по умолчанию не найден"));
        }

        context.Progress($"Замеряю задержку до внешнего узла ({ExternalTarget})…");
        var wan = await LatencyProbe.RunAsync(ExternalTarget, context.ProbeSeconds, 0, null, ct);

        results.Add(LatencyEvaluator.Evaluate(LatencyEvaluator.ApplyWanThreshold(wan), Id + ".wan",
            "до интернета",
            "Это участок провайдера и магистрали. Настройками Windows он не лечится — " +
            "но важен как ориентир: если до роутера ровно, а тут пила, вопрос к провайдеру."));

        return results;
    }
}
