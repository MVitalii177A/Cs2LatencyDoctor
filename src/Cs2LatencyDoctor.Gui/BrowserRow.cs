using System.ComponentModel;
using System.Runtime.CompilerServices;

namespace Cs2LatencyDoctor.Gui;

/// <summary>Строка списка браузеров для показа в окне выбора.</summary>
public sealed class BrowserRow : INotifyPropertyChanged
{
    public required string Title { get; init; }
    public required string Path { get; init; }
    public required InstalledBrowser Browser { get; init; }

    private string _result = string.Empty;

    /// <summary>Что произошло при попытке открыть: показывается прямо в строке.</summary>
    public string Result
    {
        get => _result;
        set { _result = value; OnPropertyChanged(); }
    }

    public event PropertyChangedEventHandler? PropertyChanged;

    private void OnPropertyChanged([CallerMemberName] string? name = null) =>
        PropertyChanged?.Invoke(this, new PropertyChangedEventArgs(name));
}
