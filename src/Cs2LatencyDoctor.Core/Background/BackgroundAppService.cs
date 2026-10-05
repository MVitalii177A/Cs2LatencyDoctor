using System.Diagnostics;
using System.Text.Json;

namespace Cs2LatencyDoctor.Core.Background;

/// <summary>Что известно о программе, которую можно поставить на паузу.</summary>
public sealed record BackgroundAppInfo(
    string ProcessName,
    string Title,
    string Reason,
    bool IsRunning,
    int ProcessCount,
    long MemoryBytes,
    int OpenConnections)
{
    public double MemoryMb => MemoryBytes / 1024.0 / 1024.0;
}

/// <summary>Запись о том, что мы остановили — чтобы вернуть обратно.</summary>
public sealed class PausedApp
{
    public required string ProcessName { get; init; }
    public required string Title { get; init; }
    public string? ExecutablePath { get; init; }

    /// <summary>Был ли процесс запущен в момент паузы. Если нет — при возврате его не запускаем.</summary>
    public bool WasRunning { get; init; }

    public DateTimeOffset PausedAt { get; init; } = DateTimeOffset.Now;
}

/// <summary>Состояние паузы, сохраняемое на диск.</summary>
public sealed class PauseState
{
    public List<PausedApp> Apps { get; init; } = new();
    public DateTimeOffset CreatedAt { get; init; } = DateTimeOffset.Now;
    public bool IsEmpty => Apps.Count == 0;
}

/// <summary>
/// Программы, которые можно ставить на паузу на время игры.
///
/// Правило отбора: они потребляют сеть, диск или постоянно опрашивают систему,
/// и при этом НЕ нужны во время матча. Всё рабочее и системное сюда не попадает.
/// </summary>
public static class BackgroundAppCatalog
{
    /// <summary>Кандидаты на паузу: имя процесса -> описание и причина.</summary>
    public static readonly IReadOnlyList<(string Process, string Title, string Reason)> Candidates = new[]
    {
        ("uTorrentClients", "Торрент-клиент (uTorrent)",
            "держит десятки постоянных соединений: трекеры и DHT стучатся в фоне непрерывно"),
        ("utorrent", "Торрент-клиент (uTorrent)",
            "держит постоянные соединения с трекерами и участниками раздачи"),
        ("bittorrent", "BitTorrent",
            "постоянный сетевой обмен в фоне"),
        ("qbittorrent", "qBittorrent",
            "постоянный сетевой обмен в фоне"),
        ("Dropbox", "Dropbox",
            "семь процессов и агрессивное слежение за файлами: возможны всплески диска"),
        ("OneDrive", "OneDrive",
            "синхронизация создаёт нагрузку на диск и сеть"),
        ("GoogleDriveFS", "Google Drive",
            "синхронизация создаёт нагрузку на диск и сеть"),
        ("YandexDisk", "Яндекс.Диск",
            "синхронизация создаёт нагрузку на диск и сеть"),
        ("Telegram", "Telegram",
            "держите открытым только если ждёте сообщение: фоновая синхронизация и анимации"),
        ("Discord", "Discord",
            "оверлей и голосовой движок нагружают систему; если играете без войса — пауза поможет"),
        ("TrafficMonitor", "TrafficMonitor",
            "оверлей-монитор трафика в углу экрана"),
        ("LEDKeeper2", "MSI LED Keeper",
            "управление подсветкой, к игре отношения не имеет"),
        ("Mystic_Light_Service", "MSI Mystic Light",
            "управление подсветкой"),
        ("LightKeeperService", "MSI LightKeeper",
            "управление подсветкой"),
        ("MSI_Central_Service", "MSI Center",
            "фоновая служба производителя: мониторинг и подсветка"),
        ("MSI_Case_Service", "MSI Case Service",
            "управление корпусом и подсветкой"),
        ("MSI_LAN_Manager_Tool", "MSI LAN Manager",
            "вмешивается в сетевые настройки — стоит проверить, помогает ли он вообще"),
        ("ArmouryCrate", "ASUS Armoury Crate",
            "фоновая служба производителя"),
        ("iCUE", "Corsair iCUE",
            "управление подсветкой и периферией"),
        ("RazerSynapse", "Razer Synapse",
            "фоновая служба периферии"),
        ("Overwolf", "Overwolf",
            "платформа оверлеев: заметная нагрузка на систему"),
        ("SteamWebHelper", "Steam Web Helper",
            "браузерный движок Steam; закрывается вместе с окном Steam")
    };

    /// <summary>
    /// Имена процессов, которые НИКОГДА не останавливаем, даже если пользователь попросит.
    /// Это защита от случайного нажатия: без неё «пауза фона» может погасить систему.
    /// </summary>
    public static readonly IReadOnlySet<string> NeverStop = new HashSet<string>(StringComparer.OrdinalIgnoreCase)
    {
        "system", "idle", "registry", "memory compression", "smss", "csrss", "wininit", "winlogon",
        "services", "lsass", "svchost", "fontdrvhost", "dwm", "explorer", "sihost", "taskhostw",
        "ctfmon", "runtimebroker", "searchindexer", "searchapp", "securityhealthservice",
        "securityhealthsystray", "msmpeng", "nissrv", "audiodg", "spoolsv", "wudfhost",
        "cs2", "steam", "steamservice", "cs2.exe",
        // рабочие инструменты: их потеря может стоить человеку данных
        "postgres", "sqlwriter", "devenv", "code", "kompas", "polynomappserver",
        "docker", "com.docker.backend", "wslservice", "vmmem", "vmware-vmx", "virtualboxvm",
        "keepass", "keepassxc", "nvcontainer", "nvdisplay.container", "node", "python"
    };
}

/// <summary>
/// Пауза фоновых программ на время игры и возврат их обратно.
/// Ничего не удаляет и не отключает навсегда: только останавливает процессы,
/// запоминая, что именно было запущено.
/// </summary>
public sealed class BackgroundAppService
{
    private static readonly JsonSerializerOptions JsonOptions = new()
    {
        WriteIndented = true,
        Encoder = System.Text.Encodings.Web.JavaScriptEncoder.UnsafeRelaxedJsonEscaping
    };

    private readonly string? _statePath;

    public BackgroundAppService(string? statePath = null) => _statePath = statePath;

    public string StatePathText => ResolveStatePath() ?? "недоступен";

    private string? ResolveStatePath()
    {
        if (!string.IsNullOrEmpty(_statePath)) return _statePath;

        var journalPath = Fixes.UndoJournal.ResolveJournalPath();
        var directory = journalPath is null ? null : Path.GetDirectoryName(journalPath);
        return directory is null ? null : Path.Combine(directory, "paused-apps.json");
    }

    /// <summary>Список кандидатов с текущим состоянием: что реально работает прямо сейчас.</summary>
    public IReadOnlyList<BackgroundAppInfo> Survey()
    {
        var result = new List<BackgroundAppInfo>();
        var seen = new HashSet<string>(StringComparer.OrdinalIgnoreCase);

        foreach (var (processName, title, reason) in BackgroundAppCatalog.Candidates)
        {
            if (!seen.Add(processName)) continue;

            var processes = FindProcesses(processName);
            if (processes.Count == 0) continue;

            long memory = 0;
            foreach (var process in processes)
            {
                try { memory += process.WorkingSet64; } catch { /* процесс мог завершиться */ }
            }

            result.Add(new BackgroundAppInfo(
                processName, title, reason,
                IsRunning: true,
                ProcessCount: processes.Count,
                MemoryBytes: memory,
                OpenConnections: CountConnections(processes)));
        }

        return result.OrderByDescending(a => a.OpenConnections)
                     .ThenByDescending(a => a.MemoryBytes)
                     .ToList();
    }

    /// <summary>
    /// Остановить выбранные программы. Возвращает состояние, которое нужно сохранить,
    /// чтобы потом вернуть всё назад.
    /// </summary>
    public PauseState Pause(IEnumerable<string> processNames, DiagnosticContext context)
    {
        var state = new PauseState();

        foreach (var name in processNames)
        {
            if (BackgroundAppCatalog.NeverStop.Contains(name))
            {
                context.Progress($"Пропускаю {name}: в списке защищённых");
                continue;
            }

            var processes = FindProcesses(name);
            if (processes.Count == 0) continue;

            string? path = null;
            try { path = processes[0].MainModule?.FileName; } catch { /* нет доступа к пути */ }

            var stopped = 0;
            foreach (var process in processes)
            {
                try
                {
                    process.Kill(entireProcessTree: true);
                    stopped++;
                }
                catch
                {
                    // не удалось — например, служба перезапускает процесс
                }
            }

            state.Apps.Add(new PausedApp
            {
                ProcessName = name,
                Title = BackgroundAppCatalog.Candidates.First(c => c.Process == name).Title,
                ExecutablePath = path,
                WasRunning = true
            });

            context.Progress($"Остановлено: {name} ({stopped} процессов)");
        }

        SaveState(state);
        return state;
    }

    /// <summary>Вернуть остановленные программы.</summary>
    public (int Restored, int Failed) Resume(DiagnosticContext context)
    {
        var state = LoadState();
        if (state.IsEmpty) return (0, 0);

        var restored = 0;
        var failed = 0;

        foreach (var app in state.Apps)
        {
            if (!app.WasRunning) continue;

            if (FindProcesses(app.ProcessName).Count > 0)
            {
                restored++;
                continue;
            }

            if (string.IsNullOrEmpty(app.ExecutablePath) || !File.Exists(app.ExecutablePath))
            {
                failed++;
                continue;
            }

            try
            {
                Process.Start(new ProcessStartInfo
                {
                    FileName = app.ExecutablePath,
                    UseShellExecute = true
                });
                restored++;
                context.Progress($"Запущено: {app.Title}");
            }
            catch
            {
                failed++;
            }
        }

        // Состояние очищаем: возврат выполнен.
        SaveState(new PauseState());
        return (restored, failed);
    }

    /// <summary>Что сейчас стоит на паузе.</summary>
    public PauseState GetCurrentState() => LoadState();

    private static List<Process> FindProcesses(string name)
    {
        try
        {
            return Process.GetProcessesByName(name).ToList();
        }
        catch
        {
            return new List<Process>();
        }
    }

    /// <summary>Сколько TCP-соединений держат эти процессы. Для торрента это главный показатель.</summary>
    private static int CountConnections(List<Process> processes)
    {
        try
        {
            var ids = processes.Select(p => p.Id).ToHashSet();

            return System.Net.NetworkInformation.IPGlobalProperties.GetIPGlobalProperties()
                .GetActiveTcpConnections()
                .Count(c => ids.Contains(c.LocalEndPoint.Port));
        }
        catch
        {
            return 0;
        }
    }

    private void SaveState(PauseState state)
    {
        var path = ResolveStatePath();
        if (path is null) return;

        try
        {
            File.WriteAllText(path, JsonSerializer.Serialize(state, JsonOptions));
        }
        catch
        {
            // не смогли сохранить — вернуть программы автоматически не получится,
            // но останавливать уже остановленное не будем
        }
    }

    private PauseState LoadState()
    {
        var path = ResolveStatePath();
        if (path is null || !File.Exists(path)) return new PauseState();

        try
        {
            var json = File.ReadAllText(path);
            return JsonSerializer.Deserialize<PauseState>(json, JsonOptions) ?? new PauseState();
        }
        catch
        {
            return new PauseState();
        }
    }
}
