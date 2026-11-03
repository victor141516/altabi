using System.Collections.ObjectModel;
using System.ComponentModel;
using System.Runtime.CompilerServices;
using Altabi.Models;

namespace Altabi.ViewModels;

public abstract class ObservableObject : INotifyPropertyChanged
{
    public event PropertyChangedEventHandler? PropertyChanged;

    protected void Set<T>(ref T field, T value, [CallerMemberName] string? name = null)
    {
        if (EqualityComparer<T>.Default.Equals(field, value)) return;
        field = value;
        PropertyChanged?.Invoke(this, new PropertyChangedEventArgs(name));
    }
}

// Sizes live on each item (fixed for the item's lifetime) so the template binds to its own
// DataContext and the very first measure pass already knows the final size.
public sealed class AppTileViewModel : ObservableObject
{
    private bool _isSelected;

    public AppTileViewModel(AppInfo app, double tileWidth, double iconSize)
    {
        App = app;
        TileWidth = tileWidth;
        IconSize = iconSize;
    }

    public AppInfo App { get; }
    public double TileWidth { get; }
    public double IconSize { get; }

    public bool IsSelected
    {
        get => _isSelected;
        set => Set(ref _isSelected, value);
    }
}

public sealed class WindowThumbViewModel : ObservableObject
{
    private bool _isSelected;

    public WindowThumbViewModel(WindowEntry window, AppInfo app, double thumbWidth, double thumbHeight)
    {
        Window = window;
        App = app;
        ThumbWidth = thumbWidth;
        ThumbHeight = thumbHeight;
    }

    public WindowEntry Window { get; }
    public AppInfo App { get; }
    public string Title => Window.Title;
    public bool IsMinimized => Window.IsMinimized;
    public double ThumbWidth { get; }
    public double ThumbHeight { get; }

    public bool IsSelected
    {
        get => _isSelected;
        set => Set(ref _isSelected, value);
    }
}

public sealed class SwitcherViewModel : ObservableObject
{
    private bool _showWindows;

    public ObservableCollection<AppTileViewModel> Tiles { get; } = new();
    public ObservableCollection<WindowThumbViewModel> Windows { get; } = new();
    public bool ShowWindows { get => _showWindows; set => Set(ref _showWindows, value); }
}
