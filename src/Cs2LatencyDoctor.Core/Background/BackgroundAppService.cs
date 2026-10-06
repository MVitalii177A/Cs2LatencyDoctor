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

    /// <summary>
    /// Программа найдена по нагрузке, а не взята из известного списка.
    ///
    /// Такие строки НИКОГДА не отмечаются заранее. Причина не в осторожности,
    /// а в реальной ошибке: первая версия поиска отмечала найденное к остановке,
    /// нашла браузер и среду разработки и закрыла их вместе с открытой работой.
    /// Найденное по нагрузке человек должен выбрать сам, осознанно.
    /// </summary>
    public bool FoundByActivity { get; init; }
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

    /// <summary>
    /// Программы, которые НЕЛЬЗЯ останавливать никогда, даже если они попали
    /// в список кандидатов.
    ///
    /// Этот список появился после реальной ошибки. Обнаружение по нагрузке нашло
    /// браузер и среду разработки — они держали больше всех памяти — и остановило
    /// их вместе с открытыми вкладками и несохранённой работой. Вывод: определять
    /// «ненужную» программу по одной памяти нельзя, нужен явный запрет.
    /// </summary>
    public static readonly IReadOnlySet<string> NeverDiscover = new HashSet<string>(
        StringComparer.OrdinalIgnoreCase)
    {
        // Браузеры: в них открыта работа, вкладки и несохранённые формы.
        "chrome", "firefox", "msedge", "opera", "brave", "vivaldi", "iexplore",
        "browser", "yandex", "chromium", "waterfox", "palemoon", "tor-browser",

        // Среды разработки, редакторы и терминалы.
        "code", "devenv", "rider64", "pycharm64", "idea64", "webstorm64", "clion64",
        "goland64", "phpstorm64", "notepad++", "sublime_text", "atom", "vim", "nvim",
        "emacs", "textpad", "kompas", "polynomappserver", "autocad", "photoshop",
        "illustrator", "figma", "blender", "excel", "winword", "powerpnt", "outlook",
        "winterm", "windowsterminal", "wt", "conhost", "powershell", "pwsh", "cmd",
        "bash", "wsl", "mintty", "putty", "kitty", "alacritty",

        // Инструменты, которые могут выполнять длительную работу прямо сейчас.
        "python", "pythonw", "node", "dotnet", "java", "javaw", "ruby", "perl",
        "php", "gcc", "g++", "clang", "msbuild", "vbcscOmpiler", "vctip", "git",
        "docker", "com.docker.backend", "docker-compose", "wslservice", "vmmem",
        "vmware-vmx", "virtualboxvm", "qemu-system-x86_64", "postgres", "mysql",
        "mongod", "sqlservr", "sqlwriter", "redis-server", "elasticsearch",

        // Пароли, ключи и прочее, что нельзя терять.
        "keepass", "keepassxc", "1password", "bitwarden", "lastpass", "dashlane",
        "gpg-agent", "kleopatra", "veracrypt", "truecrypt",

        // Средства разработки и отладки, а также наш собственный процесс.
        "cs2latencydoctor.gui", "cs2latency", "guicheck",
        "devenv.exe", "nuget", "dnx", "csi",

        // Файловые менеджеры и оболочка: закрывать их бессмысленно и вредно.
        "explorer", "totalcmd", "far", "winrar", "7zfm", "winzip"
    };

    private readonly string? _statePath;

    public BackgroundAppService(string? statePath = null) => _statePath = statePath;

    public string StatePathText => ResolveStatePath() ?? "недоступен";

    /// <summary>
    /// Каталог для файлов программы: тот же, где лежит журнал отката.
    /// Отдельный метод, потому что им пользуются и свой список программ,
    /// и список запомненных.
    /// </summary>
    private static string? ResolveDataDirectory()
    {
        var journalPath = Fixes.UndoJournal.ResolveJournalPath();
        return journalPath is null ? null : Path.GetDirectoryName(journalPath);
    }

    private string? ResolveStatePath()
    {
        if (!string.IsNullOrEmpty(_statePath)) return _statePath;

        var directory = ResolveDataDirectory();
        return directory is null ? null : Path.Combine(directory, "paused-apps.json");
    }

    /// <summary>
    /// Список кандидатов с текущим состоянием: что реально работает прямо сейчас.
    ///
    /// Кандидаты собираются из четырёх источников, а не только из зашитого списка:
    ///   1) базовый список — то, что известно про типовые программы;
    ///   2) список пользователя — файл рядом с журналом, куда можно вписать свои имена;
    ///   3) запомненные программы — те, что человек уже ставил на паузу раньше;
    ///   4) найденные по активности — процессы, которые прямо сейчас держат много
    ///      соединений или памяти, но в списках их нет.
    ///
    /// Зачем это. Зашитый список конечен: он не знает ни про программу, которой
    /// пользуется конкретный человек, ни про новую версию знакомой программы
    /// с другим именем процесса. В итоге самая полезная кнопка программы работала
    /// только для тех, кто пользуется тем же набором программ, что и автор.
    /// </summary>
    public IReadOnlyList<BackgroundAppInfo> Survey()
    {
        var result = new List<BackgroundAppInfo>();
        var seen = new HashSet<string>(StringComparer.OrdinalIgnoreCase);

        foreach (var candidate in BuildCandidates())
        {
            if (!seen.Add(candidate.Process)) continue;

            var processes = FindProcesses(candidate.Process);
            if (processes.Count == 0) continue;

            long memory = 0;
            foreach (var process in processes)
            {
                try { memory += process.WorkingSet64; } catch { /* процесс мог завершиться */ }
            }

            result.Add(new BackgroundAppInfo(
                candidate.Process, candidate.Title, candidate.Reason,
                IsRunning: true,
                ProcessCount: processes.Count,
                MemoryBytes: memory,
                OpenConnections: CountConnections(processes))
            {
                FoundByActivity = candidate.FoundByActivity
            });
        }

        return result.OrderByDescending(a => a.OpenConnections)
                     .ThenByDescending(a => a.MemoryBytes)
                     .ToList();
    }

    /// <summary>Свести вместе все источники кандидатов, не повторяя имена.</summary>
    private IReadOnlyList<(string Process, string Title, string Reason, bool FoundByActivity)> BuildCandidates()
    {
        var list = new List<(string Process, string Title, string Reason, bool FoundByActivity)>();
        var seen = new HashSet<string>(StringComparer.OrdinalIgnoreCase);

        void Add(string process, string title, string reason, bool foundByActivity = false)
        {
            if (string.IsNullOrWhiteSpace(process)) return;
            if (BackgroundAppCatalog.NeverStop.Contains(process)) return;
            if (NeverDiscover.Contains(process)) return;
            if (!seen.Add(process)) return;

            list.Add((process, title, reason, foundByActivity));
        }

        foreach (var (process, title, reason) in BackgroundAppCatalog.Candidates)
            Add(process, title, reason);

        foreach (var process in ReadUserList())
            Add(process, "Своя программа: " + process,
                "добавлено вами в список программ для паузы");

        // Запомненные тоже проходят через запрет: список мог остаться от прежней
        // версии, которая запоминала всё подряд, включая браузеры.
        foreach (var paused in ReadLearnedList())
            Add(paused.ProcessName, paused.Title,
                "вы уже ставили её на паузу раньше");

        // Программы, которых нет ни в одном списке, но которые прямо сейчас
        // заметно работают. Это и есть ответ на «список вместо догадок».
        foreach (var found in DiscoverBusyApps(seen.ToHashSet(StringComparer.OrdinalIgnoreCase)))
            Add(found.Process, found.Title, found.Reason, foundByActivity: true);

        return list;
    }

    /// <summary>
    /// Найти программы, которых нет в списках, но которые прямо сейчас держат
    /// много соединений.
    ///
    /// Осторожность здесь не перестраховка, а вывод из реальной ошибки. Первая версия
    /// этого поиска смотрела только на память, нашла браузер и среду разработки —
    /// они занимают больше всех — и остановила их вместе с открытыми вкладками
    /// и несохранённой работой. Поэтому теперь:
    ///   * в находки попадают только процессы с большим числом СЕТЕВЫХ соединений:
    ///     это признак фоновой передачи данных, а не открытой работы;
    ///   * есть отдельный список запрещённых имён, куда входят браузеры, редакторы,
    ///     терминалы и всё, где может быть несохранённый результат;
    ///   * порог высокий: лучше не найти программу, чем закрыть нужную.
    /// </summary>
    private static IReadOnlyList<(string Process, string Title, string Reason)> DiscoverBusyApps(
        HashSet<string> alreadyKnown)
    {
        var connections = ConnectionCounter.CountByProcess();
        var found = new List<(string Process, string Title, string Reason)>();

        // Только соединения. Память как признак убрана: она одинаково велика
        // и у фоновой синхронизации, и у браузера с открытой работой.
        const int ManyConnections = 12;

        System.Diagnostics.Process[] all;

        try { all = System.Diagnostics.Process.GetProcesses(); }
        catch { return found; }

        foreach (var process in all)
        {
            try
            {
                var name = process.ProcessName;

                if (string.IsNullOrWhiteSpace(name)) continue;
                if (name.Length < 3) continue;
                if (alreadyKnown.Contains(name)) continue;
                if (BackgroundAppCatalog.NeverStop.Contains(name)) continue;
                if (NeverDiscover.Contains(name)) continue;

                var openConnections = connections.TryGetValue(process.Id, out var c) ? c : 0;
                if (openConnections < ManyConnections) continue;

                // Без пути к файлу вернуть программу после паузы не получится,
                // поэтому такие процессы в список не берём.
                string? path;
                try { path = process.MainModule?.FileName; }
                catch { path = null; }

                if (string.IsNullOrEmpty(path)) continue;

                found.Add((name, name + " (найдена по нагрузке)",
                    $"сейчас держит {openConnections} сетевых соединений — это влияет на задержку. " +
                    "В известных списках её нет, поэтому проверьте, что это за программа."));
            }
            catch
            {
                // Процесс завершился или доступ запрещён — пропускаем.
            }
            finally
            {
                try { process.Dispose(); } catch { /* не критично */ }
            }
        }

        return found;
    }

    /// <summary>
    /// Свой список программ: один файл, по одному имени процесса в строке.
    /// Нужен, чтобы не пересобирать программу ради одной своей программы.
    /// </summary>
    public static string? UserListPath
    {
        get
        {
            var directory = ResolveDataDirectory();
            return directory is null ? null : Path.Combine(directory, "background-apps.txt");
        }
    }

    /// <summary>Прочитать свой список. Файла нет — это нормально, не ошибка.</summary>
    public static IReadOnlyList<string> ReadUserList()
    {
        var path = UserListPath;
        if (path is null || !File.Exists(path)) return Array.Empty<string>();

        try
        {
            return File.ReadAllLines(path)
                .Select(line => line.Trim())
                .Where(line => line.Length > 0 && !line.StartsWith('#'))
                .ToList();
        }
        catch
        {
            return Array.Empty<string>();
        }
    }

    /// <summary>
    /// Создать файл своего списка с подсказками внутри.
    /// Возвращает путь или null, если записать некуда.
    /// </summary>
    public static string? EnsureUserListFile()
    {
        var path = UserListPath;
        if (path is null) return null;
        if (File.Exists(path)) return path;

        try
        {
            File.WriteAllLines(path, new[]
            {
                "# Свои программы для паузы: по одному имени процесса в строке.",
                "# Строки, начинающиеся с #, не читаются.",
                "# Имя процесса — это то, что видно в «Диспетчере задач» на вкладке",
                "# «Подробности», в колонке «Имя образа», но БЕЗ расширения .exe.",
                "# Примеры:",
                "#   telegram",
                "#   mygamehelper",
                "#",
                "# Рабочие и системные программы ставить на паузу всё равно нельзя:",
                "# программа их пропустит, даже если они здесь указаны."
            },
            // С меткой UTF-8: без неё Блокнот открывает русский текст кракозябрами,
            // а файл создан именно для того, чтобы его открыли руками.
            new System.Text.UTF8Encoding(encoderShouldEmitUTF8Identifier: true));

            return path;
        }
        catch
        {
            return null;
        }
    }

    /// <summary>Программы, которые человек уже ставил на паузу: пригодятся в следующий раз.</summary>
    private static IReadOnlyList<PausedApp> ReadLearnedList()
    {
        var directory = ResolveDataDirectory();
        if (directory is null) return Array.Empty<PausedApp>();

        // Журнал пауз очищается после возврата, поэтому помним отдельно.
        var path = Path.Combine(directory, "known-background-apps.json");
        if (!File.Exists(path)) return Array.Empty<PausedApp>();

        try
        {
            var json = File.ReadAllText(path);
            return JsonSerializer.Deserialize<List<PausedApp>>(json) ?? new List<PausedApp>();
        }
        catch
        {
            return Array.Empty<PausedApp>();
        }
    }

    /// <summary>Запомнить программу, которую поставили на паузу: в следующий раз покажем её сразу.</summary>
    private static void RememberPaused(IEnumerable<PausedApp> apps)
    {
        var directory = ResolveDataDirectory();
        if (directory is null) return;

        var path = Path.Combine(directory, "known-background-apps.json");

        try
        {
            var known = ReadLearnedList().ToList();

            foreach (var app in apps)
            {
                if (known.Any(k => string.Equals(k.ProcessName, app.ProcessName, StringComparison.OrdinalIgnoreCase)))
                    continue;

                known.Add(app);
            }

            // Держим список коротким: он нужен как подсказка, а не как архив.
            if (known.Count > 60)
                known = known.OrderByDescending(k => k.PausedAt).Take(60).ToList();

            File.WriteAllText(path, JsonSerializer.Serialize(known,
                new JsonSerializerOptions { WriteIndented = true }));
        }
        catch
        {
            // Не смогли запомнить — не беда: список кандидатов просто будет прежним.
        }
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
            if (BackgroundAppCatalog.NeverStop.Contains(name) || NeverDiscover.Contains(name))
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
                // Заголовок берём из собранного списка, а не только из зашитого:
                // программа может быть из своего списка или найдена по нагрузке,
                // и тогда поиск по базовому каталогу просто не найдёт её.
                Title = DescribeTitle(name),
                ExecutablePath = path,
                WasRunning = true
            });

            context.Progress($"Остановлено: {name} ({stopped} процессов)");
        }

        SaveState(state);

        // Запоминаем программы, которые человек ставил на паузу: в следующий раз
        // они попадут в список кандидатов, даже если их нет в зашитом каталоге.
        RememberPaused(state.Apps);

        return state;
    }

    /// <summary>Понятное название программы. Неизвестной считаем само имя процесса.</summary>
    private static string DescribeTitle(string processName)
    {
        var known = BackgroundAppCatalog.Candidates
            .FirstOrDefault(c => string.Equals(c.Process, processName, StringComparison.OrdinalIgnoreCase));

        if (!string.IsNullOrEmpty(known.Title)) return known.Title;

        var learned = ReadLearnedList()
            .FirstOrDefault(a => string.Equals(a.ProcessName, processName, StringComparison.OrdinalIgnoreCase));

        return string.IsNullOrEmpty(learned?.Title) ? processName : learned.Title;
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

    /// <summary>
    /// Сколько TCP-соединений держат эти процессы. Для торрента это главный показатель.
    ///
    /// Считает системная таблица: раньше здесь сравнивались идентификаторы процессов
    /// с номерами портов, то есть числа из разных величин, и результат был случайным.
    /// </summary>
    private static int CountConnections(List<Process> processes) =>
        ConnectionCounter.CountFor(processes);

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
