using System.Diagnostics;
using System.Net;
using System.Net.NetworkInformation;
using System.Net.Sockets;

namespace Cs2LatencyDoctor.NetWatch;

/// <summary>
/// Замер задержки и потерь до одного узла.
///
/// Почему через ping.exe, а не через класс Ping из .NET. На этой системе ICMP
/// из .NET требует прав администратора: без них «Отказано в доступе». Утилите
/// ping.exe система отвечать разрешает всегда. Поэтому запускаем её и читаем вывод.
///
/// Почему не TCP-подключением. Оно добавляет к задержке собственное дрожание
/// в 1–3 мс: открытие сокета, рукопожатие, планировщик. На быстром канале это
/// дрожание больше самой задержки, и оно записывается как «джиттер сети» —
/// программа начинает показывать проблему там, где её нет.
/// </summary>
public sealed class PingRunner
{
    private readonly string _target;

    public PingRunner(string target, string title)
    {
        _target = target;
        Title = title;
    }

    public string Title { get; }

    /// <summary>Один замер. Возвращает null, если ответа не было.</summary>
    public double? MeasureOnce(int timeoutMs = 1500)
    {
        try
        {
            var psi = new ProcessStartInfo("ping.exe", $"-n 1 -w {timeoutMs} {_target}")
            {
                RedirectStandardOutput = true,
                RedirectStandardError = true,
                UseShellExecute = false,
                CreateNoWindow = true
            };

            using var process = Process.Start(psi);

            if (process is null) return null;

            // Читаем байты и декодируем сами: русский ping.exe печатает в кодировке
            // консоли (обычно 866), а не в UTF-8. При чтении как UTF-8 получаются
            // кракозябры, и разбор времени не находит.
            var buffer = new MemoryStream();
            process.StandardOutput.BaseStream.CopyTo(buffer);

            if (!process.WaitForExit(timeoutMs + 2000))
            {
                try { process.Kill(); } catch { }
                return null;
            }

            var text = DecodeConsoleBytes(buffer.ToArray());

            return ParseTime(text);
        }
        catch
        {
            return null;
        }
    }

    /// <summary>
    /// Время ответа из вывода ping.exe. null — ответа не было.
    ///
    /// «время&lt;1мс» — это не ноль и не единица, а «меньше миллисекунды».
    /// Возвращаем 0,5: так в расчёте разброса не появляется ложное дрожание.
    /// </summary>
    public static double? ParseTime(string output)
    {
        if (string.IsNullOrWhiteSpace(output)) return null;

        foreach (var raw in output.Split('\n'))
        {
            var line = raw.Trim();

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
        }

        return null;
    }

    /// <summary>Декодировать вывод консольной программы: UTF-8, при явной порче — OEM.</summary>
    public static string DecodeConsoleBytes(byte[] bytes)
    {
        if (bytes.Length == 0) return string.Empty;

        EnsureConsoleEncoding();

        // Сначала пробуем кодировку консоли: она верна для русских версий утилит.
        if (ConsoleEncoding is not null)
        {
            try
            {
                var text = ConsoleEncoding.GetString(bytes);

                // Признак верного декодирования — находим знакомые слова.
                if (text.Contains("Ответ", StringComparison.Ordinal) ||
                    text.Contains("Reply", StringComparison.Ordinal) ||
                    text.Contains("Пакет", StringComparison.Ordinal) ||
                    text.Contains("Packets", StringComparison.Ordinal) ||
                    text.Contains("время", StringComparison.Ordinal) ||
                    text.Contains("time", StringComparison.OrdinalIgnoreCase))
                {
                    return text;
                }
            }
            catch
            {
                // переходим к UTF-8
            }
        }

        // Английские версии и современные утилиты обычно печатают в UTF-8.
        return System.Text.Encoding.UTF8.GetString(bytes);
    }

    private static System.Text.Encoding? ConsoleEncoding;

    private static void EnsureConsoleEncoding()
    {
        if (ConsoleEncoding is not null) return;

        try
        {
            System.Text.Encoding.RegisterProvider(System.Text.CodePagesEncodingProvider.Instance);
            ConsoleEncoding = System.Text.Encoding.GetEncoding(866);
        }
        catch
        {
            // Не вышло — будем читать как UTF-8.
        }
    }

    /// <summary>Разрешается ли узел: по имени или адресу.</summary>
    public bool Resolves()
    {
        if (IPAddress.TryParse(_target, out _)) return true;

        try
        {
            return Dns.GetHostAddresses(_target).Length > 0;
        }
        catch
        {
            return false;
        }
    }

    public override string ToString() => $"{Title} ({_target})";
}

/// <summary>Сведения о сетевом подключении: адрес, шлюз, адрес роутера по MAC.</summary>
public sealed record NetworkSnapshot(
    string InterfaceName,
    string LocalAddress,
    string Gateway,
    string RouterMac,
    bool IsWireless,
    long LinkSpeedBps);

public static class NetworkReader
{
    /// <summary>Снять сведения об активном подключении. null — сеть не найдена.</summary>
    public static NetworkSnapshot? Read()
    {
        try
        {
            var best = NetworkInterface.GetAllNetworkInterfaces()
                .Where(n => n.OperationalStatus == OperationalStatus.Up)
                .Where(n => n.NetworkInterfaceType != NetworkInterfaceType.Loopback)
                .Where(n => n.NetworkInterfaceType != NetworkInterfaceType.Tunnel)
                .Select(n => new { Nic = n, Props = n.GetIPProperties() })
                .Where(x => x.Props.GatewayAddresses.Count > 0)
                .Where(x => x.Props.UnicastAddresses.Any(a => a.Address.AddressFamily == AddressFamily.InterNetwork))
                .OrderBy(x => x.Nic.NetworkInterfaceType == NetworkInterfaceType.Ethernet ? 0 : 1)
                .FirstOrDefault();

            if (best is null) return null;

            var gateway = best.Props.GatewayAddresses
                .Select(g => g.Address)
                .FirstOrDefault(a => a.AddressFamily == AddressFamily.InterNetwork);

            if (gateway is null) return null;

            var local = best.Props.UnicastAddresses
                .Select(a => a.Address)
                .FirstOrDefault(a => a.AddressFamily == AddressFamily.InterNetwork);

            var speed = 0L;
            try { speed = best.Nic.Speed; } catch { }

            return new NetworkSnapshot(
                best.Nic.Name,
                local?.ToString() ?? "неизвестен",
                gateway.ToString(),
                ReadRouterMac(gateway.ToString()),
                best.Nic.NetworkInterfaceType == NetworkInterfaceType.Wireless80211,
                speed);
        }
        catch
        {
            return null;
        }
    }

    /// <summary>
    /// MAC-адрес роутера. По нему видно, не подменился ли роутер: если адрес
    /// сменился, значит в сети появилось другое устройство с теми же адресами.
    /// </summary>
    private static string ReadRouterMac(string gateway)
    {
        try
        {
            foreach (var line in RunArp())
            {
                // Формат: «192.168.31.1          xx-xx-xx-xx-xx-xx     динамический»
                var parts = line.Split(' ', StringSplitOptions.RemoveEmptyEntries);

                if (parts.Length < 2) continue;

                if (parts[0] != gateway) continue;

                var mac = parts[1].Replace('-', ':').ToUpperInvariant();

                return mac;
            }
        }
        catch
        {
            // не критично: MAC нужен только для дополнительной проверки
        }

        return "неизвестен";
    }

    private static string[] RunArp()
    {
        try
        {
            var psi = new ProcessStartInfo("arp.exe", "-a")
            {
                RedirectStandardOutput = true,
                UseShellExecute = false,
                CreateNoWindow = true
            };

            using var process = Process.Start(psi);

            if (process is null) return Array.Empty<string>();

            var buffer = new MemoryStream();
            process.StandardOutput.BaseStream.CopyTo(buffer);
            process.WaitForExit(3000);

            return PingRunner.DecodeConsoleBytes(buffer.ToArray())
                .Split('\n', StringSplitOptions.RemoveEmptyEntries);
        }
        catch
        {
            return Array.Empty<string>();
        }
    }
}
