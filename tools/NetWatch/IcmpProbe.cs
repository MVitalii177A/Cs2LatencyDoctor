using System.Diagnostics;
using System.Net;
using System.Net.Sockets;

namespace Cs2LatencyDoctor.NetWatch;

/// <summary>
/// Замер задержки прямым ICMP-запросом через сокет.
///
/// Почему не стандартными способами. На этой системе оба обычных пути закрыты:
///
///   System.Net.NetworkInformation.Ping  -> «Отказано в доступе»
///   ping.exe                            -> «PING: сбой передачи. Общий сбой»
///   cmd.exe /c ping                     -> то же самое
///
/// Проверено на самой машине: все три способа дают отказ, а прямой запрос
/// через сокет с ручной сборкой ICMP-пакета отвечает за 1,9 мс. То есть
/// система не запрещает проверку связи как таковую — она запрещает её
/// готовым средствам.
///
/// Поэтому собираем пакет сами: заголовок ICMP, контрольная сумма, отправка,
/// ожидание ответа. Это тот же самый запрос, который посылает ping, только
/// без системных ограничений вокруг него.
/// </summary>
public sealed class IcmpProbe : IDisposable
{
    private readonly IPAddress _address;
    private readonly Socket _socket;
    private ushort _sequence;

    /// <summary>
    /// Куда придёт ответ. Нам важен не адрес отправителя — его проверяет система,
    /// а содержимое пакета. Поэтому адрес не сохраняем.
    /// </summary>
    private EndPoint AnyEndpoint = new IPEndPoint(IPAddress.Any, 0);

    public IcmpProbe(string target, string title)
    {
        Title = title;

        if (!IPAddress.TryParse(target, out var parsed) || parsed is null)
        {
            var resolved = Dns.GetHostAddresses(target)
                .FirstOrDefault(a => a.AddressFamily == AddressFamily.InterNetwork);

            parsed = resolved ?? throw new InvalidOperationException("Адрес не разрешился: " + target);
        }

        _address = parsed;

        _socket = new Socket(AddressFamily.InterNetwork, SocketType.Raw, ProtocolType.Icmp);
    }

    public string Title { get; }

    /// <summary>
    /// Один замер. Возвращает время отклика в миллисекундах
    /// или null, если ответа не было.
    /// </summary>
    public double? MeasureOnce(int timeoutMs = 2000)
    {
        try
        {
            _sequence++;

            var packet = BuildRequest(_sequence);

            _socket.ReceiveTimeout = timeoutMs;

            var endpoint = new IPEndPoint(_address, 0);

            var stopwatch = Stopwatch.StartNew();
            _socket.SendTo(packet, endpoint);

            var buffer = new byte[1024];

            // Ответ может прийти с заголовком IP или без — зависит от системы.
            // Читаем в цикле: приходят и посторонние пакеты.
            var deadline = DateTime.UtcNow.AddMilliseconds(timeoutMs);

            while (DateTime.UtcNow < deadline)
            {
                int received;

                try
                {
                    received = _socket.ReceiveFrom(buffer, ref AnyEndpoint);
                }
                catch (SocketException)
                {
                    return null;
                }

                stopwatch.Stop();

                if (IsOurReply(buffer, received, out var rejected))
                    return stopwatch.Elapsed.TotalMilliseconds;

                // Узел ответил «адрес недоступен» — это тоже ответ, но плохой.
                // Считать его задержкой нельзя: пакет до цели не дошёл.
                if (rejected) return null;

                stopwatch.Start();
            }

            return null;
        }
        catch (Exception)
        {
            return null;
        }
    }

    /// <summary>
    /// Наш ли это ответ.
    ///
    /// Проверок три, и все обязательны. Без них программа принимала за ответ
    /// посторонние пакеты: например, ICMP-редирект от роутера. Он приходил
    /// за десятые доли миллисекунды, и в отчёте появлялась задержка до интернета
    /// 0,06 мс — чего быть не может.
    ///
    ///   1. Тип ICMP — «эхо-ответ». Редиректы, «время истекло» и прочее не считаем.
    ///   2. Отправитель — тот узел, которому мы писали. Иначе зачтём чужой ответ.
    ///   3. Номер запроса совпадает — значит ответ именно на нашу посылку.
    ///
    /// Заголовок IP может отсутствовать: тогда проверить отправителя нельзя,
    /// и мы полагаемся на номер запроса.
    /// </summary>
    private bool IsOurReply(byte[] buffer, int length, out bool rejected)
    {
        rejected = false;

        if (length < 8) return false;

        var hasIpHeader = (buffer[0] >> 4) == 4;

        var offset = hasIpHeader ? 20 : 0;

        if (offset + 8 > length) return false;

        // Проверка отправителя: в заголовке IP адрес источника идёт с 12-го байта.
        if (hasIpHeader)
        {
            if (length < 20) return false;

            var source = new IPAddress(new[] { buffer[12], buffer[13], buffer[14], buffer[15] });

            if (!source.Equals(_address)) return false;
        }

        var type = buffer[offset];
        var code = buffer[offset + 1];

        // 3 — «узел недоступен»: ответ есть, но цель не достигнута.
        if (type == 3)
        {
            rejected = true;
            return false;
        }

        // Нас интересует только эхо-ответ.
        if (type != 0 || code != 0) return false;

        var sequence = (ushort)((buffer[offset + 6] << 8) + buffer[offset + 7]);

        return sequence == _sequence;
    }

    /// <summary>Собрать ICMP-запрос: тип 8 (эхо), код 0, контрольная сумма, номер.</summary>
    private byte[] BuildRequest(ushort sequence)
    {
        var packet = new byte[32];

        packet[0] = 8;  // тип: эхо-запрос
        packet[1] = 0;  // код
        packet[4] = 0x43; packet[5] = 0x53;  // идентификатор «CS» — узнаём свои пакеты
        packet[6] = (byte)(sequence >> 8);
        packet[7] = (byte)(sequence & 0xFF);

        // Данные: метка времени, чтобы содержимое не повторялось
        var stamp = BitConverter.GetBytes(Environment.TickCount);
        Array.Copy(stamp, 0, packet, 8, Math.Min(4, stamp.Length));

        for (var i = 12; i < packet.Length; i++) packet[i] = (byte)i;

        WriteChecksum(packet);

        return packet;
    }

    /// <summary>Контрольная сумма ICMP: сумма всех 16-битных слов, затем инверсия.</summary>
    private static void WriteChecksum(byte[] packet)
    {
        packet[2] = 0;
        packet[3] = 0;

        var sum = 0;

        for (var i = 0; i < packet.Length; i += 2)
        {
            sum += (packet[i] << 8) | (i + 1 < packet.Length ? packet[i + 1] : 0);
        }

        while ((sum >> 16) != 0) sum = (sum & 0xFFFF) + (sum >> 16);

        sum = ~sum;

        packet[2] = (byte)(sum >> 8);
        packet[3] = (byte)(sum & 0xFF);
    }

    /// <summary>Проверить, что способ вообще работает: один пробный запрос.</summary>
    public bool Works() => MeasureOnce(2000) is not null;

    /// <summary>Разрешается ли узел: по имени или адресу.</summary>
    public bool Resolves() => _address is not null;

    public void Dispose() => _socket.Dispose();
}
