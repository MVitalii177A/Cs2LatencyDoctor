using System.Text.Json;
using System.Text.Json.Serialization;

namespace Cs2LatencyDoctor.Core.Fixes;

/// <summary>Состояние одного изменённого значения до правки — чтобы можно было вернуть как было.</summary>
public sealed class JournalEntry
{
    public required string FixId { get; init; }
    public required string Title { get; init; }

    /// <summary>Тип хранилища: registryKeyword, registryValue, powercfg.</summary>
    public required string Kind { get; init; }

    /// <summary>Где именно: описание адаптера, путь ветки реестра или GUID настройки питания.</summary>
    public required string Location { get; init; }

    /// <summary>Имя параметра (keyword драйвера или имя значения реестра).</summary>
    public required string Name { get; init; }

    /// <summary>Значение до правки. Строкой, потому что типы разные.</summary>
    public required string OldValue { get; init; }

    /// <summary>Значение, которое записали.</summary>
    public required string NewValue { get; init; }

    public DateTimeOffset AppliedAt { get; init; } = DateTimeOffset.Now;
}

/// <summary>
/// Журнал изменений: что и когда мы поменяли, чтобы вернуть всё назад.
/// Пишется на диск после каждого шага — если программа упадёт, откат всё равно сработает.
/// </summary>
public sealed class UndoJournal
{
    private static readonly JsonSerializerOptions JsonOptions = new()
    {
        WriteIndented = true,
        DefaultIgnoreCondition = JsonIgnoreCondition.WhenWritingNull,
        Encoder = System.Text.Encodings.Web.JavaScriptEncoder.UnsafeRelaxedJsonEscaping
    };

    private readonly List<JournalEntry> _entries = new();
    private bool _loaded;
    private bool _fileReady;

    /// <summary>Путь к файлу журнала. null, если ни одна папка не доступна для записи.</summary>
    public string? FilePath { get; private set; }

    /// <summary>Удалось ли подготовить файл журнала (папка существует и доступна для записи).</summary>
    public bool IsFileReady
    {
        get
        {
            EnsureLoaded();
            return _fileReady;
        }
    }

    public UndoJournal(string? filePath = null)
    {
        FilePath = filePath;
    }

    /// <summary>
    /// Подобрать место для журнала. Пробуем по очереди, потому что папка пользователя
    /// может быть недоступна (ограниченные права, политики, защищённый профиль).
    /// </summary>
    public static string? ResolveJournalPath()
    {
        var fileName = "undo-journal.json";

        var candidates = new List<string>();

        try
        {
            candidates.Add(Path.Combine(
                Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData),
                "Cs2LatencyDoctor"));
        }
        catch { /* нет доступа к спецпапке */ }

        try
        {
            var appData = Environment.GetEnvironmentVariable("APPDATA");
            if (!string.IsNullOrWhiteSpace(appData))
                candidates.Add(Path.Combine(appData, "Cs2LatencyDoctor"));
        }
        catch { /* нет переменной */ }

        try { candidates.Add(Path.Combine(Environment.CurrentDirectory, "cs2latency-data")); } catch { }
        try { candidates.Add(Path.Combine(Path.GetTempPath(), "Cs2LatencyDoctor")); } catch { }

        foreach (var directory in candidates)
        {
            if (TryPrepareDirectory(directory))
                return Path.Combine(directory, fileName);
        }

        return null;
    }

    /// <summary>Создать папку и убедиться, что в неё реально можно писать.</summary>
    private static bool TryPrepareDirectory(string directory)
    {
        try
        {
            Directory.CreateDirectory(directory);

            // Проверка записью: наличие папки ещё не значит, что писать разрешено.
            var probe = Path.Combine(directory, ".write-probe");
            File.WriteAllText(probe, "ok");
            File.Delete(probe);

            return true;
        }
        catch
        {
            return false;
        }
    }

    /// <summary>Текст пути для интерфейса: null означает, что журнал недоступен.</summary>
    public string JournalPathText() =>
        IsFileReady && FilePath is not null
            ? FilePath
            : "недоступен (записать журнал не удалось!)";

    private void EnsureLoaded()
    {        if (_loaded) return;
        _loaded = true;

        if (string.IsNullOrEmpty(FilePath))
            FilePath = ResolveJournalPath();

        if (string.IsNullOrEmpty(FilePath))
            return;

        _fileReady = true;
        Load();
    }

    public IReadOnlyList<JournalEntry> Entries
    {
        get
        {
            EnsureLoaded();
            return _entries;
        }
    }

    public bool HasChanges => Entries.Count > 0;

    public void Add(JournalEntry entry)
    {
        EnsureLoaded();
        _entries.Add(entry);
        Save();
    }

    public void Clear()
    {
        EnsureLoaded();
        _entries.Clear();
        Save();
    }

    private void Load()
    {
        try
        {
            if (FilePath is null || !File.Exists(FilePath)) return;
            var json = File.ReadAllText(FilePath);
            var loaded = JsonSerializer.Deserialize<List<JournalEntry>>(json, JsonOptions);
            if (loaded is not null) _entries.AddRange(loaded);
        }
        catch
        {
            // Битый журнал не должен ронять программу: просто начинаем с чистого.
            _entries.Clear();
        }
    }

    private void Save()
    {
        try
        {
            if (FilePath is null)
            {
                _fileReady = false;
                return;
            }

            File.WriteAllText(FilePath, JsonSerializer.Serialize(_entries, JsonOptions));
        }
        catch
        {
            // не смогли записать — не повод падать, но откат может быть неполным
            _fileReady = false;
        }
    }
}
