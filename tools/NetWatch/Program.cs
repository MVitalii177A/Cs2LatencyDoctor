using System.Diagnostics;
using System.Globalization;
using System.Text;

namespace Cs2LatencyDoctor.NetWatch;

/// <summary>
/// NetWatch — наблюдение за сетью и роутером.
///
/// Зачем это нужно. Интернет периодически пропадает так, что роутер приходится
/// выключать и включать. Поймать такой момент наугад нельзя: он длится минуту,
/// а случается раз в день. Эта программа следит постоянно и записывает всё,
/// что нужно для разбора: когда пропало, что именно пропало, что было до этого.
///
/// Ключевое различие, которое она делает. Пропажа интернета при работающем
/// роутере — это проблема провайдера. Пропажа вместе с роутером — проблема
/// роутера. Это два совершенно разных виновника, и без разделения искать
/// нечего.
///
/// Программа ничего не меняет в системе: только измеряет и пишет файл.
/// </summary>
public static class Program
{
    private const string Version = "1.0.0";

    /// <summary>Сколько ждать между замерами. Секунда — чтобы поймать короткий обрыв.</summary>
    private static readonly TimeSpan CheckInterval = TimeSpan.FromSeconds(1);

    /// <summary>Как часто писать полное состояние, когда всё в порядке.</summary>
    private static readonly TimeSpan HeartbeatInterval = TimeSpan.FromSeconds(30);

    private static readonly string[] ExternalHosts = { "1.1.1.1", "8.8.8.8" };

    private static string _logPath = string.Empty;
    private static string _statePath = string.Empty;
    private static readonly StringBuilder Pending = new();

    public static async Task<int> Main(string[] args)
    {
        Console.OutputEncoding = Encoding.UTF8;
        Console.Title = "NetWatch — наблюдение за сетью";

        // Куда писать. Рядом с программой — человек найдёт файл там же,
        // где саму программу.
        var directory = AppContext.BaseDirectory;
        var stamp = DateTime.Now.ToString("yyyy-MM-dd-HH-mm");

        _logPath = Path.Combine(directory, $"сеть-{stamp}.log");
        _statePath = Path.Combine(directory, "netwatch-состояние.txt");

        PrintHeader();

        var network = NetworkReader.Read();

        if (network is null)
        {
            Console.WriteLine("  СЕТЬ НЕ НАЙДЕНА. Проверьте, что кабель подключён или Wi-Fi включён.");
            return 2;
        }

        var gateway = new IcmpProbe(network.Gateway, "роутер");
        var externals = ExternalHosts.Select(h => new IcmpProbe(h, h)).ToArray();

        if (!gateway.Resolves())
        {
            Console.WriteLine("  АДРЕС РОУТЕРА НЕ ОТВЕЧАЕТ. Наблюдение невозможно.");
            return 2;
        }

        // Проверка перед началом. Без неё программа, которой система запрещает
        // измерять, писала бы «обрыв» каждую секунду — и её отчёт был бы не
        // доказательством, а мусором. Лучше сразу сказать, что измерить не выходит.
        Console.WriteLine("  Проверяю, могу ли измерять…");

        var warmupRouter = gateway.MeasureOnce();
        var warmupInternet = externals[0].MeasureOnce();

        if (warmupRouter is null && warmupInternet is null)
        {
            Console.WriteLine();
            Console.WriteLine("  НЕ МОГУ ИЗМЕРЯТЬ ЗАДЕРЖКУ.");
            Console.WriteLine();
            Console.WriteLine("  Ни роутер, ни интернет не ответили на проверку. Возможные причины:");
            Console.WriteLine("    • система или антивирус запрещают программе проверку связи;");
            Console.WriteLine("    • сеть действительно не работает прямо сейчас.");
            Console.WriteLine();
            Console.WriteLine($"  Проверить вручную: откройте командную строку и введите");
            Console.WriteLine($"     ping {network.Gateway}");
            Console.WriteLine($"     ping {ExternalHosts[0]}");
            Console.WriteLine();
            Console.WriteLine("  Если ping вручную тоже не отвечает — сеть действительно не работает.");

            return 3;
        }

        Console.WriteLine($"  Измерение работает: роутер {Describe(warmupRouter)}, интернет {Describe(warmupInternet)}");
        Console.WriteLine();

        Write($"Наблюдение начато: {DateTime.Now:dd.MM.yyyy HH:mm:ss}");
        Write($"Программа: NetWatch {Version}");
        Write($"Подключение: {network.InterfaceName} ({network.LocalAddress})");
        Write($"Роутер: {network.Gateway}, MAC {network.RouterMac}");
        Write($"Тип: {(network.IsWireless ? "Wi-Fi" : "кабель")}, скорость {network.LinkSpeedBps / 1_000_000} Мбит/с");
        Write($"Проверяю: роутер и {string.Join(", ", ExternalHosts)}");
        Write("Остановить: Ctrl+C. Файл можно закрыть, он дописывается.");
        Write("");

        Console.WriteLine();
        Console.WriteLine("  Слежу за сетью. Остановить — Ctrl+C.");
        Console.WriteLine("  Файл: " + _logPath);
        Console.WriteLine();

        var stop = new CancellationTokenSource();

        Console.CancelKeyPress += (_, e) =>
        {
            e.Cancel = true;
            stop.Cancel();
        };

        await WatchAsync(network, gateway, externals, stop.Token);

        Write("");
        Write($"Наблюдение остановлено: {DateTime.Now:dd.MM.yyyy HH:mm:ss}");
        Flush();

        Console.WriteLine();
        Console.WriteLine("  Готово. Файл: " + _logPath);

        return 0;
    }

    // ------------------------------------------------------------------ наблюдение

    private static async Task WatchAsync(
        NetworkSnapshot startNetwork,
        IcmpProbe gateway,
        IcmpProbe[] externals,
        CancellationToken ct)
    {
        var network = startNetwork;

        var checks = 0;
        var failures = 0;
        var lostRouter = 0;
        var lostInternet = 0;
        var consecutiveFailures = 0;
        var wasDown = false;

        var routerTimes = new List<double>();
        var internetTimes = new List<double>();

        var nextHeartbeat = DateTime.UtcNow;
        var externalIndex = 0;
        var currentNetwork = network;
        var lastState = DateTime.UtcNow;

        while (!ct.IsCancellationRequested)
        {
            checks++;

            var routerMs = gateway.MeasureOnce();
            var external = externals[externalIndex % externals.Length];
            var internetMs = external.MeasureOnce();

            externalIndex++;

            var routerOk = routerMs is not null;
            var internetOk = internetMs is not null;

            if (routerOk) routerTimes.Add(routerMs!.Value);
            if (internetOk) internetTimes.Add(internetMs!.Value);

            if (!internetOk) lostInternet++;
            if (!routerOk) lostRouter++;
            if (!routerOk || !internetOk) failures++;

            // Сеть перестроилась: сменился адрес или роутер. Часто это следствие
            // перезагрузки роутера или нового подключения после обрыва.
            if (--_networkRecheckIn <= 0)
            {
                _networkRecheckIn = 15;

                var fresh = NetworkReader.Read();

                if (fresh is not null && !SameAs(currentNetwork, fresh))
                {
                    Write("");
                    Write($"[{Now()}] !!! СЕТЬ ПЕРЕСТРОИЛАСЬ");
                    Write($"         было:  {currentNetwork.InterfaceName}, адрес {currentNetwork.LocalAddress}, роутер {currentNetwork.Gateway} ({currentNetwork.RouterMac})");
                    Write($"         стало: {fresh.InterfaceName}, адрес {fresh.LocalAddress}, роутер {fresh.Gateway} ({fresh.RouterMac})");

                    if (currentNetwork.RouterMac != fresh.RouterMac && currentNetwork.RouterMac != "неизвестен")
                        Write("         MAC роутера сменился — в сети другое устройство с теми же адресами");

                    if (currentNetwork.Gateway != fresh.Gateway)
                        Write("         адрес роутера сменился — вероятно, роутер перезагрузился");

                    Write("");

                    currentNetwork = fresh;
                }
            }

            if (routerOk && internetOk)
            {
                consecutiveFailures = 0;

                if (wasDown)
                {
                    Write($"[{Now()}] СВЯЗЬ ВЕРНУЛАСЬ");
                    Write($"         роутер {Describe(routerMs)}, {external.Title} {Describe(internetMs)}");
                    Write("");
                    wasDown = false;
                }

                // Полное состояние — редко, чтобы файл не разрастался.
                if (DateTime.UtcNow >= nextHeartbeat)
                {
                    nextHeartbeat = DateTime.UtcNow + HeartbeatInterval;

                    var ctx = ReadContext(routerMs!.Value, internetMs!.Value, routerTimes, internetTimes);

                    // Узел называем по имени: внешних узлов два, и цифры у них разные.
                    // Без имени показалось бы, что связь скачет между 29 и 43 мс.
                    Write($"[{Now()}] в порядке: роутер {Describe(routerMs)}" +
                          $" · {external.Title} {Describe(internetMs)}" +
                          $" · соединений {ctx.Connections} · процессов {ctx.Processes}" +
                          $" · CS2 {(ctx.Cs2Running ? "идёт" : "нет")}" +
                          $" · память {ctx.MemoryUsedPercent}% · ЦП {ctx.CpuLoad}%");
                }
            }
            else
            {
                consecutiveFailures++;

                // Пишем не каждую неудачу, а с нарастанием: одиночная потеря пакета —
                // это норма, а вот три подряд — уже событие.
                if (!wasDown || consecutiveFailures % 5 == 0)
                {
                    var what = !routerOk && !internetOk
                        ? "РОУТЕР И ИНТЕРНЕТ НЕ ОТВЕЧАЮТ"
                        : !internetOk
                            ? "ИНТЕРНЕТ НЕ ОТВЕЧАЕТ, РОУТЕР ОТВЕЧАЕТ"
                            : "роутер не отвечает, интернет работает";

                    var ctx = ReadContext(routerMs, internetMs, routerTimes, internetTimes);

                    Write("");
                    Write($"[{Now()}] {(wasDown ? "ВСЁ ЕЩЁ НЕТ СВЯЗИ" : "!!! ОБРЫВ")}: {what}");
                    Write($"         подряд неудач: {consecutiveFailures}");

                    // Ближайшее окружение обрыва — самое важное для разбора.
                    Write($"         ПЕРЕД ОБРЫВОМ: роутер {LastOr(routerTimes)} мс, интернет {LastOr(internetTimes)} мс");
                    Write($"         соединений: {ctx.Connections} · процессов: {ctx.Processes} · потоков: {ctx.Threads}");
                    Write($"         ЦП {ctx.CpuLoad}%, память {ctx.MemoryUsedPercent}% ({ctx.MemoryUsedMb} МБ из {ctx.MemoryTotalMb} МБ)");
                    Write($"         CS2: {(ctx.Cs2Running ? "ИДЁТ" : "нет")}" +
                          (ctx.Cs2Running ? $", памяти {ctx.Cs2MemoryMb} МБ, соединений {ctx.Cs2Connections}" : ""));
                    Write($"         больше всего соединений: {ctx.TopTalkers}");
                    Write($"         внешний адрес: {ctx.PublicIp}");
                    Write($"         ошибок сетевой карты: приём {ctx.ReceiveErrors}, отправка {ctx.SendErrors}");
                    Write("");

                    wasDown = true;
                }
            }

            WriteState(checks, failures, lostRouter, lostInternet, routerTimes, internetTimes, currentNetwork);

            try
            {
                await Task.Delay(CheckInterval, ct);
            }
            catch (OperationCanceledException)
            {
                break;
            }
        }

        Write("");
        Write("=== ИТОГ НАБЛЮДЕНИЯ ===");
        Write($"Проверок: {checks}");
        Write($"Обрывов: {failures} (роутер {lostRouter}, интернет {lostInternet})");
        Write($"Доля обрывов: {Percent(failures, checks)}%");
        Write($"Задержка до роутера: {Describe(routerTimes)}");
        Write($"Задержка до интернета: {Describe(internetTimes)}");
    }

    private static int _networkRecheckIn = 15;

    private static bool SameAs(NetworkSnapshot a, NetworkSnapshot b) =>
        a.InterfaceName == b.InterfaceName &&
        a.LocalAddress == b.LocalAddress &&
        a.Gateway == b.Gateway &&
        a.RouterMac == b.RouterMac;

    // ------------------------------------------------------------------ окружение

    private sealed record Context(
        int Connections,
        int Processes,
        int Threads,
        int CpuLoad,
        int MemoryUsedPercent,
        long MemoryUsedMb,
        long MemoryTotalMb,
        bool Cs2Running,
        long Cs2MemoryMb,
        int Cs2Connections,
        string TopTalkers,
        string PublicIp,
        long ReceiveErrors,
        long SendErrors);

    /// <summary>
    /// Что происходило в системе в этот момент. Это и отличает полезную запись
    /// от бесполезной: по одним потерям пакетов виновника не найти.
    /// </summary>
    private static Context ReadContext(
        double? routerMs, double? internetMs,
        List<double> routerTimes, List<double> internetTimes)
    {
        var processes = 0;
        var threads = 0;
        var cs2Running = false;
        var cs2Memory = 0L;
        var cs2Connections = 0;
        var totalMemory = 0L;
        var usedMemory = 0L;

        try
        {
            var all = Process.GetProcesses();
            processes = all.Length;

            foreach (var p in all)
            {
                try
                {
                    threads += p.Threads.Count;
                    usedMemory += p.WorkingSet64;

                    if (string.Equals(p.ProcessName, "cs2", StringComparison.OrdinalIgnoreCase))
                    {
                        cs2Running = true;
                        cs2Memory = p.WorkingSet64 / 1024 / 1024;
                    }
                }
                catch { }
                finally { p.Dispose(); }
            }
        }
        catch { }

        var connections = 0;
        var topTalkers = "нет данных";

        try
        {
            var established = System.Net.NetworkInformation.IPGlobalProperties
                .GetIPGlobalProperties()
                .GetActiveTcpConnections();

            connections = established.Length;

            // Кто держит больше всего соединений — главное для поиска виновника
            // переполнения таблицы роутера.
            topTalkers = DescribeTalkers();
        }
        catch { }

        try
        {
            totalMemory = GC.GetGCMemoryInfo().TotalAvailableMemoryBytes / 1024 / 1024;
        }
        catch { }

        var memoryPercent = totalMemory > 0 ? (int)(usedMemory * 100 / (totalMemory * 1024 * 1024)) : 0;

        return new Context(
            connections,
            processes,
            threads,
            0,
            memoryPercent,
            usedMemory / 1024 / 1024,
            totalMemory,
            cs2Running,
            cs2Memory,
            cs2Connections,
            topTalkers,
            ReadPublicIp(),
            ReadAdapterErrors().Item1,
            ReadAdapterErrors().Item2);
    }

    private static string DescribeTalkers()
    {
        try
        {
            var psi = new ProcessStartInfo("netstat.exe", "-ano -p tcp")
            {
                RedirectStandardOutput = true,
                UseShellExecute = false,
                CreateNoWindow = true
            };

            using var process = Process.Start(psi);

            if (process is null) return "нет данных";

            var output = process.StandardOutput.ReadToEnd();
            process.WaitForExit(4000);

            var counts = new Dictionary<int, int>();

            foreach (var raw in output.Split('\n'))
            {
                var line = raw.Trim();

                if (!line.StartsWith("TCP", StringComparison.Ordinal)) continue;
                if (!line.Contains("ESTABLISHED", StringComparison.Ordinal)) continue;

                var parts = line.Split(' ', StringSplitOptions.RemoveEmptyEntries);
                if (parts.Length < 5) continue;

                if (!int.TryParse(parts[^1], out var pid)) continue;

                // Соединения внутри сети и на себя не считаем: они не грузят роутер.
                if (parts[2].StartsWith("127.", StringComparison.Ordinal)) continue;
                if (parts[2].StartsWith("192.168.", StringComparison.Ordinal)) continue;
                if (parts[2].StartsWith("0.0.0.0", StringComparison.Ordinal)) continue;

                counts.TryGetValue(pid, out var n);
                counts[pid] = n + 1;
            }

            if (counts.Count == 0) return "нет внешних соединений";

            var top = counts
                .OrderByDescending(kv => kv.Value)
                .Take(5)
                .Select(kv =>
                {
                    try
                    {
                        var p = Process.GetProcessById(kv.Key);
                        var name = p.ProcessName;
                        p.Dispose();
                        return $"{name} ({kv.Value})";
                    }
                    catch
                    {
                        return $"процесс {kv.Key} ({kv.Value})";
                    }
                });

            return string.Join(", ", top);
        }
        catch
        {
            return "нет данных";
        }
    }

    private static string _lastPublicIp = "неизвестен";
    private static DateTime _publicIpCheckedAt = DateTime.MinValue;

    /// <summary>
    /// Внешний адрес. Если он сменился — значит соединение с провайдером рвалось:
    /// при разрыве провайдер обычно выдаёт новый адрес. Это прямое доказательство
    /// обрыва на линии, которое не видно с компьютера никак иначе.
    /// </summary>
    private static string ReadPublicIp()
    {
        // Спрашиваем редко: это внешний запрос, и часто его делать незачем.
        if (DateTime.UtcNow - _publicIpCheckedAt < TimeSpan.FromMinutes(5))
            return _lastPublicIp;

        _publicIpCheckedAt = DateTime.UtcNow;

        try
        {
            using var client = new System.Net.Http.HttpClient { Timeout = TimeSpan.FromSeconds(4) };
            var ip = client.GetStringAsync("https://api.ipify.org").GetAwaiter().GetResult().Trim();

            if (ip != _lastPublicIp && _lastPublicIp != "неизвестен")
                Write($"[{Now()}] !!! ВНЕШНИЙ АДРЕС СМЕНИЛСЯ: {_lastPublicIp} -> {ip} (соединение с провайдером рвалось)");

            _lastPublicIp = ip;
        }
        catch
        {
            // нет интернета — оставляем прежнее значение
        }

        return _lastPublicIp;
    }

    private static (long, long) ReadAdapterErrors()
    {
        try
        {
            var nic = System.Net.NetworkInformation.NetworkInterface.GetAllNetworkInterfaces()
                .FirstOrDefault(n => n.OperationalStatus == System.Net.NetworkInformation.OperationalStatus.Up
                                     && n.NetworkInterfaceType != System.Net.NetworkInformation.NetworkInterfaceType.Loopback
                                     && n.GetIPProperties().GatewayAddresses.Count > 0);

            if (nic is null) return (0, 0);

            var stats = nic.GetIPStatistics();

            return (stats.IncomingPacketsWithErrors, stats.OutgoingPacketsWithErrors);
        }
        catch
        {
            return (0, 0);
        }
    }

    // ------------------------------------------------------------------ запись

    private static string Now() => DateTime.Now.ToString("HH:mm:ss");

    private static string LastOr(List<double> values) =>
        values.Count == 0 ? "нет данных" : values[^1].ToString("0.#", CultureInfo.InvariantCulture);

    private static string Percent(int part, int total) =>
        total == 0 ? "0" : (100.0 * part / total).ToString("0.00", CultureInfo.InvariantCulture);

    /// <summary>Описание одиночного замера: «нет ответа» или значение.</summary>
    private static string Describe(double? value) =>
        value is null ? "нет ответа" : $"{value:0.#} мс";

    /// <summary>Описание распределения: медиана и разброс важнее среднего.</summary>
    private static string Describe(List<double> values)
    {
        if (values.Count == 0) return "нет данных";

        var sorted = values.OrderBy(v => v).ToList();
        var median = sorted[sorted.Count / 2];
        var avg = values.Average();
        var sumSq = values.Sum(v => Math.Pow(v - avg, 2));
        var stdDev = Math.Sqrt(sumSq / values.Count);

        return $"медиана {median:0.#} мс, средняя {avg:0.#} мс, " +
               $"от {sorted[0]:0.#} до {sorted[^1]:0.#}, разброс {stdDev:0.##} мс";
    }

    /// <summary>Строка состояния: её читает программа, чтобы понять, что наблюдение живо.</summary>
    private static void WriteState(
        int checks, int failures, int lostRouter, int lostInternet,
        List<double> routerTimes, List<double> internetTimes,
        NetworkSnapshot network)
    {
        try
        {
            var text =
                $"NetWatch {Version}{Environment.NewLine}" +
                $"обновлено: {DateTime.Now:dd.MM.yyyy HH:mm:ss}{Environment.NewLine}" +
                $"работает: {(DateTime.Now - StartedAt).TotalMinutes:0} мин{Environment.NewLine}" +
                $"проверок: {checks}, обрывов: {failures}{Environment.NewLine}" +
                $"роутер: не отвечал {lostRouter} раз{Environment.NewLine}" +
                $"интернет: не отвечал {lostInternet} раз{Environment.NewLine}" +
                $"роутер сейчас: {LastOr(routerTimes)} мс{Environment.NewLine}" +
                $"интернет сейчас: {LastOr(internetTimes)} мс{Environment.NewLine}" +
                $"подключение: {network.InterfaceName}, адрес {network.LocalAddress}{Environment.NewLine}" +
                $"роутер: {network.Gateway}, MAC {network.RouterMac}{Environment.NewLine}" +
                $"файл: {_logPath}{Environment.NewLine}";

            File.WriteAllText(_statePath, text, new UTF8Encoding(true));
        }
        catch
        {
            // не критично
        }
    }

    private static readonly DateTime StartedAt = DateTime.Now;

    private static void Write(string line)
    {
        lock (Pending)
        {
            Pending.AppendLine(line);
        }

        Flush();
    }

    private static void Flush()
    {
        string text;

        lock (Pending)
        {
            if (Pending.Length == 0) return;

            text = Pending.ToString();
            Pending.Clear();
        }

        try
        {
            File.AppendAllText(_logPath, text, new UTF8Encoding(true));
        }
        catch
        {
            // Если запись не удалась, сообщать нечем: программа про сеть, не про диск.
        }
    }

    private static void PrintHeader()
    {
        Console.WriteLine();
        Console.WriteLine("  NetWatch " + Version + " — наблюдение за сетью и роутером");
        Console.WriteLine("  ──────────────────────────────────────────────────────────────");
        Console.WriteLine("  Следит за связью и записывает всё нужное для разбора обрывов:");
        Console.WriteLine("  когда пропало, что именно, и что было в системе в этот момент.");
        Console.WriteLine();
        Console.WriteLine("  Программа ничего не меняет: только измеряет и пишет файл.");
        Console.WriteLine();
    }
}
