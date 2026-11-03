using System.ComponentModel;
using System.Runtime.CompilerServices;
using System.Windows.Media;

namespace Altabi.Models;

/// <summary>Display metadata of one application (one group of windows), shared across sessions.</summary>
public sealed class AppInfo : INotifyPropertyChanged
{
    private string _name;
    private ImageSource? _icon;

    public AppInfo(string key, string name)
    {
        Key = key;
        _name = name;
    }

    public string Key { get; }

    public string Name
    {
        get => _name;
        set
        {
            if (_name == value) return;
            _name = value;
            OnPropertyChanged();
            OnPropertyChanged(nameof(Initial));
        }
    }

    public string Initial => Name.Length > 0 ? Name[..1].ToUpperInvariant() : "?";

    public ImageSource? Icon
    {
        get => _icon;
        set
        {
            if (ReferenceEquals(_icon, value)) return;
            _icon = value;
            OnPropertyChanged();
            OnPropertyChanged(nameof(HasIcon));
        }
    }

    public bool HasIcon => _icon is not null;

    public event PropertyChangedEventHandler? PropertyChanged;

    private void OnPropertyChanged([CallerMemberName] string? name = null) =>
        PropertyChanged?.Invoke(this, new PropertyChangedEventArgs(name));
}
