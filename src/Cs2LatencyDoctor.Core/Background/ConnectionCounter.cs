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
    /// <summary>Сколько соединений держит каждый процесс: идентификатор -> количество.</summary>
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

    /// <summary>Строка таблицы: локальный и удалённый адрес плюс идентификатор процесса-владельца.</summary>
    private readonly record struct TcpRow(int ProcessId, IPAddress RemoteAddress, int RemotePort);

    private static List<TcpRow> ReadIpv4Table()
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

                var remoteAddress = new IPAddress(row.RemoteAddress);
                var remotePort = (ushort)IPAddress.NetworkToHostOrder((short)row.RemotePort);

                rows.Add(new TcpRow((int)row.OwningProcessId, remoteAddress, remotePort));
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
