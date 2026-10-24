using System;
using System.Collections.ObjectModel;
using System.Linq;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Input;
using DesktopZones.Helpers;
using DesktopZones.Models;
using DesktopZones.Services;
using DesktopZones.ViewModels;
using DesktopZones.Views.Components;
using static DesktopZones.Views.Pages.PageHelpers;
using RelayCommand = DesktopZones.ViewModels.RelayCommand;

namespace DesktopZones.Views.Pages;

/// <summary>「播放器」列表页（律动条 / 唱片机）。结构与 ClockPage 一致：
/// 每行一个 EditableListRow，副标题显示形态 + 尺寸 + 当前播放状态。</summary>
public partial class MediaPlayerPage : UserControl
{
    readonly ManagementWindow _main;
    readonly WidgetService? _widgetService;
    readonly MediaSessionService _media = MediaSessionService.Instance;
    readonly LocalizationService _loc = LocalizationService.Instance;
    DesktopMediaPlayer? _selected;
    readonly ObservableCollection<EditableListRow> _rows = new();

    public MediaPlayerPage(ManagementWindow main, ManagementViewModel vm, WidgetService? widgetService)
    {
        InitializeComponent();
        _main = main;
        _widgetService = widgetService;
        ListHost.ItemsSource = _rows;
        Loaded += (_, _) =>
        {
            if (_widgetService != null) _widgetService.MediaPlayersChanged += RefreshList;
            RefreshList();
        };
        Unloaded += (_, _) =>
        {
            if (_widgetService != null) _widgetService.MediaPlayersChanged -= RefreshList;
        };
    }

    public void ApplyLoc() => RefreshList();

    public void RefreshList()
    {
        if (_widgetService == null) return;
        var players = _widgetService.MediaPlayers;
        CountLabel.Text = _loc.Get("MediaPage.ItemCount", players.Count);
        _rows.Clear();
        foreach (var p in players) _rows.Add(BuildRow(p));
        EmptyHint.Visibility = players.Count == 0 ? Visibility.Visible : Visibility.Collapsed;
        SetSelection(ListHost, _selected);
    }

    EditableListRow BuildRow(DesktopMediaPlayer player)
    {
        bool bars = player.Mode == MediaDisplayMode.Bars;
        string mode = bars ? _loc["MediaPage.Bars"] : _loc["MediaPage.Vinyl"];
        var now = _media.Now;
        string state = now.IsPlaying ? _loc["Media.Playing"]
            : now.HasSession ? _loc["Media.Paused"]
            : _loc["Media.Idle"];
        double w = bars ? player.BarsModeWidth ?? player.Width : player.VinylModeWidth ?? player.Width;
        double h = bars ? player.BarsModeHeight ?? player.Height : player.VinylModeHeight ?? player.Height;

        var row = new EditableListRow
        {
            Tag = player,
            Title = _loc["Manage.MediaPlayers"],
            Subtitle = $"{mode} · {state} · {(int)w}×{(int)h}",
            IsLocked = player.IsLocked,
            IsVisible = player.IsVisible,
        };
        ApplyIcon(row, player.IconChar, "Icon.Media");
        row.ReorderRequested += (src, targetIdx) =>
        {
            if (src.Tag is not DesktopMediaPlayer p || _widgetService == null) return;
            _widgetService.MoveMediaPlayer(p.Id, targetIdx);
            MoveRow(_rows, src, targetIdx);
        };
        row.LockCommand = new RelayCommand(_ =>
        {
            player.IsLocked = !player.IsLocked;
            _widgetService?.UpdateMediaPlayer(player);
            RefreshList();
        });
        row.VisibilityCommand = new RelayCommand(_ => _main.ToggleMediaPlayerWindow(player));
        row.DeleteCommand = new RelayCommand(_ => Delete(player));
        row.RenameCommand = new RelayCommand(_ => { /* 播放器没有自由命名 */ });
        row.PreviewMouseLeftButtonUp += (_, e) =>
        {
            if (e.OriginalSource is DependencyObject src)
            {
                var parent = src;
                while (parent != null && parent is not Button)
                    parent = LogicalTreeHelper.GetParent(parent);
                if (parent is Button) return;
            }
            Select(player);
        };
        return row;
    }

    void Select(DesktopMediaPlayer player)
    {
        PropertyWindowManager.Instance.DockTarget(player, _main);
        if (!ReferenceEquals(_selected, player))
        {
            _selected = player;
            SetSelection(ListHost, player);
        }
    }

    void NewPlayer_Click(object sender, RoutedEventArgs e)
    {
        _main.NewMediaPlayer();
        RefreshList();
    }

    void Delete(DesktopMediaPlayer player)
    {
        if (MessageBox.Show(_loc["MediaPage.DeleteConfirm"], _loc["MediaPage.DeleteTitle"],
                MessageBoxButton.YesNo, MessageBoxImage.Warning) != MessageBoxResult.Yes) return;
        _main.DeleteMediaPlayer(player);
        if (ReferenceEquals(_selected, player)) _selected = null;
        RefreshList();
    }
}
