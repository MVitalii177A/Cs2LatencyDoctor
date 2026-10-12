using System.Net;
using System.Runtime.InteropServices;

namespace Cs2LatencyDoctor.Core.Background;
/// <summary>
/// Сколько сетевых соединений держит каждый процесс.
///
/// Зачем это переписано. Раньше количество соединений считалось так: брались
/// идентификаторы процессов и искались среди НОМЕРОВ ПОРТОВ активных соединений.
/// Порт и идентификатор процесса — разные числа, и совпадение между ними ничего
/// не значит: программа показывала случайные величины. Теперь владелец соединения
/// берётся из системной таблицы TCP, то есть по-настоящему.
/// </summary>
public static class ConnectionCounter
{
    /// <summary>
    /// Состояния, при которых соединение занимает строку в таблице роутера.
    ///
    /// Коды состояний — из системы, а не по памяти. Значения проверены опытом:
    /// прочитали таблицу соединений и сопоставили коды с тем, что показывает
    /// система. Первая версия использовала неверные коды (сдвиг на единицу),
    /// из-за чего слушающие порты считались устанавливающимися соединениями,
    /// а настоящие рабочие соединения не считались вовсе.
    ///
    /// Что входит в подсчёт:
    ///   3  SYN_SENT     — соединение устанавливается
    ///   4  SYN_RECEIVED — соединение устанавливается
    ///   5  ESTABLISHED  — соединение работает
    ///
    /// Что НЕ входит и почему:
    ///   2  LISTEN    — порт открыт и ждёт входящих. Через роутер ничего не идёт,
    ///                  строку в его таблице не занимает. Это не соединение.
    ///   11 TIME_WAIT — соединение закрыто, роутер строку уже освободил.
    ///                  Windows держит запись ещё пару минут «на всякий случай»,
    ///                  и таких записей обычно больше всего: в наблюдении их было
    ///                  428 из 489, то есть 88%. Если их считать, число окажется
    ///                  завышенным почти в десять раз.
    ///   Остальные    — соединение закрывается, роутер освобождает строку.
    /// </summary>
    private static readonly HashSet<uint> ActiveStates = new() { 3, 4, 5 };

    /// <summary>Сколько активных соединений держит каждый процесс: идентификатор -> количество.</summary>
    public static Dictionary<int, int> CountByProcess()
    {
        var result = new Dictionary<int, int>();

        try
        {
            foreach (var row in ReadIpv4Table())
            {
                if (row.ProcessId <= 0) continue;

                result[row.ProcessId] = result.TryGetValue(row.ProcessId, out var count) ? count + 1 : 1;
            }
        }
        catch
        {
            // Таблица недоступна: вернём пустой словарь. В отчёте это будет видно
            // как ноль соединений, но лучше так, чем показывать выдуманные числа.
        }

        return result;
    }

    /// <summary>
    /// Общее число активных соединений в системе.
    ///
    /// Именно эта величина важна для роутера: он ведёт общую таблицу
    /// соответствий, и когда она переполняется — связь пропадает у всех сразу.
    /// </summary>
    public static int CountActive() => CountByProcess().Values.Sum();

    /// <summary>
    /// Сколько АКТИВНЫХ соединений в каждом состоянии.
    ///
    /// Важно: сюда попадают только те состояния, которые учитываются в общем
    /// числе. Иначе цифры в отчёте не сойдутся — например, «всего 68, из них
    /// закрывается 113». Закрытые соединения (они ещё висят в системе, но через
    /// роутер уже не идут) в подсчёт не входят, поэтому и здесь их нет.
    /// </summary>
    public static Dictionary<string, int> CountByStateActive()
    {
        var result = new Dictionary<string, int>();

        try
        {
            foreach (var row in ReadIpv4Table())
            {
                var name = DescribeState(row.State);
                result[name] = result.TryGetValue(name, out var count) ? count + 1 : 1;
            }
        }
        catch
        {
            // см. выше: пустой словарь честнее выдуманных чисел
        }

        return result;
    }

    /// <summary>Сколько соединений во всех состояниях, включая закрытые. Для справки в отчёте.</summary>
    public static Dictionary<string, int> CountByStateAll()
    {
        var result = new Dictionary<string, int>();

        try
        {
            foreach (var row in ReadIpv4Table(includeClosed: true))
            {
                var name = DescribeState(row.State);
                result[name] = result.TryGetValue(name, out var count) ? count + 1 : 1;
            }
        }
        catch
        {
            // не критично: справочные числа
        }

        return result;
    }

    /// <summary>
    /// Человеческое название состояния соединения.
    ///
    /// Коды соответствуют системным, проверенным опытом: 2 — порт слушается,
    /// 5 — соединение работает, 11 — закрыто и ждёт очистки. Первая версия
    /// использовала сдвинутые коды, поэтому в отчёте 32 слушающих порта
    /// назывались «устанавливающимися соединениями».
    /// </summary>
    private static string DescribeState(uint state) => state switch
    {
        1 => "закрыто",
        2 => "порт слушается",
        3 or 4 => "устанавливается",
        5 => "работает",
        6 or 7 or 8 or 9 or 10 => "закрывается",
        11 => "закрыто (висит)",
        12 => "удаляется",
        _ => "прочее"
    };

    /// <summary>Суммарное число соединений для набора процессов.</summary>
    public static int CountFor(IEnumerable<System.Diagnostics.Process> processes)
    {
        var byProcess = CountByProcess();
        var total = 0;

        foreach (var process in processes)
        {
            try
            {
                if (byProcess.TryGetValue(process.Id, out var count)) total += count;
            }
            catch
            {
                // Процесс завершился, пока мы считали: пропускаем.
            }
        }

        return total;
    }

    // ------------------------------------------------------- системная таблица TCP

    private const int AfInet = 2;
    private const int ErrorInsufficientBuffer = 122;
    private const int TcpTableOwnerPidAll = 5;

    [DllImport("iphlpapi.dll", SetLastError = true)]
    private static extern int GetExtendedTcpTable(
        IntPtr tcpTable,
        ref int size,
        bool order,
        int addressFamily,
        int tableClass,
        int reserved);

    /// <summary>Строка таблицы: состояние, адреса и идентификатор процесса-владельца.</summary>
    private readonly record struct TcpRow(int ProcessId, uint State, IPAddress RemoteAddress, int RemotePort);

    /// <summary>
    /// Читает системную таблицу соединений.
    ///
    /// includeClosed = false (по умолчанию) — только те соединения, что занимают
    /// место в таблице роутера. includeClosed = true — все, включая закрытые:
    /// они ещё висят в системе пару минут, но через роутер уже не идут.
    /// </summary>
    private static List<TcpRow> ReadIpv4Table(bool includeClosed = false)
    {
        var rows = new List<TcpRow>();

        var size = 0;
        var first = GetExtendedTcpTable(IntPtr.Zero, ref size, false, AfInet, TcpTableOwnerPidAll, 0);

        // Первый вызов всегда сообщает нужный размер буфера — это не ошибка.
        if (first != ErrorInsufficientBuffer || size <= 0) return rows;

        var buffer = Marshal.AllocHGlobal(size);

        try
        {
            var result = GetExtendedTcpTable(buffer, ref size, false, AfInet, TcpTableOwnerPidAll, 0);
            if (result != 0) return rows;

            // Структура: сначала количество записей (4 байта), затем сами записи.
            var count = Marshal.ReadInt32(buffer);
            var rowSize = Marshal.SizeOf<MibTcpRowOwnerPid>();
            var pointer = IntPtr.Add(buffer, 4);

            for (var i = 0; i < count; i++)
            {
                var row = Marshal.PtrToStructure<MibTcpRowOwnerPid>(pointer);
                pointer = IntPtr.Add(pointer, rowSize);

                // Закрытые соединения в счёт не идут: места в роутере они не занимают.
                if (!includeClosed && !ActiveStates.Contains(row.State)) continue;

                var remoteAddress = new IPAddress(row.RemoteAddress);
                var remotePort = (ushort)IPAddress.NetworkToHostOrder((short)row.RemotePort);

                rows.Add(new TcpRow((int)row.OwningProcessId, row.State, remoteAddress, remotePort));
            }
        }
        finally
        {
            Marshal.FreeHGlobal(buffer);
        }

        return rows;
    }

    /// <summary>
    /// Описание строки таблицы TCP, как её отдаёт Windows.
    /// Поля идут строго по порядку — менять их местами нельзя, иначе данные поедут.
    /// </summary>
    [StructLayout(LayoutKind.Sequential)]
    private struct MibTcpRowOwnerPid
    {
        public uint State;
        public uint LocalAddress;
        public uint LocalPort;
        public uint RemoteAddress;
        public uint RemotePort;
        public uint OwningProcessId;
    }
}
