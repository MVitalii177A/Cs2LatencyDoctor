using System.Diagnostics;
using System.Net;
using System.Net.NetworkInformation;
using System.Text;

namespace Cs2LatencyDoctor.Core.Windows;

/// <summary>Каким способом удалось (или не удалось) замерить задержку.</summary>
public enum ProbeMethod
{
    /// <summary>Прямой ICMP через .NET — точный, даёт время с точностью до мс.</summary>
    DotNetIcmp = 0,

    /// <summary>Через ping.exe: значений задержки нет, но факт ответа известен.</summary>
    PingExe = 1,

    /// <summary>ICMP заблокирован (фаервол, антивирус, политика). Замер невозможен.</summary>
    Blocked = 2,

    /// <summary>
    /// Замер временем установления TCP-соединения. Работает там, где ICMP режут,
    /// и для игры даже показательнее: это тот же путь, по которому идёт игровой трафик.
    /// </summary>
    TcpConnect = 3
}

/// <summary>Итог замера задержки: не среднее, а распределение. Именно "пила" ломает ощущения в игре.</summary>
public sealed record LatencyProbeResult
{
    public required string Target { get; init; }
    public ProbeMethod Method { get; init; } = ProbeMethod.DotNetIcmp;

    public int Sent { get; init; }
    public int Received { get; init; }
    public int Lost => Sent - Received;

    public double MinMs { get; init; }
    public double AverageMs { get; init; }
    public double MedianMs { get; init; }
    public double MaxMs { get; init; }

    /// <summary>Стандартное отклонение — главная метрика ровности.</summary>
    public double StdDevMs { get; init; }

    /// <summary>Сколько ответов пришло медленнее порога.</summary>
    public int Spikes { get; init; }

    public double SpikeThresholdMs { get; init; }

    /// <summary>Уточнение способа замера: например, какой порт ответил при TCP-замере.</summary>
    public string? Detail { get; init; }

    public IReadOnlyList<double> Samples { get; init; } = Array.Empty<double>();

    public double LossPercent => Sent == 0 ? 0 : 100.0 * Lost / Sent;
    public double SpikePercent => Received == 0 ? 0 : 100.0 * Spikes / Received;

    /// <summary>ICMP не проходит вовсе — судить о сети по этому замеру нельзя.</summary>
    public bool IsInconclusive => Method == ProbeMethod.Blocked || Sent == 0;

    /// <summary>
    /// Узел не ответил ни разу. Это НЕ то же самое, что потери: так ведут себя
    /// роутеры и серверы, закрытые фаерволом, — замер просто не состоялся.
    /// </summary>
    public bool IsNoResponse => IsInconclusive || (Method == ProbeMethod.PingExe && Received == 0);

    /// <summary>true, если сеть до цели ровная: нет потерь и практически нет всплесков.</summary>
    public bool IsFlat(double maxSpikePercent = 2.0) =>
        !IsInconclusive && LossPercent == 0 && SpikePercent <= maxSpikePercent;
}

/// <summary>
/// Замер задержки через ICMP.
///
/// Интервал 1 секунда — как у обычного ping: частые запросы роутеры воспринимают
/// как флуд и сами начинают отвечать рывками, создавая ложную картину.
///
/// Два способа замера:
///   1. Прямой ICMP из .NET — точные миллисекунды.
///   2. Если raw-сокеты запрещены (фаервол, антивирус, политики) — ping.exe,
///      который умеет ходить через системный стек. Тогда задержку не измерить,
///      но потери видны.
/// Если не работает ни то, ни другое — честно сообщаем, что ICMP заблокирован,
/// а не рисуем "100% потерь".
/// </summary>
public static class LatencyProbe
{
    public static async Task<LatencyProbeResult> RunAsync(
        string target,
        int seconds,
        double spikeThresholdMs = 3.0,
        Action<int, int>? onTick = null,
        CancellationToken ct = default)
    {
        var addresses = Resolve(target);
        if (addresses.Count == 0)
            return new LatencyProbeResult { Target = target, Sent = 0, Received = 0, Method = ProbeMethod.Blocked };

        // Один пробный запрос решает, доступен ли ICMP вообще.
        // Если нет — вызывающая сторона переключится на TCP-замер.
        var primary = await TryDotNetIcmpAsync(addresses[0], ct);
        if (primary.Available)
            return await RunDotNetAsync(target, addresses[0], seconds, spikeThresholdMs, onTick, ct);

        return new LatencyProbeResult
        {
            Target = target,
            Sent = 0,
            Received = 0,
            Method = ProbeMethod.Blocked,
            SpikeThresholdMs = spikeThresholdMs
        };
    }

    private static List<IPAddress> Resolve(string target)
    {
        var list = new List<IPAddress>();

        if (IPAddress.TryParse(target, out var direct) && direct is not null)
        {
            list.Add(direct);
            return list;
        }

        try
        {
            list.AddRange(Dns.GetHostAddresses(target)
                .Where(a => a.AddressFamily == System.Net.Sockets.AddressFamily.InterNetwork));
        }
        catch
        {
            // не разрешилось — вернём пустой список
        }

        return list;
    }

    // ------------------------------------------------------------ способ 1
    private sealed record Attempt(bool Available, double RoundtripMs);

    private static async Task<Attempt> TryDotNetIcmpAsync(IPAddress address, CancellationToken ct)
    {
        try
        {
            using var ping = new Ping();
            var reply = await ping.SendPingAsync(address, TimeSpan.FromMilliseconds(2000));
            // Любой статус, кроме исключения, означает, что механизм работает.
            return new Attempt(true, reply.Status == IPStatus.Success ? reply.RoundtripTime : -1);
        }
        catch
        {
            return new Attempt(false, 0);
        }
    }

    private static async Task<LatencyProbeResult> RunDotNetAsync(
        string target, IPAddress address, int seconds, double spikeThresholdMs,
        Action<int, int>? onTick, CancellationToken ct)
    {
        var samples = new List<double>();
        using var ping = new Ping();
        var sent = 0;
        var consecutiveFailures = 0;

        for (var i = 0; i < seconds && !ct.IsCancellationRequested; i++)
        {
            sent++;
            var gotReply = false;

            try
            {
                var reply = await ping.SendPingAsync(address, TimeSpan.FromMilliseconds(1500));
                if (reply.Status == IPStatus.Success)
                {
                    samples.Add(reply.RoundtripTime);
                    gotReply = true;
                }
            }
            catch
            {
                // прерывание или потеря — просто нет сэмпла
            }

            consecutiveFailures = gotReply ? 0 : consecutiveFailures + 1;

            // Если ни один запрос не прошёл, ждать дальше бессмысленно:
            // ICMP блокируется в системе — сообщаем об этом сразу, а не через полминуты.
            if (consecutiveFailures >= 4 && samples.Count == 0)
            {
                onTick?.Invoke(sent, seconds);
                break;
            }

            onTick?.Invoke(i + 1, seconds);

            if (i < seconds - 1)
            {
                try { await Task.Delay(1000, ct); }
                catch (TaskCanceledException) { break; }
            }
        }

        if (samples.Count == 0)
        {
            return new LatencyProbeResult
            {
                Target = target,
                Method = ProbeMethod.Blocked,
                Sent = sent,
                Received = 0,
                SpikeThresholdMs = spikeThresholdMs
            };
        }

        return Build(target, sent, samples, spikeThresholdMs, ProbeMethod.DotNetIcmp);
    }

    // ------------------------------------------------------------ сборка
    private static LatencyProbeResult Build(
        string target, int sent, List<double> samples, double spikeThresholdMs, ProbeMethod method)
    {
        var ordered = samples.OrderBy(x => x).ToList();
        var median = ordered.Count == 0 ? 0 : ordered[ordered.Count / 2];
        var avg = ordered.Count == 0 ? 0 : ordered.Average();
        var stdDev = ordered.Count < 2
            ? 0
            : Math.Sqrt(ordered.Sum(v => Math.Pow(v - avg, 2)) / ordered.Count);

        return new LatencyProbeResult
        {
            Target = target,
            Method = method,
            Sent = sent,
            Received = samples.Count,
            MinMs = ordered.Count == 0 ? 0 : ordered[0],
            MaxMs = ordered.Count == 0 ? 0 : ordered[^1],
            AverageMs = Math.Round(avg, 2),
            MedianMs = median,
            StdDevMs = Math.Round(stdDev, 2),
            Spikes = samples.Count(v => v >= spikeThresholdMs),
            SpikeThresholdMs = spikeThresholdMs,
            Samples = samples
        };
    }

    /// <summary>
    /// Запуск консольной утилиты с чтением вывода в OEM-кодировке.
    /// Важно: русские версии ping/powercfg печатают в кодировке консоли (обычно 866),
    /// а не в UTF-8. Читаем сырые байты и декодируем правильно, иначе получаем кракозябры.
    /// </summary>
    internal static async Task<string?> RunProcessAsync(string fileName, string arguments, CancellationToken ct)
    {
        try
        {
            var psi = new ProcessStartInfo
            {
                FileName = fileName,
                Arguments = arguments,
                RedirectStandardOutput = true,
                RedirectStandardError = true,
                UseShellExecute = false,
                CreateNoWindow = true
            };

            using var process = Process.Start(psi);
            if (process is null) return null;

            // Читаем байты, а не строки: так кодировку выбираем мы, а не .NET.
            var buffer = new MemoryStream();
            await process.StandardOutput.BaseStream.CopyToAsync(buffer, ct);
            await process.WaitForExitAsync(ct);

            var bytes = buffer.ToArray();
            if (bytes.Length == 0) return string.Empty;

            return DecodeConsoleBytes(bytes);
        }
        catch
        {
            return null;
        }
    }

    /// <summary>Декодировать вывод консольной программы: пробуем UTF-8, при явной порче — OEM.</summary>
    internal static string DecodeConsoleBytes(byte[] bytes)
    {
        // Если байты — корректный UTF-8, декодируем как UTF-8.
        try
        {
            var strict = new UTF8Encoding(encoderShouldEmitUTF8Identifier: false, throwOnInvalidBytes: true);
            return strict.GetString(bytes);
        }
        catch (DecoderFallbackException)
        {
            // Иначе это OEM-кодировка консоли (866 на русской Windows).
            try
            {
                Encoding.RegisterProvider(CodePagesEncodingProvider.Instance);
                return Encoding.GetEncoding(866).GetString(bytes);
            }
            catch
            {
                return Encoding.Latin1.GetString(bytes);
            }
        }
    }
}
