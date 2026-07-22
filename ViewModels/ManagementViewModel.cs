using System.Collections.ObjectModel;
using System.ComponentModel;
using System.Linq;
using System.Runtime.CompilerServices;
using System.Windows.Input;
using DesktopZones.Models;
using DesktopZones.Services;

namespace DesktopZones.ViewModels;

public class ManagementViewModel : INotifyPropertyChanged
{
    private readonly ZoneManager _zoneManager;
    private readonly ConfigService _configService;

    public ObservableCollection<Zone> Zones => _zoneManager.Zones;

    private bool _startWithWindows;
    public bool StartWithWindows
    {
        get => _startWithWindows;
        set
        {
            _startWithWindows = value;
            OnPropertyChanged();
            SaveSettings();
        }
    }

    private bool _startMinimized;
    public bool StartMinimized
    {
        get => _startMinimized;
        set
        {
            _startMinimized = value;
            OnPropertyChanged();
            SaveSettings();
        }
    }

    public ICommand CreateZoneCommand { get; }
    public ICommand DeleteZoneCommand { get; }
    public ICommand ToggleZoneCommand { get; }
    public ICommand EditZoneCommand { get; }
    public ICommand ShowAllCommand { get; }
    public ICommand HideAllCommand { get; }

    public ManagementViewModel(ZoneManager zoneManager, ConfigService configService)
    {
        _zoneManager = zoneManager;
        _configService = configService;

        var config = configService.Load();
        _startWithWindows = config.StartWithWindows;
        _startMinimized = config.StartMinimized;

        CreateZoneCommand = new RelayCommand(_ => CreateNewZone());
        DeleteZoneCommand = new RelayCommand<Zone>(zone => { if (zone != null) _zoneManager.DeleteZone(zone.Id); });
        ToggleZoneCommand = new RelayCommand<Zone>(zone => { if (zone != null) _zoneManager.ToggleZone(zone.Id); });
        EditZoneCommand = new RelayCommand<Zone>(zone => { if (zone != null) EditZone(zone); });
        ShowAllCommand = new RelayCommand(_ => _zoneManager.ShowAll());
        HideAllCommand = new RelayCommand(_ => _zoneManager.HideAll());

        _zoneManager.ZonesChanged += () => OnPropertyChanged(nameof(Zones));
    }

    private void CreateNewZone()
    {
        var zone = _zoneManager.CreateZone();
        EditZone(zone);
    }

    private void EditZone(Zone zone)
    {
        var dialog = new Views.ZoneSettingsDialog(zone, _zoneManager);
        if (dialog.ShowDialog() == true)
        {
            _zoneManager.UpdateZone(dialog.ResultZone);
        }
    }

    private void SaveSettings()
    {
        var config = _zoneManager.GetConfig();
        config.StartWithWindows = _startWithWindows;
        config.StartMinimized = _startMinimized;
        _zoneManager.UpdateConfig(config);

        // Update startup shortcut
        UpdateStartupShortcut();
    }

    private void UpdateStartupShortcut()
    {
        var startupPath = System.IO.Path.Combine(
            Environment.GetFolderPath(Environment.SpecialFolder.Startup),
            "DeskOrder.lnk");

        if (_startWithWindows)
        {
            CreateShortcut(startupPath);
        }
        else
        {
            if (System.IO.File.Exists(startupPath))
                System.IO.File.Delete(startupPath);
        }
    }

    private void CreateShortcut(string shortcutPath)
    {
        try
        {
            var exePath = Environment.ProcessPath;
            if (exePath == null) return;

            // Use COM to create shortcut
            Type? shellType = Type.GetTypeFromProgID("WScript.Shell");
            if (shellType == null) return;

            dynamic? shell = Activator.CreateInstance(shellType);
            if (shell == null) return;

            dynamic? shortcut = shell.CreateShortcut(shortcutPath);
            shortcut.TargetPath = exePath;
            shortcut.WorkingDirectory = System.IO.Path.GetDirectoryName(exePath);
            shortcut.Description = "DeskOrder - 秩序桌面";
            shortcut.Save();
        }
        catch
        {
            // Silently fail — startup shortcut is non-critical
        }
    }

    public event PropertyChangedEventHandler? PropertyChanged;
    protected void OnPropertyChanged([CallerMemberName] string? name = null)
    {
        PropertyChanged?.Invoke(this, new PropertyChangedEventArgs(name));
    }
}
