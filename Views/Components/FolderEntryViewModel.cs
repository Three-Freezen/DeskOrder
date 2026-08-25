using System.ComponentModel;
using System.Runtime.CompilerServices;
using System.Windows.Media;

namespace DesktopZones.Views.Components;

/// <summary>
/// One entry in a zone's mapped-folder listing (file / folder / drive).
/// The icon is filled in asynchronously after the background enumeration —
/// the list shows names first, icons arrive via <see cref="Icon"/> change
/// notifications.
/// </summary>
public sealed class FolderEntryViewModel : INotifyPropertyChanged
{
    public string Name { get; }
    public string FullPath { get; }
    public bool IsFolder { get; }

    private ImageSource? _icon;
    public ImageSource? Icon
    {
        get => _icon;
        set { _icon = value; OnPropertyChanged(); }
    }

    public FolderEntryViewModel(string name, string fullPath, bool isFolder, ImageSource? icon = null)
    {
        Name = name;
        FullPath = fullPath;
        IsFolder = isFolder;
        _icon = icon;
    }

    public event PropertyChangedEventHandler? PropertyChanged;

    void OnPropertyChanged([CallerMemberName] string? name = null)
        => PropertyChanged?.Invoke(this, new PropertyChangedEventArgs(name));
}
