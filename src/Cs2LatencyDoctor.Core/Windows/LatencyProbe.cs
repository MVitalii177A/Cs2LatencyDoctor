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

/// <summary>
/// Разбор ответа ping.exe.
///
/// Зачем это нужно. Программа умеет мерить задержку двумя способами: ICMP напрямую
/// и TCP-подключением. Бывает, что закрыты оба, а ping.exe всё равно проходит —
/// например, когда system-wide прокси перехватывает TCP, а ICMP отдаётся отдельно.
/// Миллисекунды оттуда не достать, но потери видно, а потери в игре важнее.
///
/// Вывод ping.exe локализован, поэтому ищем числа по смыслу строк, а не по словам:
/// строка с «потер» или «lost» содержит процент, а строка со «средн» или «Average»
/// содержит времена. Если разобрать не удалось — честно возвращаем «неизвестно»,
/// а не выдумываем числа.
/// </summary>
public static class PingExeParser
{
    /// <summary>Что удалось узнать из вывода ping.exe.</summary>
    /// <param name="Sent">Сколько пакетов отправлено. 0 — не удалось узнать.</param>
    /// <param name="Received">Сколько пришло. 0 — не удалось узнать.</param>
    /// <param name="TimesMs">
    /// Время каждого ответа в миллисекундах. Пусто, если вывод не разобрался:
    /// тогда известны только потери, а о задержке судить нельзя.
    /// </param>
    public readonly record struct Result(int Sent, int Received, IReadOnlyList<double> TimesMs)
    {
        public bool Parsed => Sent > 0;

        /// <summary>Известны ли времена ответов, а не только потери.</summary>
        public bool HasTimes => TimesMs.Count > 0;
    }

    public static Result Parse(string output)
    {
        if (string.IsNullOrWhiteSpace(output)) return new Result(0, 0, Array.Empty<double>());

        var lines = output.Split('\n', StringSplitOptions.RemoveEmptyEntries);

        var times = new List<double>();
        var sent = 0;
        var received = 0;

        foreach (var raw in lines)
        {
            var line = raw.Trim();

            // Время ответа: «time<1ms», «time=29ms», «время<1мс», «время=29мс».
            //
            // Случай «<1» важен отдельно: на быстрых каналах и до роутера Windows
            // всегда пишет «меньше миллисекунды». Без его разбора задержка до роутера
            // выглядела неизвестной, и программа переходила на TCP-замер, который
            // добавлял собственное дрожание в 1–3 мс — то есть придумывал проблему,
            // которой нет.
            var time = ParseTimeMs(line);
            if (time >= 0) times.Add(time);

            // Строка итогов: «Packets: Sent = 4, Received = 4, Lost = 0 (0% loss)»
            // или «Пакетов: отправлено = 4, получено = 4, потеряно = 0 (0% потерь)».
            var sentHere = FindNumberAfter(line, "Sent", "отправлено");
            var receivedHere = FindNumberAfter(line, "Received", "получено");

            if (sentHere > 0 && receivedHere >= 0)
            {
                sent = sentHere;
                received = receivedHere;
            }
        }

        // Если строку итогов не нашли, но ответы есть — считаем по ним: это лучше,
        // чем объявить замер неудачным.
        if (sent == 0 && times.Count > 0)
        {
            sent = times.Count;
            received = times.Count;
        }

        return new Result(sent, received, times);
    }

    /// <summary>
    /// Время ответа из строки. Возвращает -1, если строку разобрать не удалось.
    ///
    /// «time&lt;1ms» — это не «ноль», а «меньше миллисекунды». Возвращаем 0.5:
    /// так в расчёте джиттера не появляется ложный разброс, но и утверждения
    /// «ровно ноль» мы не делаем.
    /// </summary>
    private static double ParseTimeMs(string line)
    {
        foreach (var marker in new[] { "time<", "time=", "время<", "время=" })
        {
            var index = line.IndexOf(marker, StringComparison.OrdinalIgnoreCase);
            if (index < 0) continue;

            var less = marker.EndsWith('<');

            var rest = line[(index + marker.Length)..];

            var digits = new string(rest.SkipWhile(c => !char.IsDigit(c)).TakeWhile(char.IsDigit).ToArray());

            if (digits.Length == 0) continue;

            if (!double.TryParse(digits, out var value)) continue;

            return less ? 0.5 : value;
        }

        return -1;
    }

    /// <summary>
    /// Число, идущее после указанного слова. Проверяем оба языка: вывод ping.exe
    /// зависит от языка Windows, а не от языка программы.
    /// </summary>
    private static int FindNumberAfter(string line, string english, string russian)
    {
        foreach (var marker in new[] { english, russian })
        {
            var index = line.IndexOf(marker, StringComparison.OrdinalIgnoreCase);
            if (index < 0) continue;

            var rest = line[(index + marker.Length)..];
            var digits = new string(rest.SkipWhile(c => !char.IsDigit(c)).TakeWhile(char.IsDigit).ToArray());

            if (digits.Length > 0 && int.TryParse(digits, out var value)) return value;
        }

        return 0;
    }
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

        // ICMP напрямую недоступен. Прежде чем объявить замер невозможным, пробуем
        // ping.exe: бывает, что raw-сокеты закрыты, а утилите система отвечать разрешает.
        // Миллисекунд оттуда не достать, но потери видно — а потери в игре важнее.
        var viaPing = await RunPingExeAsync(target, seconds, spikeThresholdMs, onTick, ct);
        if (viaPing.Received > 0) return viaPing;

        return new LatencyProbeResult
        {
            Target = target,
            Sent = 0,
            Received = 0,
            Method = ProbeMethod.Blocked,
            SpikeThresholdMs = spikeThresholdMs
        };
    }

    /// <summary>
    /// Замер через ping.exe: потери видно, миллисекунды — нет.
    ///
    /// Вызывается как последний запасной способ, когда ICMP напрямую не проходит.
    /// Если и он не дал ответов, возвращаем результат с нулём полученных: вызывающая
    /// сторона сама решит, пробовать ли TCP.
    /// </summary>
    public static async Task<LatencyProbeResult> RunPingExeAsync(
        string target,
        int seconds,
        double spikeThresholdMs = 3.0,
        Action<int, int>? onTick = null,
        CancellationToken ct = default)
    {
        // ping.exe считает пакетами, а не секундами. Берём число запросов,
        // близкое к длительности замера, но не меньше четырёх: на одном пакете
        // о потерях судить нельзя.
        var count = Math.Clamp(seconds, 4, 20);

        var output = await RunProcessAsync("ping.exe", $"-n {count} {target}", ct);

        var parsed = PingExeParser.Parse(output ?? string.Empty);

        if (!parsed.Parsed)
        {
            return new LatencyProbeResult
            {
                Target = target,
                Sent = 0,
                Received = 0,
                Method = ProbeMethod.Blocked,
                SpikeThresholdMs = spikeThresholdMs
            };
        }

        onTick?.Invoke(parsed.Sent, parsed.Sent);

        // Времена ответов есть — считаем по ним всё, что нужно: медиану, разброс,
        // всплески. Раньше здесь стояли нули и подпись «время недоступно», и
        // вызывающая сторона уходила на TCP-замер. А TCP-подключение добавляет
        // к задержке собственное дрожание в 1–3 мс: на быстром канале программа
        // показывала джиттер до роутера 2 мс там, где его нет вовсе.
        if (parsed.HasTimes)
        {
            return Build(target, parsed.Sent, new List<double>(parsed.TimesMs), spikeThresholdMs, ProbeMethod.PingExe) with
            {
                Received = parsed.Received,
                Detail = "через ping.exe: время ответа с точностью до миллисекунды"
            };
        }

        return new LatencyProbeResult
        {
            Target = target,
            Method = ProbeMethod.PingExe,
            Sent = parsed.Sent,
            Received = parsed.Received,
            // Времён нет: видно только потери, о задержке судить нельзя.
            SpikeThresholdMs = spikeThresholdMs,
            Detail = "через ping.exe: видны только потери, время ответа недоступно"
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
