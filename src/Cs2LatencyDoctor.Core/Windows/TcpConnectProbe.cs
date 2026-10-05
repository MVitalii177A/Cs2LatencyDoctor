using System.Diagnostics;
using System.Net.Sockets;

namespace Cs2LatencyDoctor.Core.Windows;

/// <summary>Результат одного TCP-подключения.</summary>
public sealed record TcpConnectSample(bool Success, double Milliseconds, string? Error = null);

/// <summary>
/// Замер задержки через время установления TCP-соединения.
///
/// Зачем это нужно: ICMP (обычный ping) часто блокируется фаерволом, антивирусом
/// или политикой сети — и тогда инструмент честно говорит «измерить не удалось»,
/// но пользы пользователю от этого нет. TCP-подключение проходит почти всегда,
/// а измеряет оно ровно то же самое: полный путь до узла туда и обратно.
///
/// Дополнительный плюс: для игр важен именно TCP/UDP-путь, а не ICMP,
/// который многие маршрутизаторы обрабатывают с другим приоритетом.
/// </summary>
public static class TcpConnectProbe
{
    /// <summary>Порты для проверки, по порядку. 443 открыт почти везде.</summary>
    private static readonly int[] Ports = { 443, 80, 53 };

    /// <summary>
    /// Одно подключение с замером времени. Возвращает результат и порт, который ответил.
    /// </summary>
    public static async Task<(TcpConnectSample Sample, int Port)> TryConnectAsync(
        string host, int timeoutMs, CancellationToken ct)
    {
        string? lastError = null;
        var stopwatch = new Stopwatch();

        foreach (var port in Ports)
        {
            ct.ThrowIfCancellationRequested();

            using var client = new TcpClient { NoDelay = true };

            try
            {
                stopwatch.Restart();
                using var timeout = CancellationTokenSource.CreateLinkedTokenSource(ct);
                timeout.CancelAfter(timeoutMs);

                await client.ConnectAsync(host, port, timeout.Token);
                stopwatch.Stop();

                if (client.Connected)
                    return (new TcpConnectSample(true, stopwatch.Elapsed.TotalMilliseconds), port);
            }
            catch (OperationCanceledException) when (ct.IsCancellationRequested)
            {
                throw;
            }
            catch (Exception ex)
            {
                lastError = ex is OperationCanceledException
                    ? $"таймаут {timeoutMs} мс на порту {port}"
                    : $"{port}: {ex.Message}";
            }
        }

        return (new TcpConnectSample(false, 0, lastError ?? "ни один порт не ответил"), 0);
    }

    /// <summary>
    /// Замер серией: подключение раз в секунду, как обычный ping.
    /// Чаще нельзя — роутеры и серверы воспринимают частые подключения как атаку
    /// и сами начинают отвечать рывками, создавая ложную картину.
    /// </summary>
    public static async Task<LatencyProbeResult> RunAsync(
        string host,
        int seconds,
        double spikeThresholdMs,
        Action<int, int>? onTick,
        CancellationToken ct = default)
    {
        var samples = new List<double>();
        var consecutiveFailures = 0;
        var sent = 0;
        var port = 0;

        for (var i = 0; i < seconds && !ct.IsCancellationRequested; i++)
        {
            sent++;

            var (sample, usedPort) = await TryConnectAsync(host, timeoutMs: 1500, ct);

            if (sample.Success)
            {
                samples.Add(sample.Milliseconds);
                port = usedPort;
                consecutiveFailures = 0;
            }
            else
            {
                consecutiveFailures++;
            }

            onTick?.Invoke(i + 1, seconds);

            // Если ни одно подключение не прошло, дальше ждать бессмысленно.
            if (consecutiveFailures >= 4 && samples.Count == 0) break;

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
                Target = host,
                Method = ProbeMethod.Blocked,
                Sent = sent,
                Received = 0,
                SpikeThresholdMs = spikeThresholdMs
            };
        }

        var ordered = samples.OrderBy(x => x).ToList();
        var average = ordered.Average();
        var stdDev = ordered.Count < 2
            ? 0
            : Math.Sqrt(ordered.Sum(v => Math.Pow(v - average, 2)) / ordered.Count);

        return new LatencyProbeResult
        {
            Target = host,
            Method = ProbeMethod.TcpConnect,
            Sent = sent,
            Received = samples.Count,
            MinMs = Math.Round(ordered[0], 2),
            MaxMs = Math.Round(ordered[^1], 2),
            AverageMs = Math.Round(average, 2),
            MedianMs = Math.Round(ordered[ordered.Count / 2], 2),
            StdDevMs = Math.Round(stdDev, 2),
            Spikes = samples.Count(v => v >= spikeThresholdMs),
            SpikeThresholdMs = spikeThresholdMs,
            Samples = samples,
            Detail = port > 0 ? $"порт {port}" : null
        };
    }
}
