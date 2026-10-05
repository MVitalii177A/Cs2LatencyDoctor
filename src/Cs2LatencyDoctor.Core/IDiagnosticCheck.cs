namespace Cs2LatencyDoctor.Core;

/// <summary>Проверка одной темы (сеть, сетевая карта, питание, CS2 и т.д.).</summary>
public interface IDiagnosticCheck
{
    /// <summary>Стабильный id проверки.</summary>
    string Id { get; }

    /// <summary>Название для интерфейса.</summary>
    string Title { get; }

    /// <summary>Нужны ли права администратора, чтобы прочитать данные этой проверки.</summary>
    bool RequiresAdmin { get; }

    Task<IReadOnlyList<CheckResult>> RunAsync(DiagnosticContext context, CancellationToken ct);
}

/// <summary>Общее состояние, которое проверки переиспользуют, чтобы не делать одну работу дважды.</summary>
public sealed class DiagnosticContext
{
    public required bool IsAdministrator { get; init; }

    /// <summary>Прогресс для GUI: текст того, что происходит сейчас.</summary>
    public Action<string>? OnProgress { get; init; }

    /// <summary>Широковещательный адрес сети — шлюз по умолчанию (обычно роутер).</summary>
    public string? GatewayAddress { get; set; }

    /// <summary>Основной физический адаптер, через который идёт интернет.</summary>
    public string? PrimaryAdapterName { get; set; }

    /// <summary>Длительность одного замера задержки, сек.</summary>
    public int ProbeSeconds { get; init; } = 20;

    /// <summary>Путь к установленной CS2, если нашли.</summary>
    public string? Cs2Path { get; set; }

    /// <summary>SteamID пользователя (папка userdata), если нашли однозначно.</summary>
    public string? SteamUserId { get; set; }

    private Windows.Cs2Installation? _installation;
    private bool _installationResolved;

    /// <summary>
    /// Найденная установка CS2. Ищем один раз на всю диагностику: несколько проверок
    /// работают с одними и теми же файлами игры, а повторный поиск по дискам — лишняя работа.
    /// </summary>
    public Windows.Cs2Installation? GetCs2Installation()
    {
        if (_installationResolved) return _installation;

        _installationResolved = true;
        _installation = Windows.Cs2Locator.Find();

        if (_installation is not null)
        {
            Cs2Path = _installation.GameFolder;
            SteamUserId = _installation.SteamUserId;
        }

        return _installation;
    }

    public void Progress(string message) => OnProgress?.Invoke(message);
}
