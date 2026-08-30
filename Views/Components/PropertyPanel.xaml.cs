using System;
using System.ComponentModel;
using System.Globalization;
using System.IO;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Input;
using System.Windows.Media;
using System.Windows.Media.Animation;
using System.Windows.Shapes;
using DesktopZones.Helpers;
using DesktopZones.Models;
using DesktopZones.Services;
using DesktopZones.ViewModels;
using DesktopZones.Views;

namespace DesktopZones.Views.Components;

// ponytail: only the new "鼠标悬停自动展开" row is i18n-fetched; the rest of the
// rows still use hardcoded Chinese strings (ROADMAP v1.1 will migrate them in
// bulk — adding one now diverges from the surrounding style but is what the
// user explicitly asked for).

/// <summary>
/// Host panel for instance field UI. Three vertical sections:
/// header (instance name + undock/collapse), preview, field area.
/// Target DP drives the field-area rebuild: setting Target to a Zone
/// renders the Zone field tree; null clears it.
/// ponytail: field tree is built in code (not XAML) so per-target branching
/// stays a switch instead of dozens of UserControls with the same skeleton.
/// </summary>
public partial class PropertyPanel : UserControl
{
    public static readonly DependencyProperty InstanceNameProperty = DependencyProperty.Register(
        nameof(InstanceName), typeof(string), typeof(PropertyPanel), new PropertyMetadata(""));
    public static readonly DependencyProperty TargetProperty = DependencyProperty.Register(
        nameof(Target), typeof(object), typeof(PropertyPanel),
        new PropertyMetadata(null, (d, _) => ((PropertyPanel)d).OnTargetChanged()));
    public static readonly DependencyProperty IsFloatingProperty = DependencyProperty.Register(
        nameof(IsFloating), typeof(bool), typeof(PropertyPanel),
        new PropertyMetadata(false, (d, _) => ((PropertyPanel)d).OnIsFloatingChanged()));

    public string InstanceName
    {
        get => (string)GetValue(InstanceNameProperty);
        set => SetValue(InstanceNameProperty, value);
    }
    public object? Target
    {
        get => GetValue(TargetProperty);
        set => SetValue(TargetProperty, value);
    }

    /// <summary>True when the panel lives in a floating PropertyWindow; false when
    /// docked in ManagementWindow's right column. Drives the dock/undock toggle
    /// button: docked = undock icon + UndockRequested; floating = dock icon +
    /// DockRequested. Toggled by the host (PropertyWindow sets true on show;
    /// ManagementWindow leaves default false).</summary>
    public bool IsFloating
    {
        get => (bool)GetValue(IsFloatingProperty);
        set => SetValue(IsFloatingProperty, value);
    }

    /// <summary>
    /// Optional sink for "save back to manager" calls. The page that owns
    /// the panel wires this up once; field controls call it after mutating
    /// the target. Null is fine — properties still mutate in-memory for
    /// preview, persistence just won't happen.
    /// </summary>
    public Action<object>? Persist { get; set; }

    /// <summary>Live-preview sink (wired alongside <see cref="Persist"/> by the
    /// host). Field edits route through <c>Save()</c> → this callback to repaint
    /// the live desktop window without writing to disk; disk persistence happens
    /// only on Apply via <see cref="Persist"/>.</summary>
    public Action<object>? Preview { get; set; }

    // ── 状态区操作回调（由宿主 ManagementWindow 在 WirePropertyPanelPersist 时注入）──
    // PropertyPanel 不持有任何服务，所有窗口级/破坏性操作经回调回到宿主执行：
    // 显示/隐藏（ShowZone/HideZone/ToggleClockWindow/…）、删除/解散（带确认）、
    // 加入组合分区、分离单个分区、便签/面板快捷键预设菜单。

    /// <summary>切换目标的显示/隐藏（或面板启用）。宿主按目标类型路由到对应服务。</summary>
    public Action<object>? ToggleVisibility { get; set; }
    /// <summary>删除目标（宿主负责确认对话框 + 服务删除）。</summary>
    public Action<object>? DeleteTarget { get; set; }
    /// <summary>分区目标：「加入组合分区」按钮（仅当存在其它组合时显示）。</summary>
    public Action<Zone>? AddZoneToMerge { get; set; }
    /// <summary>组合分区目标：「分离单个分区」按钮。</summary>
    public Action<Zone>? DisbandSingleFromGroup { get; set; }
    /// <summary>便签目标：打开快捷键预设菜单（placement = 触发按钮）。</summary>
    public Action<StickyNote, FrameworkElement>? ShowNoteHotkeyMenu { get; set; }
    /// <summary>面板目标：打开快捷键预设菜单。</summary>
    public Action<PanelConfig, FrameworkElement>? ShowPanelHotkeyMenu { get; set; }
    /// <summary>面板目标：返回当前快捷键显示文本（PanelHotkey 在 AppConfig 上，面板拿不到）。</summary>
    public Func<string>? GetPanelHotkeyLabel { get; set; }

    public event EventHandler? UndockRequested;
    public event EventHandler? DockRequested;
    public event EventHandler? CollapseRequested;
    /// <summary>Host wires this to its PropertyTabStrip.CloseActiveTab().</summary>
    public event EventHandler? CloseTabRequested;
    /// <summary>Floating-only: host (PropertyWindow) wires this to close the whole
    /// floating window. Raised by the same header X button when IsFloating=true.</summary>
    public event EventHandler? CloseWindowRequested;

    /// <summary>True when there's an active tab to close. Host (Mgmt/PropertyWindow)
    /// binds this to the X button's Visibility.</summary>
    public static readonly DependencyProperty IsCloseableProperty = DependencyProperty.Register(
        nameof(IsCloseable), typeof(bool), typeof(PropertyPanel),
        new PropertyMetadata(false, (d, _) => ((PropertyPanel)d).OnIsCloseableChanged()));
    public bool IsCloseable
    {
        get => (bool)GetValue(IsCloseableProperty);
        set => SetValue(IsCloseableProperty, value);
    }

    // ponytail: i18n for the one row that uses it. Read on every rebuild so a
    // user-driven language switch reflects on the next Target change without us
    // having to subscribe to LocalizationService.LangChanged.
    readonly LocalizationService _loc = LocalizationService.Instance;

    // ponytail: Target switch snapshot for the Cancel button. Captured at
    // OnTargetChanged entry (when Target changes); restored by CancelBtn_Click.
    // Type-erased object? because Zone / Clock / Calendar / Note / PanelConfig
    // don't share a base — caller switches on runtime type. PanelConfig has
    // no Clone(), so the snapshot is PanelPresetConfig (the preset round-trip).
    // Cleared to null when Target is null.
    object? _snapshot;

    /// <summary>预设卡点击实时预览时抑制 OnTargetChanged 尾部的切换动画 —
    /// 每次点预设卡只重建字段树，不重放淡入+滑动。</summary>
    bool _suppressSwitchAnimation;

    /// <summary>Cached host Window resolved once on Loaded. Dialogs read this before
    /// ShowDialog — falls back to a fresh Window.GetWindow lookup, then refuses to open
    /// if still null (avoids the InvalidOperationException that ShowDialog(owner:null)
    /// raises the moment the dialog tries to Activate).</summary>
    public Window? CachedOwner { get; private set; }

    public PropertyPanel()
    {
        InitializeComponent();
        Loaded += (_, _) => CachedOwner = Window.GetWindow(this);
        Unloaded += (_, _) =>
        {
            _loc.LanguageChanged -= OnLanguageChanged;
            ThemeService.Changed -= OnThemeChanged;
            if (_zoneChangedHandler != null && Application.Current is App app2 && app2.ZoneManager is ZoneManager zm2)
            {
                zm2.ZonesChanged -= _zoneChangedHandler;
                zm2.ZoneVisibilityChanged -= _zoneVisibilityChangedHandler;
                zm2.LockChanged -= _zoneLockChangedHandler;
            }
            if (_notesChangedHandler != null && Application.Current is App app3 && app3.NotesService is NotesService ns)
                ns.NotesChanged -= _notesChangedHandler;
            if (_clocksChangedHandler != null && Application.Current is App app4 && app4.WidgetService is WidgetService ws)
            {
                ws.ClocksChanged -= _clocksChangedHandler;
                ws.CalendarsChanged -= _clocksChangedHandler;
            }
            if (_panelChangedHandler != null && Application.Current is App appP2 && appP2.PanelService is PanelService ps2)
                ps2.PanelEnabledChanged -= _panelChangedHandler;
        };
        _loc.LanguageChanged += OnLanguageChanged;
        ThemeService.Changed += OnThemeChanged;
        // ponytail: folder-mapping sync — the zone window can toggle the mapping
        // (✕ button / + menu / navigation), and the panel's checkbox + path row
        // rebuild when the manager reports a change to the current target's state.
        // 状态区同时挂在 ZonesChanged / NotesChanged / ClocksChanged / CalendarsChanged
        // 上做轻量实时刷新（只重建状态区控件，不碰字段树）。
        // 分区窗口自身的隐藏/锁定按钮只触发 ZoneVisibilityChanged / LockChanged
        // （不走 ZonesChanged），必须额外订阅才能让状态区开关实时跟随。
        if (Application.Current is App app && app.ZoneManager is ZoneManager zm)
        {
            _zoneChangedHandler = () =>
            {
                SyncFolderMappingStateFromManager();
                RefreshStatusArea();
            };
            _zoneVisibilityChangedHandler = (_, _) => RefreshStatusArea();
            _zoneLockChangedHandler = (_, _) => RefreshStatusArea();
            zm.ZonesChanged += _zoneChangedHandler;
            zm.ZoneVisibilityChanged += _zoneVisibilityChangedHandler;
            zm.LockChanged += _zoneLockChangedHandler;
        }
        if (Application.Current is App appN && appN.NotesService is NotesService notesService)
        {
            _notesChangedHandler = RefreshStatusArea;
            notesService.NotesChanged += _notesChangedHandler;
        }
        if (Application.Current is App appW && appW.WidgetService is WidgetService widgetService)
        {
            _clocksChangedHandler = RefreshStatusArea;
            widgetService.ClocksChanged += _clocksChangedHandler;
            widgetService.CalendarsChanged += _clocksChangedHandler;
        }
        // 面板启用开关实时同步 — 面板经热键/托盘/自身"─"按钮开关时,PanelService
        // 发 PanelEnabledChanged,状态区的启用开关立即跟随(参考分区/便签等组件的
        // Changed 事件订阅模式)。
        if (Application.Current is App appP && appP.PanelService is PanelService panelService)
        {
            _panelChangedHandler = RefreshStatusArea;
            panelService.PanelEnabledChanged += _panelChangedHandler;
        }
    }

    void OnLanguageChanged(string _) => OnTargetChanged();

    /// <summary>主题切换时重建字段树，重新解析自适应文字（chip 前景色）与主题 brush。</summary>
    void OnThemeChanged(AppThemeMode _) => OnTargetChanged();

    // ── Folder-mapping state sync (zone window ↔ style panel) ──

    Action? _zoneChangedHandler;
    Action<Guid, bool>? _zoneVisibilityChangedHandler;
    Action<string, bool>? _zoneLockChangedHandler;
    Action? _notesChangedHandler;
    Action? _clocksChangedHandler;
    Action? _panelChangedHandler;
    (bool Enabled, string Path)? _lastFolderMappingState;

    // 组合分区编辑器已移除文件夹映射 section（组级映射只保留在分区窗口层），
    // 同步只关注普通分区，避免组级映射变化无谓地全量重建组合分区字段树。
    (bool Enabled, string Path)? FolderMappingStateOf(object? target) => target switch
    {
        Zone z => (z.FolderMappingEnabled, z.FolderMappingPath ?? ""),
        _ => null,
    };

    /// <summary>Called BEFORE persisting the panel's own mapping edits so the
    /// synchronous ZonesChanged round-trip sees the state as unchanged.</summary>
    void CaptureFolderMappingState() => _lastFolderMappingState = FolderMappingStateOf(Target);

    /// <summary>ZonesChanged handler: rebuild only when the current target's
    /// mapping state actually moved (avoids resetting fields mid-edit).
    /// 同时实时同步「自定义图标」行 — 分区图标数变化（增删图标）时立即更新
    /// 门控状态，不用重开面板。</summary>
    void SyncFolderMappingStateFromManager()
    {
        if (Target == null) return;

        // 磁贴模式实时门控：图标数变化 → 刷新自定义图标行的可用状态。
        if (Target is Zone z)
        {
            int count = z.Items.Count;
            if (_lastZoneItemCount.HasValue && _lastZoneItemCount.Value != count)
            {
                _lastZoneItemCount = count;
                ApplyCustomIconGating(z);
            }
            else if (!_lastZoneItemCount.HasValue)
            {
                _lastZoneItemCount = count;
            }
        }

        var current = FolderMappingStateOf(Target);
        if (current == null) return;
        if (_lastFolderMappingState == current) return;
        _lastFolderMappingState = current;
        OnTargetChanged();
    }

    /// <summary>Sync the 4 footer buttons' IsEnabled to whether we have a snapshot.
    /// Null snapshot means Target is null (or unsupported type) — buttons stay disabled.
    /// Called from OnTargetChanged. Cheap; no allocation.</summary>
    void UpdateButtonBarEnabled()
    {
        var enabled = _snapshot != null;
        if (LoadPresetBtn != null) LoadPresetBtn.IsEnabled = enabled;
        if (SavePresetBtn != null) SavePresetBtn.IsEnabled = enabled;
        if (CancelBtn != null) CancelBtn.IsEnabled = enabled;
        if (ApplyBtn != null) ApplyBtn.IsEnabled = enabled;
    }

    void ApplyBtn_Click(object sender, RoutedEventArgs e)
    {
        // ponytail: preview mode — field edits only live-preview until Apply.
        // Apply commits the current preview state to disk, then closes the
        // floating style window (the docked panel stays open for editing).
        if (Target == null) return;
        if (Commit(Target) && IsFloating)
            CloseWindowRequested?.Invoke(this, EventArgs.Empty);
    }

    void CancelBtn_Click(object sender, RoutedEventArgs e)
    {
        // ponytail: preview mode — field edits were never persisted, so Cancel
        // only needs to restore the in-memory model from the OnTargetChanged
        // snapshot and repaint the live window back to that state. No disk
        // write here (the disk still holds the pre-edit values).
        if (Target == null || _snapshot == null) return;
        switch (Target)
        {
            case Zone z when _snapshot is Zone sz: CopyZoneFields(sz, z); break;
            case DesktopClock c when _snapshot is DesktopClock sc: CopyClockFields(sc, c); break;
            case DesktopCalendar cal when _snapshot is DesktopCalendar scal: CopyCalendarFields(scal, cal); break;
            case StickyNote n when _snapshot is StickyNote sn: CopyNoteFields(sn, n); break;
            case PanelConfig p when _snapshot is PanelPresetConfig sp: CopyPanelConfigFields(sp, p); break;
            case MergedGroupTarget g when _snapshot is Zone sz: CopyMergedGroupFields(sz, g.Master); break;
            // ponytail 2026-08-26: SubFolder 取消 — 还原到 snapshot 时的字段值。
            case ZoneItem sub when _snapshot is ZoneItem ssub && sub.Type == ItemType.SubFolder: CopySubfolderFields(ssub, sub); break;
        }
        Save(Target);  // live-preview refresh back to snapshot (no disk write)
        OnTargetChanged();  // rebuild UI from restored model + refresh snapshot
    }

    void LoadPresetBtn_Click(object sender, RoutedEventArgs e)
    {
        // ponytail: mirrors old WidgetSettingsDialog.LoadPreset_Click with two
        // independent cancel layers. The pre-picker payload snapshot restores
        // the live model + window when the inner dialog is cancelled; OK
        // re-baselines the outer snapshot so a later outer Cancel reverts to
        // the post-preset state (preserving the preset), not the pre-pick state.
        var (kind, payload) = BuildCurrentPayload();
        if (kind == null || payload == null || CachedOwner == null) return;

        var applied = PresetButtonsHelper.OpenLoad(CachedOwner, kind.Value, payload,
            onPicked: picked => ApplyPayload(picked),
            onCardPicked: record => ApplyCardPicked(record));

        if (applied == true)
        {
            OnTargetChanged();  // OK: re-capture snapshot to post-preset baseline
        }
        else
        {
            // Inner Cancel (or dialog dismissed) — revert to pre-picker state.
            RestoreFromPayload(payload);
            OnTargetChanged();
        }
    }

    /// <summary>Restore the live model + window to the state captured before the
    /// LoadPresetDialog opened (inner-cancel layer). Mirrors the historical
    /// WidgetSettingsDialog._loadDialogSnapshot restore.</summary>
    void RestoreFromPayload(object snapshot)
    {
        switch (Target)
        {
            case Zone z when snapshot is Zone sz: CopyZoneFields(sz, z); break;
            case MergedGroupTarget g when snapshot is Zone sz: CopyMergedGroupFields(sz, g.Master); break;
            case DesktopClock c when snapshot is DesktopClock sc: CopyClockFields(sc, c); break;
            case DesktopCalendar cal when snapshot is DesktopCalendar scal: CopyCalendarFields(scal, cal); break;
            case StickyNote n when snapshot is StickyNote sn: CopyNoteFields(sn, n); break;
            case PanelConfig p when snapshot is PanelPresetConfig sp: CopyPanelConfigFields(sp, p); break;
            case ZoneItem sub when sub.Type == ItemType.SubFolder && snapshot is ZoneItem ssub: CopySubfolderFields(ssub, sub); break;
        }
        if (Target != null) Save(Target);  // live-preview refresh back to pre-picker state
    }

    void SavePresetBtn_Click(object sender, RoutedEventArgs e)
    {
        var (kind, payload) = BuildCurrentPayload();
        if (kind == null || payload == null || CachedOwner == null) return;
        PresetButtonsHelper.OpenSave(CachedOwner, kind.Value, payload);
    }

    /// <summary>Snapshot the current Target into (PresetKind, payload) for the
    /// preset helper. PanelConfig round-trips through PanelPresetConfig (which has
    /// Clone()) — the helper expects a serializable snapshot.</summary>
    (PresetKind? kind, object? payload) BuildCurrentPayload() => Target switch
    {
        Zone z => (PresetKind.Zone, (object?)z.Clone()),
        MergedGroupTarget g => (PresetKind.Zone, (object?)g.Master.Clone()),
        DesktopClock c => (PresetKind.Clock, (object?)c.Clone()),
        DesktopCalendar cal => (PresetKind.Calendar, (object?)cal.Clone()),
        StickyNote n => (PresetKind.StickyNote, (object?)n.Clone()),
        PanelConfig p => (PresetKind.Panel, (object?)PanelPresetConfig.FromConfig(new AppConfig { Panel = p })),
        // ponytail 2026-08-26: SubFolder preset — PresetKind.Subfolder 走 PresetService
        // 已有的 SubfolderPreset 序列化分支 (Models/SubfolderPreset.cs)。
        ZoneItem sub when sub.Type == ItemType.SubFolder => (PresetKind.Subfolder, (object?)sub.Clone()),
        _ => (null, null),
    };

    /// <summary>Sync the dialog UI from the live model. Called after the inner
    /// LoadPresetDialog closes with OK — model is already at preset state via
    /// ApplyCardPicked; resync UI without re-capturing snapshot (resnapshot
    /// happens in OnTargetChanged at the bottom of LoadPresetBtn_Click).</summary>
    void ApplyPayload(object? picked)
    {
        // picked is unused here — payload identity was the trigger, the model
        // is already mutated by ApplyCardPicked. We just need to rebuild fields.
        // (Mirrors historical WidgetSettingsDialog.ApplyPayload.)
        OnTargetChanged();
    }

    /// <summary>Per-card click hook for the Load Preset dialog. Writes the
    /// preset's payload into the live model and refreshes the live widget —
    /// real-time preview. Mirrors the old WidgetSettingsDialog.ApplyCardPicked,
    /// minus the deleted global-appearance sync (UseGlobalAppearance removed
    /// in commit e4bd2cf).</summary>
    void ApplyCardPicked(PresetRecord record)
    {
        var app = Application.Current as App;
        switch (Target)
        {
            case Zone z when record is ZonePreset zp:
                CopyZoneFields(zp.Zone, z);
                // ponytail: real-time preset preview — repaint the live desktop
                // zone window (no disk write; Apply commits later).
                app?.ZoneManager?.GetZoneWindow(z.Id)?.RefreshZone(z);
                break;
            case MergedGroupTarget g when record is ZonePreset zp:
                CopyZoneFields(zp.Zone, g.Master);
                // ponytail: preset zone's group style rides along; the group's
                // identity (name/icon/membership) is never overwritten.
                CloneHelper.CopyBaseProperties<MergedGroupStyle>(zp.Zone.MergedGroupStyle, g.Master.MergedGroupStyle);
                app?.ZoneManager?.GetZoneWindow(g.Master.Id)?.RefreshZone(g.Master);
                break;
            case DesktopClock c when record is ClockPreset cp:
                CopyClockFields(cp.Clock, c);
                app?.GetClockWindow(c.Id)?.RefreshAppearance(c);
                break;
            case DesktopCalendar cal when record is CalendarPreset cap:
                CopyCalendarFields(cap.Calendar, cal);
                app?.GetCalendarWindow(cal.Id)?.RefreshAppearance(cal);
                break;
            case StickyNote n when record is StickyNotePreset snp:
                CopyNoteFields(snp.Note, n);
                if (app?.NotesService?.Windows.TryGetValue(n.Id, out var nw) == true && nw is StickyNoteWindow snw)
                    snw.RefreshAppearance(n);
                break;
            case PanelConfig p when record is PanelPreset pp:
                CopyPanelConfigFields(pp.Config, p);
                // ponytail: repaint the live panel window from the mutated live
                // config (no disk write; Apply commits later).
                app?.PanelService?.RefreshAppearance();
                break;
            // ponytail 2026-08-26: SubFolder preset — 镜像 preset.Subfolder 的字段到当前 ZoneItem,
            // SubItems 内容不动 (SubfolderPreset.Clone 不含 SubItems,符合 spec §4.5)。
            case ZoneItem sub when sub.Type == ItemType.SubFolder && record is SubfolderPreset sp:
                CopySubfolderFields(sp.Subfolder, sub);
                // ponytail: repaint the parent zone window so the subfolder's
                // icon/fill/glass change shows on the desktop (no disk write).
                var parentZone = app?.ZoneManager?.Zones.FirstOrDefault(z => z.Items.Contains(sub));
                if (parentZone != null)
                    app?.ZoneManager?.GetZoneWindow(parentZone.Id)?.RefreshZone(parentZone);
                break;
        }
        _suppressSwitchAnimation = true;
        try { OnTargetChanged(); }
        finally { _suppressSwitchAnimation = false; }  // resync UI to the new model state
    }

    void ToggleBtn_Click(object sender, RoutedEventArgs e)
    {
        // ponytail: same button serves both directions — when docked it pops out,
        // when floating it docks back. Routing is by IsFloating so the host
        // doesn't need to swap event handlers.
        if (IsFloating) DockRequested?.Invoke(this, EventArgs.Empty);
        else UndockRequested?.Invoke(this, EventArgs.Empty);
    }

    void CollapseBtn_Click(object sender, RoutedEventArgs e) => CollapseRequested?.Invoke(this, EventArgs.Empty);

    void CloseTabBtn_Click(object sender, RoutedEventArgs e)
    {
        // ponytail 2026-08-28: 诊断 — 浮窗"开一下就没了"每次都死在这里。记录点击时光标
        // 相对 ✕ 的位置与鼠标捕获状态,分辨真实误触(光标在按钮上)与输入重放幻影
        // (光标远离按钮 → staged input 借捕获路由)。
        if (IsFloating)
        {
            try
            {
                var btn = (System.Windows.Controls.Button)sender;
                var rel = System.Windows.Input.Mouse.GetPosition(btn);
                var screenPx = btn.PointToScreen(rel);
                System.Diagnostics.Trace.WriteLine(
                    $"[PropWin] CloseTabBtn_Click: 光标在✕内({rel.X:F0},{rel.Y:F0}) 屏幕({screenPx.X:F0},{screenPx.Y:F0}) " +
                    $"IsMouseOver={btn.IsMouseOver} Captured={System.Windows.Input.Mouse.Captured?.GetType().Name ?? "null"}");
            }
            catch { }
        }
        // ponytail: one X, two meanings, same placement/style as the docked
        // panel. Docked → close the active tab; floating → close the floating
        // window itself ("点击后浮窗直接关闭").
        if (IsFloating) CloseWindowRequested?.Invoke(this, EventArgs.Empty);
        else CloseTabRequested?.Invoke(this, EventArgs.Empty);
    }

    void OnIsCloseableChanged()
    {
        if (CloseTabBtn != null)
            CloseTabBtn.Visibility = IsCloseable ? Visibility.Visible : Visibility.Collapsed;
    }

    void OnIsFloatingChanged()
    {
        // ponytail: do NOT swap the icon here — the phased flip below swaps it
        // while the button is invisible (ScaleX=0 at the midpoint) so the
        // transition reads as "the button flipped over and is now the new one".
        if (ToggleBtn == null || ToggleIcon == null) return;
        ToggleBtn.ToolTip = IsFloating ? _loc["Common.Dock"] : _loc["Common.Undock"];
        AnimateToggleFlip();
    }

    void AnimateToggleFlip()
    {
        if (ToggleIconScale == null) return;
        // ponytail: true horizontal mirror via ScaleX. Two phases:
        //   1) collapse to ScaleX=0  (button "closes" like a clamshell)
        //   2) swap icon while invisible, then expand to target ±1
        // Phase 1 uses DecelSpline (ends slow — meets the icon swap); phase 2
        // uses AccentSpline (starts fast — opens up from the invisible seam).
        // NOTE: Motion.DecelSpline / Motion.AccentSpline are KeySpline values
        // (consumed by SplineDoubleKeyFrame), NOT IEasingFunction — casting
        // them to IEasingFunction throws InvalidCastException at runtime.
        // Only Motion.StandardSpline (a CubicEase EaseOut) is an IEasingFunction;
        // reuse it for both phases.
        var toFloating = IsFloating;
        var easing = (System.Windows.Media.Animation.IEasingFunction)FindResource("Motion.StandardSpline");
        var phase1 = new System.Windows.Media.Animation.DoubleAnimation
        {
            To = 0,
            Duration = TimeSpan.FromMilliseconds(140),
            EasingFunction = easing,
        };
        phase1.Completed += (_, _) =>
        {
            if (ToggleIconScale == null) return;
            // Swap icon while invisible (ScaleX=0).
            ToggleIcon.Data = (System.Windows.Media.Geometry)FindResource(
                toFloating ? "Icon.Dock" : "Icon.Undock");
            var phase2 = new System.Windows.Media.Animation.DoubleAnimation
            {
                From = 0,
                To = toFloating ? -1.0 : 1.0,
                Duration = TimeSpan.FromMilliseconds(160),
                EasingFunction = easing,
            };
            ToggleIconScale.BeginAnimation(System.Windows.Media.ScaleTransform.ScaleXProperty, phase2);
        };
        ToggleIconScale.BeginAnimation(System.Windows.Media.ScaleTransform.ScaleXProperty, phase1);
    }

    void OnTargetChanged()
    {
        if (FieldScroller == null) return; // pre-InitializeComponent guard

        // ponytail: capture snapshot BEFORE building the field tree so Cancel
        // restores the model state the user saw when they opened the panel.
        // PanelConfig lacks a Clone(); route through PanelPresetConfig round-trip.
        _snapshot = Target switch
        {
            Zone z => z.Clone(),
            MergedGroupTarget g => g.Master.Clone(),
            DesktopClock c => c.Clone(),
            DesktopCalendar cal => cal.Clone(),
            StickyNote n => n.Clone(),
            PanelConfig p => PanelPresetConfig.FromConfig(new AppConfig { Panel = p }).Clone(),
            // ponytail 2026-08-26: SubFolder 用 ZoneItem.Clone() (已有,深拷贝 SubItems + 所有专属字段)。
            ZoneItem sub when sub.Type == ItemType.SubFolder => sub.Clone(),
            _ => null,
        };
        UpdateButtonBarEnabled();

        switch (Target)
        {
            case Zone z:
                InstanceName = z.Name;
                SetInstanceIcon("Icon.Zones");
                BuildZoneFields(z);
                break;
            // ponytail 2026-08-25: Clock/Calendar/Note/Panel targets build their
            // own field trees per the per-component settings spec. Each builder
            // reuses MakeSection/MakeCheckRow/MakeColorRow/MakeTextRow/
            // MakeNumberSubBlock/MakeSliderRow/MakeCheckRowWithSideBtn from
            // BuildZoneFields, plus the shared dialogs (动效设置 / 液态玻璃) and
            // the BgImageBinding-based background-image row.
            // Header = icon + window name, matching the management list rows
            // and the tab strip titles (no "某某设置" labels).
            case DesktopClock c:
                InstanceName = $"{_loc["Breadcrumb.Clock"]} ({(c.Mode == ClockDisplayMode.Digital ? _loc["ClockProp.Digital"] : _loc["ClockProp.Analog"])})";
                SetInstanceIcon("Icon.Clock");
                BuildClockFields(c);
                break;
            case DesktopCalendar cal:
                InstanceName = $"{_loc["Breadcrumb.Calendar"]} {cal.DisplayYear}-{cal.DisplayMonth:D2}";
                SetInstanceIcon("Icon.Calendar");
                BuildCalendarFields(cal);
                break;
            case StickyNote note:
                InstanceName = string.IsNullOrEmpty(note.Title) ? _loc["StickyNotePage.FallbackTitle"] : note.Title;
                SetInstanceIcon("Icon.Sticky");
                BuildNoteFields(note);
                break;
            case PanelConfig p:
                InstanceName = _loc["PanelPage.PanelTitle"];
                SetInstanceIcon("Icon.Panel");
                BuildPanelFields(p);
                break;
            // ponytail 2026-08-26: merged-group editor (组合分区字段分类.txt).
            // Group-level style lives on the master's MergedGroupStyle +
            // MergedGroupMembership; the master's per-zone editor stays reachable
            // from the Zones page.
            case MergedGroupTarget g:
                InstanceName = string.IsNullOrEmpty(g.Master.MergedGroupMembership.DisplayName)
                    ? g.Master.Name : g.Master.MergedGroupMembership.DisplayName;
                SetInstanceIcon("Icon.Merged");
                BuildMergedGroupFields(g.Master);
                break;
            // ponytail 2026-08-26: Task 7 — SubFolder zone item style editor.
            // 5 groups + 15 controls mirror docs/superpowers/specs/2026-08-25-subfolder-design.md §5.
            case ZoneItem sub when sub.Type == ItemType.SubFolder:
                InstanceName = sub.Name;
                SetInstanceIcon("Icon.Folder");
                BuildSubfolderFields(sub);
                break;
            default:
                InstanceName = "";
                SetInstanceIcon(null);
                FieldScroller.Content = new TextBlock
                {
                    Margin = new Thickness(16),
                    Text = Target == null ? _loc["PropertyPanel.NoTarget"] : _loc["PropertyPanel.NotImplemented"],
                    Foreground = (Brush)FindResource("Brush.Text.Tertiary"),
                };
                break;
        }
        // Folder-mapping sync baseline: captured after the field tree is built.
        _lastFolderMappingState = FolderMappingStateOf(Target);
        // 状态区 + 磁贴门控：字段树建完后按当前状态渲染。
        BuildStatusArea(Target);
        ApplyTileGating(CurrentTileMode());
        AnimateSwitch();
    }

    /// <summary>Assign the header icon for the active target; pass null/unknown
    /// keys to collapse it (NoTarget / NotImplemented placeholders).</summary>
    void SetInstanceIcon(string? resourceKey)
    {
        if (InstanceIcon == null) return;
        if (string.IsNullOrEmpty(resourceKey) ||
            FindResource(resourceKey) is not System.Windows.Media.Geometry geometry)
        {
            InstanceIcon.Visibility = Visibility.Collapsed;
            return;
        }
        InstanceIcon.Data = geometry;
        InstanceIcon.Visibility = Visibility.Visible;
    }

    // ponytail: spec §4.3 — selection switch: Opacity 0→1 + TranslateX 12→0.
    // Runs on each Target change so clicks between rows replay the motion.
    void AnimateSwitch()
    {
        if (RootGrid == null) return;
        if (_suppressSwitchAnimation)
        {
            // 预设卡点击实时预览：跳过淡入+滑动，直接落到最终可见态。
            RootGrid.Opacity = 1;
            RootTranslate.X = 0;
            return;
        }
        RootGrid.Opacity = 0;
        RootTranslate.X = 12;
        var fade = new System.Windows.Media.Animation.DoubleAnimation
        {
            From = 0, To = 1,
            Duration = (Duration)FindResource("Motion.Normal"),
        };
        var slide = new System.Windows.Media.Animation.DoubleAnimation
        {
            From = 12, To = 0,
            Duration = (Duration)FindResource("Motion.Normal"),
        };
        RootGrid.BeginAnimation(OpacityProperty, fade);
        RootTranslate.BeginAnimation(System.Windows.Media.TranslateTransform.XProperty, slide);
    }

    // ── Field tree for Zone ──
    //
    // Sections: 开关区 / 基本 / 标题栏 / 边框与填充 / 液态玻璃 / 背景图片
    // Each control wires back via the Save() closure — mutating the Zone
    // instance and repainting the live window via Preview (preview mode).
    // Persist (disk write) is deferred to Apply/Commit; both callbacks are
    // wired by the host page. If not wired, updates stay in-memory only.

    /// <summary>重新渲染字段树以反映 TileMode / HideAppName / CustomIcon 的依赖更新。</summary>
    void Rebuild(Zone z) => BuildZoneFields(z);

    // ── 状态区（顶部实时状态条）──
    //
    // 替代原「边框+横杠」占位。内容跟随目标真实状态实时变化：
    // 显示/锁定滑动开关（SwitchStyle）、删除分区按钮（Apply 同款填充）、
    // 状态 chips（文件夹映射 / 监听中 / 组合分区中 — 激活亮起、未激活变暗）、
    // 面板与便签的快捷键设置行。破坏性/窗口级操作经宿主回调执行。

    /// <summary>按当前 Target 全量构建状态区。SubFolder 等无适用内容的目标折叠整个区域。</summary>
    void BuildStatusArea(object? target)
    {
        if (StatusHost == null) return; // pre-InitializeComponent guard
        StatusHost.Children.Clear();
        // 清空增量刷新引用，由各构建器重新登记。
        _statusVisibleCb = null;
        _statusLockCb = null;
        _statusChips.Clear();
        _statusHotkeyText = null;
        _statusHotkeyGetter = null;
        switch (target)
        {
            case Zone z: BuildZoneStatus(z); break;
            case MergedGroupTarget g: BuildMergedGroupStatus(g); break;
            case DesktopClock c: BuildClockStatus(c); break;
            case DesktopCalendar cal: BuildCalendarStatus(cal); break;
            case StickyNote n: BuildNoteStatus(n); break;
            case PanelConfig p: BuildPanelStatus(p); break;
            default:
                StatusArea.Visibility = Visibility.Collapsed;
                _statusBuiltFor = null;
                return;
        }
        StatusArea.Visibility = Visibility.Visible;
        _statusBuiltFor = target;
    }

    /// <summary>轻量实时刷新 — 增量更新已有开关/词条状态，不重建控件、不碰字段树。
    /// 开关值真正变化时才触发 SwitchStyle 的滑动动画；无关事件（如便签打字保存）
    /// 赋值同值不产生动画，不会闪。</summary>
    public void RefreshStatusArea()
    {
        if (Target == null) { StatusArea.Visibility = Visibility.Collapsed; return; }
        if (!ReferenceEquals(_statusBuiltFor, Target)) { BuildStatusArea(Target); return; }
        switch (Target)
        {
            case Zone z:
                if (_statusVisibleCb != null) _statusVisibleCb.IsChecked = z.IsVisible;
                if (_statusLockCb != null) _statusLockCb.IsChecked = z.IsLocked;
                break;
            case MergedGroupTarget g:
                if (_statusVisibleCb != null) _statusVisibleCb.IsChecked = g.Master.IsVisible;
                if (_statusLockCb != null) _statusLockCb.IsChecked = g.Master.IsLocked;
                break;
            case DesktopClock c:
                if (_statusVisibleCb != null) _statusVisibleCb.IsChecked = c.IsVisible;
                break;
            case DesktopCalendar cal:
                if (_statusVisibleCb != null) _statusVisibleCb.IsChecked = cal.IsVisible;
                break;
            case StickyNote n:
                if (_statusVisibleCb != null) _statusVisibleCb.IsChecked = n.IsVisible;
                break;
            case PanelConfig p:
                if (_statusVisibleCb != null) _statusVisibleCb.IsChecked = p.PanelEnabled;
                break;
        }
        foreach (var (chip, isActive) in _statusChips)
            ApplyChipState(chip, isActive());
        if (_statusHotkeyText != null && _statusHotkeyGetter != null)
            _statusHotkeyText.Text = _statusHotkeyGetter();
    }

    CheckBox? _statusVisibleCb;
    CheckBox? _statusLockCb;
    object? _statusBuiltFor;
    readonly List<(Border Chip, Func<bool> IsActive)> _statusChips = new();
    TextBlock? _statusHotkeyText;
    Func<string>? _statusHotkeyGetter;

    void ApplyChipState(Border chip, bool active)
    {
        chip.Opacity = active ? 1.0 : 0.75;
        chip.Background = active
            ? new SolidColorBrush(Color.FromArgb(0x40, 0x4A, 0xC0, 0x4A))
            : (Brush)FindResource("Brush.Bg.Input");
        if (chip.Child is TextBlock tb)
            tb.Foreground = active
                ? (Brush)FindResource("Brush.Text.Primary")
                : (Brush)FindResource("Brush.Text.Tertiary");
    }

    /// <summary>分区显示/隐藏 — 与窗口标题栏隐藏按钮、恢复按钮走同一条路径：
    /// ZoneManager.HideZone/ShowZone 内部会调用 ZoneWindow.HideZone/ShowZone，
    /// 从而正确播放窗口折叠/展开动画、切换恢复按钮并触发 ZonesChanged /
    /// ZoneVisibilityChanged 同步刷新状态区开关。</summary>
    void ApplyZoneVisibility(Zone z)
    {
        var zm = (Application.Current as App)?.ZoneManager;
        if (zm == null) return;
        if (z.IsVisible) zm.HideZone(z.Id); // → window.HideZone（窗口隐藏按钮同款；无恢复按钮则彻底隐藏）
        else zm.ShowZone(z);                // → window.ShowZone（恢复按钮/重新打开同款）
    }

    /// <summary>分区锁定 — 与窗口标题栏锁按钮走同一条路径：SetLocked 触发
    /// ZoneManager.LockChanged → ZoneWindow.OnServiceLockChanged → ApplyLockState
    /// 切换窗口锁按钮图标；再 NotifyChanged + SaveConfig 持久化并刷新列表/状态区。
    /// 不能只走 Save()（UpdateZone 不触发 LockChanged，窗口按钮图标不会变）。</summary>
    void ApplyZoneLock(Zone z, bool locked)
    {
        z.IsLocked = locked;
        var zm = (Application.Current as App)?.ZoneManager;
        if (zm == null) return;
        zm.SetLocked(z.Id.ToString(), locked); // LockChanged → 窗口锁按钮图标切换
        zm.NotifyChanged();                    // ZonesChanged → 列表行 / 状态区刷新
        zm.SaveConfig();                       // 持久化（窗口 LockBtn_Click 同款）
    }

    // ── 分区 ──

    void BuildZoneStatus(Zone z)
    {
        // 行1：显示/锁定开关
        var row1 = new DockPanel { LastChildFill = false };
        _statusVisibleCb = AddStatusSwitch(row1, _loc["PropertyPanel.Status.Visible"], z.IsVisible,
            _ => ApplyZoneVisibility(z)); // 与窗口显示/隐藏按钮同路径
        _statusLockCb = AddStatusSwitch(row1, _loc["PropertyPanel.Status.Locked"], z.IsLocked,
            on => ApplyZoneLock(z, on));
        StatusHost.Children.Add(row1);

        // 行2：操作按钮（加入组合分区 / 删除分区）
        var row2 = new DockPanel { LastChildFill = false, Margin = new Thickness(0, 10, 0, 0) };
        var delete = MakeStatusDangerButton(_loc["PropertyPanel.Status.DeleteZone"]);
        delete.Click += (_, _) => DeleteTarget?.Invoke(z);
        DockPanel.SetDock(delete, Dock.Right);
        row2.Children.Add(delete);
        var zm = (Application.Current as App)?.ZoneManager;
        bool otherGroups = zm != null && zm.Zones.Any(o =>
            o.MergedGroupMembership.SubZoneIds.Count > 0 && o.Id != z.Id);
        if (otherGroups)
        {
            var add = MakeStatusOutlineButton(_loc["Manage.Zone.AddToMerge"]);
            add.Click += (_, _) => AddZoneToMerge?.Invoke(z);
            row2.Children.Add(add);
        }
        StatusHost.Children.Add(row2);

        // 行3：状态词条单独一行（底部），不再与开关挤同一行
        StatusHost.Children.Add(MakeZoneStatusChips(z, topMargin: 10));
    }

    FrameworkElement MakeZoneStatusChips(Zone z, double topMargin = 0)
    {
        var sp = new StackPanel
        {
            Orientation = Orientation.Horizontal,
            VerticalAlignment = VerticalAlignment.Center,
            Margin = new Thickness(0, topMargin, 0, 0),
        };
        // 与列表行徽章同款绿底；未激活则暗下去（灰底 + 三级文字）。
        // 登记 (chip, getter) 供增量刷新按真实状态更新亮/暗。
        var folderChip = MakeStatusChip(_loc["Manage.Status.FolderMapping"],
            z.FolderMappingEnabled || z.MergedGroupStyle.FolderMappingEnabled);
        _statusChips.Add((folderChip, () => z.FolderMappingEnabled || z.MergedGroupStyle.FolderMappingEnabled));
        sp.Children.Add(folderChip);

        var listeningChip = MakeStatusChip(_loc["PropertyPanel.Status.Listening"], z.AutoOrganizeWatching);
        _statusChips.Add((listeningChip, () => z.AutoOrganizeWatching));
        sp.Children.Add(listeningChip);

        var mergedChip = MakeStatusChip(_loc["PropertyPanel.Status.InMergedGroup"],
            z.MergedGroupMembership.GroupId.HasValue || z.MergedGroupMembership.SubZoneIds.Count > 0);
        _statusChips.Add((mergedChip, () =>
            z.MergedGroupMembership.GroupId.HasValue || z.MergedGroupMembership.SubZoneIds.Count > 0));
        sp.Children.Add(mergedChip);
        return sp;
    }

    // ── 组合分区 ──

    void BuildMergedGroupStatus(MergedGroupTarget g)
    {
        var z = g.Master;
        // 行1：显示/锁定开关（组合分区自身无需「组合分区中」状态词条）
        var row1 = new DockPanel { LastChildFill = false };
        _statusVisibleCb = AddStatusSwitch(row1, _loc["PropertyPanel.Status.Visible"], z.IsVisible,
            _ => ApplyZoneVisibility(z)); // 与组合分区窗口显示/隐藏按钮同路径
        _statusLockCb = AddStatusSwitch(row1, _loc["PropertyPanel.Status.Locked"], z.IsLocked,
            on => ApplyZoneLock(z, on));
        StatusHost.Children.Add(row1);

        // 行2：分离单个分区 / 解散组合分区
        var row2 = new DockPanel { LastChildFill = false, Margin = new Thickness(0, 10, 0, 0) };
        var disband = MakeStatusDangerButton(_loc["Merge.DisbandAll"]);
        disband.Click += (_, _) => DeleteTarget?.Invoke(g);
        DockPanel.SetDock(disband, Dock.Right);
        row2.Children.Add(disband);
        if (z.MergedGroupMembership.SubZoneIds.Count > 0)
        {
            var single = MakeStatusOutlineButton(_loc["MergePage.DisbandSingle"]);
            single.Click += (_, _) => DisbandSingleFromGroup?.Invoke(z);
            row2.Children.Add(single);
        }
        StatusHost.Children.Add(row2);
    }

    // ── 时钟 / 日历 ──

    void BuildClockStatus(DesktopClock c)
    {
        var row1 = new DockPanel { LastChildFill = false };
        _statusVisibleCb = AddStatusSwitch(row1, _loc["PropertyPanel.Status.Visible"], c.IsVisible,
            _ => ToggleVisibility?.Invoke(c));
        StatusHost.Children.Add(row1);
        AddDeleteRow(_loc["ClockPage.Delete"], c);
    }

    void BuildCalendarStatus(DesktopCalendar cal)
    {
        var row1 = new DockPanel { LastChildFill = false };
        _statusVisibleCb = AddStatusSwitch(row1, _loc["PropertyPanel.Status.Visible"], cal.IsVisible,
            _ => ToggleVisibility?.Invoke(cal));
        StatusHost.Children.Add(row1);
        AddDeleteRow(_loc["CalendarPage.Delete"], cal);
    }

    void AddDeleteRow(string label, object target)
    {
        var row = new DockPanel { LastChildFill = false, Margin = new Thickness(0, 10, 0, 0) };
        var delete = MakeStatusDangerButton(label);
        delete.Click += (_, _) => DeleteTarget?.Invoke(target);
        DockPanel.SetDock(delete, Dock.Right);
        row.Children.Add(delete);
        StatusHost.Children.Add(row);
    }

    // ── 便签（删除按钮下方放快捷键设置）──

    void BuildNoteStatus(StickyNote n)
    {
        var row1 = new DockPanel { LastChildFill = false };
        _statusVisibleCb = AddStatusSwitch(row1, _loc["PropertyPanel.Status.Visible"], n.IsVisible,
            _ => ToggleVisibility?.Invoke(n));
        StatusHost.Children.Add(row1);
        AddDeleteRow(_loc["StickyNotePage.Delete"], n);
        StatusHost.Children.Add(MakeHotkeyStatusRow(
            () => n.HotkeyEnabled ? ManagementWindow.GetHotkeyLabel(n.HotkeyModifiers, n.HotkeyKey) : _loc["Hotkey.None"],
            btn => ShowNoteHotkeyMenu?.Invoke(n, btn)));
    }

    // ── 面板（无删除概念：启用开关 + 快捷键设置）──

    void BuildPanelStatus(PanelConfig p)
    {
        var row1 = new DockPanel { LastChildFill = false };
        _statusVisibleCb = AddStatusSwitch(row1, _loc["PropertyPanel.Status.Enabled"], p.PanelEnabled,
            _ => ToggleVisibility?.Invoke(p));
        StatusHost.Children.Add(row1);
        StatusHost.Children.Add(MakeHotkeyStatusRow(
            () => GetPanelHotkeyLabel?.Invoke() ?? _loc["Hotkey.None"],
            btn => ShowPanelHotkeyMenu?.Invoke(p, btn)));
    }

    // ── 状态区控件构建器 ──

    /// <summary>滑动开关行：固定标签 + SwitchStyle（圆角轨道+圆点，开=主题色填充）。
    /// 返回 CheckBox 供增量刷新引用。</summary>
    CheckBox AddStatusSwitch(Panel panel, string label, bool isOn, Action<bool> onToggle)
    {
        var cb = new CheckBox
        {
            IsChecked = isOn,
            Style = (Style)FindResource("SwitchStyle"),
            VerticalAlignment = VerticalAlignment.Center,
            Margin = new Thickness(6, 0, 0, 0),
        };
        // Click 只在用户点击时触发（程序改 IsChecked 不触发）→ 增量刷新不会回声。
        cb.Click += (_, _) => onToggle(cb.IsChecked == true);
        var stack = new StackPanel
        {
            Orientation = Orientation.Horizontal,
            Margin = new Thickness(0, 0, 16, 0),
            VerticalAlignment = VerticalAlignment.Center,
        };
        stack.Children.Add(new TextBlock
        {
            Text = label,
            FontSize = 11,
            Foreground = (Brush)FindResource("Brush.Text.Secondary"),
            VerticalAlignment = VerticalAlignment.Center,
        });
        stack.Children.Add(cb);
        panel.Children.Add(stack);
        return cb;
    }

    /// <summary>状态 chip — 激活亮起（列表徽章同款绿底），未激活和禁用一样暗下去。</summary>
    Border MakeStatusChip(string text, bool active)
    {
        return new Border
        {
            CornerRadius = new CornerRadius(8),
            Padding = new Thickness(8, 2, 8, 2),
            Margin = new Thickness(4, 0, 0, 0),
            VerticalAlignment = VerticalAlignment.Center,
            Opacity = active ? 1.0 : 0.75,
            Background = active
                ? new SolidColorBrush(Color.FromArgb(0x40, 0x4A, 0xC0, 0x4A))
                : (Brush)FindResource("Brush.Bg.Input"),
            Child = new TextBlock
            {
                Text = text,
                FontSize = 10,
                FontWeight = FontWeights.SemiBold,
                Foreground = active
                    ? (Brush)FindResource("Brush.Text.Primary")
                    : (Brush)FindResource("Brush.Text.Tertiary"),
            },
        };
    }

    /// <summary>删除/解散按钮 — 固定蓝填充按钮（FillBtn：深蓝底白字，悬停内部高光）。</summary>
    Button MakeStatusDangerButton(string text)
    {
        var btn = new Button
        {
            Content = text,
            Cursor = Cursors.Hand,
            Style = (Style)FindResource("FillBtn"),
            Padding = new Thickness(12, 5, 12, 5),
            FontSize = 11,
            VerticalAlignment = VerticalAlignment.Center,
        };
        return btn;
    }

    /// <summary>次要操作按钮 — 固定蓝描边按钮（OutlineBtn：亮蓝底 + 蓝边框 + 白字）。</summary>
    Button MakeStatusOutlineButton(string text)
    {
        var btn = new Button
        {
            Content = text,
            Cursor = Cursors.Hand,
            Style = (Style)FindResource("OutlineBtn"),
            Padding = new Thickness(12, 5, 12, 5),
            FontSize = 11,
            VerticalAlignment = VerticalAlignment.Center,
            Margin = new Thickness(0, 0, 6, 0),
        };
        return btn;
    }

    /// <summary>快捷键设置行：标签 + 当前值（等宽字体）+ 右侧「设置快捷键」按钮。
    /// getLabel 用于增量刷新当前值文本。</summary>
    FrameworkElement MakeHotkeyStatusRow(Func<string> getLabel, Action<FrameworkElement> onOpen)
    {
        var dock = new DockPanel { LastChildFill = true, Margin = new Thickness(0, 10, 0, 0) };
        var btn = new Button
        {
            Content = _loc["StickyNotePage.SetHotkey"],
            Cursor = Cursors.Hand,
            Padding = new Thickness(10, 4, 10, 4),
            FontSize = 11,
            Background = (Brush)FindResource("Brush.Bg.Input"),
            Foreground = (Brush)FindResource("Brush.Text.Secondary"),
            BorderBrush = (Brush)FindResource("Brush.Border.Subtle"),
            BorderThickness = new Thickness(1),
            VerticalAlignment = VerticalAlignment.Center,
        };
        btn.Click += (_, _) => onOpen(btn);
        DockPanel.SetDock(btn, Dock.Right);
        dock.Children.Add(btn);
        var label = new TextBlock
        {
            Text = _loc["PropertyPanel.Status.Hotkey"] + ":",
            Foreground = (Brush)FindResource("Brush.Text.Secondary"),
            FontSize = 11,
            VerticalAlignment = VerticalAlignment.Center,
            Margin = new Thickness(0, 0, 8, 0),
        };
        DockPanel.SetDock(label, Dock.Left);
        dock.Children.Add(label);
        var current = new TextBlock
        {
            Text = getLabel(),
            FontSize = 11,
            FontFamily = new FontFamily("Cascadia Code, Consolas, monospace"),
            Foreground = (Brush)FindResource("Brush.Text.Primary"),
            VerticalAlignment = VerticalAlignment.Center,
        };
        dock.Children.Add(current);
        _statusHotkeyText = current;
        _statusHotkeyGetter = getLabel;
        return dock;
    }

    // ── 磁贴模式门控 ──
    //
    // 磁贴模式开启 = 标题栏消失 → 标题栏分类整组灰显 0.4 并不可交互。
    // 各字段构建器把标题栏 section 注册进 _tileGated；磁贴开关切换时实时应用。

    readonly List<FrameworkElement> _tileGated = new();

    bool CurrentTileMode() => Target switch
    {
        Zone z => z.TileMode,
        MergedGroupTarget g => g.Master.MergedGroupStyle.TileMode,
        DesktopClock c => c.TileMode,
        DesktopCalendar cal => cal.TileMode,
        StickyNote n => n.TileMode,
        _ => false,
    };

    void ApplyTileGating(bool tileMode)
    {
        foreach (var el in _tileGated)
        {
            el.IsEnabled = !tileMode;
            el.Opacity = tileMode ? 0.4 : 1.0;
            el.ToolTip = tileMode ? _loc["PropertyPanel.Status.TileDisabledHint"] : null;
        }
    }

    // ── 自定义图标实时门控 ──
    CheckBox? _customIconCb;
    int? _lastZoneItemCount;

    /// <summary>按 TileMode + 图标数更新「自定义图标」勾选行的可用状态（灰显 0.4 + Tooltip）。</summary>
    void ApplyCustomIconGating(Zone z)
    {
        if (_customIconCb == null) return;
        bool allowed = z.TileMode && z.Items.Count <= 1;
        _customIconCb.IsEnabled = allowed;
        _customIconCb.Opacity = allowed ? 1.0 : 0.4;
        _customIconCb.ToolTip = allowed
            ? _loc["ZoneProp.CustomIconHint"]
            : _loc["ZoneProp.CustomIconDisabledHint"];
    }

    void BuildZoneFields(Zone z)
    {
        _tileGated.Clear();
        var root = new StackPanel { Margin = new Thickness(16, 12, 16, 12) };

        // 开关区
        var switches = MakeSection(_loc["ZoneProp.Section.Switches"]);
        // 磁贴模式开关：开启时若 HideAppName 仍为默认值 false，自动勾选；取消时默认同步关掉。
        var tileModeCb = MakeCheckRow(_loc["ZoneProp.TileMode"], z.TileMode,
            v =>
            {
                z.TileMode = v;
                if (v && !z.HideAppName) z.HideAppName = true;
                else if (!v && z.HideAppName) z.HideAppName = false;
                Rebuild(z);
                Save(z);
            });
        switches.Children.Add(MakeRowWithHint(tileModeCb, _loc["ZoneProp.TileModeDragHint"]));
        // 隐藏应用名 — 始终可用；磁贴模式下默认勾选（首次切换时自动开启）。
        var hideNameCb = MakeCheckRow(_loc["ZoneProp.HideAppName"], z.HideAppName,
            v => { z.HideAppName = v; Save(z); });
        switches.Children.Add(hideNameCb);
        // 自定义图标 — TileMode=false 或 Items.Count>1 时锁定（灰显 0.4 + 禁用）。
        // 行内右侧小字说明启用条件（Brush.Text.Tertiary 主题自适应，随字段树重建刷新）。
        var customIconCb = MakeCheckRow(_loc["ZoneProp.CustomIcon"], z.CustomIcon,
            v => { z.CustomIcon = v; Save(z); });
        switches.Children.Add(MakeRowWithHint(customIconCb, _loc["ZoneProp.CustomIconInlineHint"]));
        // 保存引用 + 记录图标数，供 ZonesChanged 实时同步门控（增删图标时联动）。
        _customIconCb = customIconCb;
        _lastZoneItemCount = z.Items.Count;
        ApplyCustomIconGating(z);
        var hoverRow = MakeCheckRowWithSideBtn(_loc["ZoneProp.RestoreButton"], z.EnableRestoreButton,
            v => { z.EnableRestoreButton = v; Save(z); },
            _loc["Motion.SettingsEllipsis"], _ => OpenMotionDialog(z, () => BuildZoneFields(z)));
        switches.Children.Add(hoverRow);
        // ponytail: hover-to-expand sub-toggle. Hidden when EnableRestoreButton
        // is off would be a bigger UX change; we just leave it always editable.
        // When EnableRestoreButton=false, EnableRestoreButton's own gate hides
        // the RestoreButton so the toggle has no observable effect — the user
        // can flip it ahead of time without anything bad happening.
        switches.Children.Add(MakeCheckRow(_loc["Motion.HoverAutoExpand"], z.HoverAutoExpand,
            v => { z.HoverAutoExpand = v; Save(z); }));
        switches.Children.Add(MakeCornerStyleRow(z.CornerRadius > 0, rounded =>
        {
            z.CornerRadius = rounded ? (z.CornerRadius > 0 ? z.CornerRadius : 8) : 0;
            Save(z);
        }));
        root.Children.Add(switches);

        // 基本
        var basic = MakeSection(_loc["ZoneProp.Section.Basic"]);
        basic.Children.Add(MakeTextRow(_loc["ZoneProp.Name"], z.Name,
            v => { z.Name = v ?? ""; Save(z); }));
        basic.Children.Add(MakeColorRow(_loc["ZoneProp.NameColor"], z.TitleTextColor,
            v => { z.TitleTextColor = v; Save(z); }));
        basic.Children.Add(MakeIconRow(_loc["ZoneProp.Icon"], z.IconChar,
            v => { z.IconChar = v; Save(z); }, Helpers.IconGlyph.Zones));
        basic.Children.Add(MakeColorRow(_loc["ZoneProp.IconColor"],
            string.IsNullOrEmpty(z.IconColor) ? "#FFFFFF" : z.IconColor,
            v => { z.IconColor = v; Save(z); }));

        var sizeGrid = new Grid { Margin = new Thickness(0, 6, 0, 0) };
        sizeGrid.ColumnDefinitions.Add(new ColumnDefinition { Width = new GridLength(1, GridUnitType.Star) });
        sizeGrid.ColumnDefinitions.Add(new ColumnDefinition { Width = new GridLength(10) });
        sizeGrid.ColumnDefinitions.Add(new ColumnDefinition { Width = new GridLength(1, GridUnitType.Star) });
        var widthGrid = MakeNumberSubBlock(_loc["ZoneProp.Width"], z.Width, v => { z.Width = v; Save(z); });
        Grid.SetColumn(widthGrid, 0);
        sizeGrid.Children.Add(widthGrid);
        var heightGrid = MakeNumberSubBlock(_loc["ZoneProp.Height"], z.Height, v => { z.Height = v; Save(z); });
        Grid.SetColumn(heightGrid, 2);
        sizeGrid.Children.Add(heightGrid);
        var widthBox = (TextBox)widthGrid.Children[1];
        var heightBox = (TextBox)heightGrid.Children[1];
        basic.Children.Add(sizeGrid);

        var gridGrid = new Grid { Margin = new Thickness(0, 6, 0, 0) };
        gridGrid.ColumnDefinitions.Add(new ColumnDefinition { Width = new GridLength(1, GridUnitType.Star) });
        gridGrid.ColumnDefinitions.Add(new ColumnDefinition { Width = new GridLength(10) });
        gridGrid.ColumnDefinitions.Add(new ColumnDefinition { Width = new GridLength(1, GridUnitType.Star) });
        var gridBlock = MakeNumberSubBlock(_loc["ZoneProp.GridSize"], z.GridSize,
            v => { z.GridSize = (int)v; Save(z); }, asInt: true, hint: _loc["ZoneProp.GridSizeHint"]);
        Grid.SetColumn(gridBlock, 0);
        gridGrid.Children.Add(gridBlock);
        var snapStack = new StackPanel { VerticalAlignment = VerticalAlignment.Bottom, Margin = new Thickness(0, 18, 0, 4) };
        snapStack.Children.Add(MakeCheckRow(_loc["ZoneProp.SnapToGrid"], z.SnapToGrid,
            v => { z.SnapToGrid = v; Save(z); }));
        snapStack.Children.Add(MakeCheckRow(_loc["ZoneProp.AutoArrange"], z.AutoArrange,
            v => { z.AutoArrange = v; Save(z); }));
        Grid.SetColumn(snapStack, 2);
        gridGrid.Children.Add(snapStack);
        basic.Children.Add(gridGrid);

        root.Children.Add(basic);

        // 标题栏
        var tb = MakeSection(_loc["ZoneProp.Section.TitleBar"]);
        bool isMergedMaster = z.MergedGroupMembership.SubZoneIds.Count > 0;
        tb.Children.Add(MakeCheckRow(_loc["ZoneProp.TitleBarFillIndependent"],
            isMergedMaster ? z.MergedGroupStyle.TitleBarFillIndependent : z.TitleBarFillIndependent,
            v =>
            {
                if (isMergedMaster)
                {
                    z.MergedGroupStyle.TitleBarFillIndependent = v;
                    z.TitleBarFillIndependent = v;
                }
                else
                {
                    z.TitleBarFillIndependent = v;
                }
                Save(z);
            }));
        tb.Children.Add(MakeColorRow(_loc["ZoneProp.TitleBarColor"], z.TitleBarFillColor,
            v => { z.TitleBarFillColor = v; Save(z); }));
        tb.Children.Add(MakeSliderRow(_loc["ZoneProp.TitleBarOpacity"], 0, 100, 5,
            ParsePercent(z.TitleBarFillColor, 6),
            p => { z.TitleBarFillColor = SetPercent(z.TitleBarFillColor, p, "FFFFFF"); Save(z); }));
        tb.Children.Add(MakeColorRow(_loc["ZoneProp.ButtonColor"], z.ButtonColor,
            v => { z.ButtonColor = v; Save(z); }));
        tb.Children.Add(MakeSliderRow(_loc["ZoneProp.ButtonOpacity"], 5, 100, 5,
            z.ControlOpacity,
            v => { z.ControlOpacity = v; Save(z); }));
        root.Children.Add(tb);
        _tileGated.Add(tb); // 磁贴模式 = 无标题栏 → 整组禁用

        // 边框与填充
        var bf = MakeSection(_loc["ZoneProp.Section.BorderFill"]);
        bf.Children.Add(MakeTextRow(_loc["ZoneProp.BorderThickness"], z.BorderThickness.ToString("0.0", CultureInfo.InvariantCulture),
            v => { if (double.TryParse(v, NumberStyles.Float, CultureInfo.InvariantCulture, out var d)) { z.BorderThickness = d; Save(z); } }));
        bf.Children.Add(MakeColorRow(_loc["ZoneProp.BorderColor"], z.BorderColor,
            v => { z.BorderColor = v; Save(z); }));
        bf.Children.Add(MakeSliderRow(_loc["ZoneProp.BorderOpacity"], 0, 100, 5,
            ParsePercent(z.BorderColor, 25),
            p => { z.BorderColor = SetPercent(z.BorderColor, p, "FFFFFF"); Save(z); }));
        bf.Children.Add(MakeColorRow(_loc["ZoneProp.FillColor"], z.FillColor,
            v => { z.FillColor = v; Save(z); }));
        bf.Children.Add(MakeSliderRow(_loc["ZoneProp.FillOpacity"], 0, 100, 5,
            ParsePercent(z.FillColor, 8),
            p => { z.FillColor = SetPercent(z.FillColor, p, "000000"); Save(z); }));
        root.Children.Add(bf);

        // 液态玻璃
        var lg = MakeSection(_loc["ZoneProp.Section.LiquidGlass"]);
        var lgRow = MakeCheckRowWithSideBtn(_loc["ZoneProp.LiquidGlass"], z.EnableLiquidGlass,
            v => { z.EnableLiquidGlass = v; Save(z); },
            _loc["ZoneProp.LiquidGlassSettingsEllipsis"], _ => OpenLiquidGlassDialog(z));
        lg.Children.Add(lgRow);
        root.Children.Add(lg);

        // 背景图片
        var bg = MakeSection(_loc["ZoneProp.Section.BgImage"]);
        bg.Children.Add(MakeBgImageRow("", new BgImageBinding
        {
            GetPath = () => z.BackgroundImagePath,
            SetPath = v => z.BackgroundImagePath = v ?? "",
            GetOpacity = () => z.BackgroundImageOpacity,
            SetOpacity = v => z.BackgroundImageOpacity = v,
            GetZoom = () => z.BgImageZoom,
            SetZoom = v => z.BgImageZoom = v,
            GetOffsetX = () => z.BgImageOffsetX,
            SetOffsetX = v => z.BgImageOffsetX = v,
            GetOffsetY = () => z.BgImageOffsetY,
            SetOffsetY = v => z.BgImageOffsetY = v,
            GetWidth = () => z.Width, GetHeight = () => z.Height,
            CropShape = "Rectangle",
            // 文件夹映射头部行(26px)也算进标题栏：分界线 + 内部 24px 分界。
            TitleBarHeight = z.TileMode ? 0 : 24 + (z.FolderMappingEnabled ? 26 : 0),
            TitleBarInnerDividerHeights = !z.TileMode && z.FolderMappingEnabled ? new[] { 24.0 } : Array.Empty<double>(),
            OnSave = () => Save(z),
            SetZoneSize = (w, h) =>
            {
                z.Width = w; z.Height = h; Save(z);
                widthBox.Text = w.ToString("0.##", CultureInfo.InvariantCulture);
                heightBox.Text = h.ToString("0.##", CultureInfo.InvariantCulture);
            },
        }));
        root.Children.Add(bg);

        // 主体内容 — 替代原「主体内容颜色自适应」的固定色 + 透明度。
        root.Children.Add(BuildBodyContentSection(
            () => z.TextColor, v => z.TextColor = v, () => Save(z)));

        // 文件夹映射 — 样式设置界面的最后一项。
        var fm = MakeSection(_loc["ZoneProp.Section.FolderMapping"]);
        AddFolderMappingSection(fm,
            () => z.FolderMappingEnabled, v => z.FolderMappingEnabled = v,
            () => z.FolderMappingPath, v => z.FolderMappingPath = v ?? "",
            () => { CaptureFolderMappingState(); Save(z); });
        root.Children.Add(fm);

        // 自动整理 — 样式设置界面最后一项（MergedGroup 不支持）。
        root.Children.Add(BuildAutoOrganizeSection(z));

        FieldScroller.Content = root;
        ApplyTileGating(z.TileMode);
    }

    // ── Auto-organize section（仅 Zone 类型）──

    /// <summary>自动整理 section：扩展名 / 文件名要素两行独立开关 + 监听路径 + 手动扫描。</summary>
    FrameworkElement BuildAutoOrganizeSection(Zone z)
    {
        var sec = MakeSection(_loc["ZoneProp.Section.AutoOrganize"]);

        // 外层总开关：勾选 = 启用监听，取消 = 暂停监听但保留规则，方便下次快速开启
        sec.Children.Add(MakeCheckRow(
            _loc["ZoneProp.AutoOrganize.WatchingEnabled"],
            z.AutoOrganizeWatching,
            v =>
            {
                z.AutoOrganizeWatching = v;
                AutoOrganizeService.Instance.AttachZone(z);
                Save(z);
            }));

        // 扩展名子开关：取消 = 保留扩展名列表但不参与匹配
        sec.Children.Add(MakeCheckRow(
            _loc["ZoneProp.AutoOrganize.ExtensionEnabled"],
            z.AutoOrganizeExtEnabled,
            v =>
            {
                z.AutoOrganizeExtEnabled = v;
                AutoOrganizeService.Instance.AttachZone(z);
                Save(z);
            }));

        sec.Children.Add(MakeAutoOrganizeExtChipRow(z));

        // 文件名要素子开关：取消 = 保留要素列表但不参与匹配
        sec.Children.Add(MakeCheckRow(
            _loc["ZoneProp.AutoOrganize.NameEnabled"],
            z.AutoOrganizeNameEnabled,
            v =>
            {
                z.AutoOrganizeNameEnabled = v;
                AutoOrganizeService.Instance.AttachZone(z);
                Save(z);
            }));

        sec.Children.Add(MakeNameTokenChipRow(z));
        sec.Children.Add(MakeAutoOrganizePathRow(z));
        sec.Children.Add(MakeScanExistingButton(z));

        // 底部说明：解释「启用监听」后的自动加入行为（Brush.Text.Tertiary 主题自适应小字）。
        sec.Children.Add(new TextBlock
        {
            Text = _loc["ZoneProp.AutoOrganize.WatchHint"],
            FontSize = 10,
            Foreground = (Brush)FindResource("Brush.Text.Tertiary"),
            TextWrapping = TextWrapping.Wrap,
            Margin = new Thickness(0, 8, 0, 0),
        });

        return sec;
    }

    FrameworkElement MakeAutoOrganizeExtChipRow(Zone z)
    {
        var dock = new DockPanel { Margin = new Thickness(0, 4, 0, 4), LastChildFill = true };

        var add = MakeAddButton(
            _loc["ZoneProp.AutoOrganize.Picker.AddCustom"],
            () => OpenPicker(z, AutoOrganizePickerKind.Extension));
        DockPanel.SetDock(add, Dock.Right);
        dock.Children.Add(add);

        var label = new TextBlock
        {
            Text = _loc["ZoneProp.AutoOrganize.ExtensionLabel"] + ":",
            Foreground = (Brush)FindResource("Brush.Text.Secondary"),
            FontSize = 11,
            VerticalAlignment = VerticalAlignment.Center,
            Margin = new Thickness(0, 0, 8, 0),
        };
        DockPanel.SetDock(label, Dock.Left);
        dock.Children.Add(label);

        var preview = new TextBlock
        {
            Text = z.AutoOrganizeExtensions.Count == 0
                ? "—"
                : string.Join("  ", z.AutoOrganizeExtensions.Take(8).Select(e => $"[{e}]")),
            Foreground = (Brush)FindResource("Brush.Text.Primary"),
            FontSize = 11,
            TextTrimming = TextTrimming.CharacterEllipsis,
            VerticalAlignment = VerticalAlignment.Center,
        };
        dock.Children.Add(preview);
        return dock;
    }

    void OpenPicker(Zone z, AutoOrganizePickerKind kind)
    {
        var owner = CachedOwner ?? Window.GetWindow(this);
        var w = new AutoOrganizePickerWindow(z, kind) { Owner = owner };
        if (w.ShowDialog() == true)
        {
            // 添加了规则后自动勾选对应子开关 + 总开关
            if (kind == AutoOrganizePickerKind.Extension)
            {
                if (z.AutoOrganizeExtensions.Count > 0 && !z.AutoOrganizeExtEnabled)
                    z.AutoOrganizeExtEnabled = true;
            }
            else
            {
                if (z.AutoOrganizeNameTokens.Count > 0 && !z.AutoOrganizeNameEnabled)
                    z.AutoOrganizeNameEnabled = true;
            }
            if (z.AutoOrganizeEnabled && !z.AutoOrganizeWatching)
                z.AutoOrganizeWatching = true;
            AutoOrganizeService.Instance.AttachZone(z);
            Rebuild(z);
            Save(z);
        }
    }

    FrameworkElement MakeNameTokenChipRow(Zone z)
    {
        var dock = new DockPanel { Margin = new Thickness(0, 4, 0, 4), LastChildFill = true };

        var add = MakeAddButton(
            _loc["ZoneProp.AutoOrganize.Picker.AddToken"],
            () => OpenPicker(z, AutoOrganizePickerKind.Token));
        DockPanel.SetDock(add, Dock.Right);
        dock.Children.Add(add);

        var label = new TextBlock
        {
            Text = _loc["ZoneProp.AutoOrganize.NameLabel"] + ":",
            Foreground = (Brush)FindResource("Brush.Text.Secondary"),
            FontSize = 11,
            VerticalAlignment = VerticalAlignment.Center,
            Margin = new Thickness(0, 0, 8, 0),
        };
        DockPanel.SetDock(label, Dock.Left);
        dock.Children.Add(label);

        var preview = new TextBlock
        {
            Text = z.AutoOrganizeNameTokens.Count == 0
                ? "—"
                : string.Join("  ", z.AutoOrganizeNameTokens.Take(8).Select(t => $"[{t}]")),
            Foreground = (Brush)FindResource("Brush.Text.Primary"),
            FontSize = 11,
            TextTrimming = TextTrimming.CharacterEllipsis,
            VerticalAlignment = VerticalAlignment.Center,
        };
        dock.Children.Add(preview);
        return dock;
    }

    FrameworkElement MakeAutoOrganizePathRow(Zone z)
    {
        var dock = new DockPanel { Margin = new Thickness(0, 4, 0, 4), LastChildFill = true };

        var browse = MakePanelButton(
            _loc["ZoneProp.AutoOrganize.ChoosePath"],
            (_, _) => PickAutoOrganizePath(z),
            minWidth: 80);
        browse.Margin = new Thickness(8, 0, 0, 0);
        DockPanel.SetDock(browse, Dock.Right);
        dock.Children.Add(browse);

        var label = new TextBlock
        {
            Text = _loc["ZoneProp.AutoOrganize.WatchPath"] + ":",
            Foreground = (Brush)FindResource("Brush.Text.Secondary"),
            FontSize = 11,
            VerticalAlignment = VerticalAlignment.Center,
            Margin = new Thickness(0, 0, 8, 0),
        };
        DockPanel.SetDock(label, Dock.Left);
        dock.Children.Add(label);

        var pathBox = new TextBox
        {
            Text = z.AutoOrganizeWatchPath,
            FontSize = 11,
            Padding = new Thickness(4, 2, 4, 2),
            VerticalAlignment = VerticalAlignment.Center,
            ToolTip = _loc["ZoneProp.AutoOrganize.WatchPathPlaceholder"],
        };
        pathBox.LostFocus += (_, _) =>
        {
            var raw = (pathBox.Text ?? "").Trim();
            if (raw == z.AutoOrganizeWatchPath) return;
            z.AutoOrganizeWatchPath = raw;
            // 手动输入路径且已有规则时，自动勾选监听开关
            if (z.AutoOrganizeEnabled && !z.AutoOrganizeWatching)
                z.AutoOrganizeWatching = true;
            AutoOrganizeService.Instance.AttachZone(z);
            Save(z);
        };
        pathBox.KeyDown += (_, e) => { if (e.Key == Key.Enter) Keyboard.ClearFocus(); };
        dock.Children.Add(pathBox);
        return dock;
    }

    void PickAutoOrganizePath(Zone z)
    {
        var owner = CachedOwner ?? Window.GetWindow(this);
        var dlg = new Microsoft.Win32.OpenFolderDialog
        {
            Title = _loc["ZoneProp.AutoOrganize.ChoosePath"],
            Multiselect = false,
            InitialDirectory = string.IsNullOrEmpty(z.AutoOrganizeWatchPath)
                ? Environment.GetFolderPath(Environment.SpecialFolder.DesktopDirectory)
                : z.AutoOrganizeWatchPath,
        };
        bool? ok;
        try { ok = owner != null ? dlg.ShowDialog(owner) : dlg.ShowDialog(); }
        catch { ok = null; }
        if (ok == true && !string.IsNullOrEmpty(dlg.FolderName))
        {
            z.AutoOrganizeWatchPath = dlg.FolderName;
            // 选好路径且已有规则时，自动勾选监听开关
            if (z.AutoOrganizeEnabled && !z.AutoOrganizeWatching)
                z.AutoOrganizeWatching = true;
            AutoOrganizeService.Instance.AttachZone(z);
            Rebuild(z);
            Save(z);
        }
    }

    FrameworkElement MakeScanExistingButton(Zone z)
    {
        bool enabled = !string.IsNullOrWhiteSpace(z.AutoOrganizeWatchPath)
            && (z.AutoOrganizeExtensions.Count > 0 || z.AutoOrganizeNameTokens.Count > 0);
        var btn = MakePanelButton(_loc["ZoneProp.AutoOrganize.ScanExisting"], null, minWidth: 120);
        btn.Margin = new Thickness(0, 8, 0, 0);
        btn.HorizontalAlignment = HorizontalAlignment.Left;
        btn.IsEnabled = enabled;
        btn.Click += async (_, _) =>
        {
            btn.IsEnabled = false;
            int n = await AutoOrganizeService.Instance.ScanExistingAsync(z);
            btn.IsEnabled = true;
            var msg = string.Format(System.Globalization.CultureInfo.InvariantCulture,
                _loc["ZoneProp.AutoOrganize.ScanDone"], n);
            if (App.Notify != null)
                App.Notify(_loc["ZoneProp.Section.AutoOrganize"], msg);
            else
                MessageBox.Show(msg, "Auto-organize", MessageBoxButton.OK, MessageBoxImage.Information);
            Rebuild(z);
        };
        return btn;
    }

    Button MakePanelButton(string content, RoutedEventHandler? onClick, double? minWidth = null)
    {
        var btn = new Button
        {
            Content = content,
            Background = (Brush)FindResource("Brush.Bg.Input"),
            Foreground = (Brush)FindResource("Brush.Text.Secondary"),
            BorderBrush = (Brush)FindResource("Brush.Border.Subtle"),
            BorderThickness = new Thickness(1),
            Cursor = Cursors.Hand,
            FontSize = 11,
            Padding = new Thickness(10, 4, 10, 4),
        };
        if (minWidth.HasValue) btn.MinWidth = minWidth.Value;
        if (onClick != null) btn.Click += onClick;
        return btn;
    }

    Button MakeAddButton(string tooltip, Action onClick)
    {
        var btn = new Button
        {
            Content = "+",
            Width = 28,
            Height = 28,
            FontSize = 16,
            Padding = new Thickness(0),
            Background = (Brush)FindResource("Brush.Bg.Input"),
            Foreground = (Brush)FindResource("Brush.Text.Secondary"),
            BorderBrush = (Brush)FindResource("Brush.Border.Subtle"),
            BorderThickness = new Thickness(1),
            Cursor = Cursors.Hand,
            ToolTip = tooltip,
            VerticalAlignment = VerticalAlignment.Center,
        };
        btn.Click += (_, _) => onClick();
        return btn;
    }

    // ── Field tree for merged groups ──
    //
    // ponytail 2026-08-26: 组合分区字段分类.txt spec. Same section structure and
    // builders as the Zone editor. Group-level style reads/writes the master's
    // MergedGroupStyle + MergedGroupMembership; window-level behavior (motion,
    // liquid glass, size, grid) reads/writes the master zone itself.
    // 统一填充/保留原有填充 is the sliding-highlight Segmented pill; 保留原有填充
    // keeps the BODY fill original — the title bar (both layers), border and corners
    // stay unified from MergedGroupStyle — while 填充 / 液态玻璃 / 背景图片 all fade
    // out + disable when 保留原有填充 is selected.

    readonly List<FrameworkElement> _unifiedGated = new();
    // ponytail 2026-08-26: SubFolder 专用填充门控 — FillFollowsZone=true 时
    // FillColorOverride / FillOpacityOverride 行灰显。镜像 _unifiedGated 形状,
    // 共享同一组动效/灰显参数。
    readonly List<FrameworkElement> _fillGated = new();

    void BuildMergedGroupFields(Zone z)
    {
        var gs = z.MergedGroupStyle;
        var gm = z.MergedGroupMembership;
        void SaveGroup() => Save(Target!);

        _unifiedGated.Clear();
        _tileGated.Clear();
        CheckBox? titleBarIndependentCb = null;
        var root = new StackPanel { Margin = new Thickness(16, 12, 16, 12) };

        // 开关区
        var switches = MakeSection(_loc["ZoneProp.Section.Switches"]);
        switches.Children.Add(MakeCheckRow(_loc["ZoneProp.TileMode"], gs.TileMode,
            v =>
            {
                gs.TileMode = v;
                z.TileMode = v;
                SaveGroup();
                ApplyTileGating(v); // 磁贴模式 = 无标题栏 → 标题栏分类实时禁用
            }));
        switches.Children.Add(MakeCheckRow(_loc["ZoneProp.RestoreButton"], z.EnableRestoreButton,
            v => { z.EnableRestoreButton = v; SaveGroup(); }));
        switches.Children.Add(MakeCheckRowWithSideBtn(_loc["Motion.HoverAutoExpand"], z.HoverAutoExpand,
            v => { z.HoverAutoExpand = v; SaveGroup(); },
            _loc["Motion.SettingsEllipsis"], _ => OpenMotionDialog(z, () => BuildMergedGroupFields(z))));
        switches.Children.Add(MakeRowWithHint(
            MakeUnifiedFillRow(gs.UseUnifiedFill, v =>
            {
                gs.UseUnifiedFill = v;
                // 保留原有填充 → 自动开启「标题栏填充单独设置」：两层标题栏的填充
                // 颜色独立于各子分区自己的主体填充，避免统一标题栏盖在保留的原填充上。
                if (!v)
                {
                    gs.TitleBarFillIndependent = true;
                    z.TitleBarFillIndependent = true;
                    if (titleBarIndependentCb != null)
                        titleBarIndependentCb.IsChecked = true; // 触发 onChange 幂等写入并 SaveGroup
                }
                SaveGroup();
                SetUnifiedGating(v, animate: true);
            }),
            _loc["MergedGroupProp.KeepOriginalFillHint"]));
        var cornerRow = MakeCornerStyleRow(gs.CornerRadius > 0, rounded =>
        {
            gs.CornerRadius = rounded ? (gs.CornerRadius > 0 ? gs.CornerRadius : 8) : 0;
            z.CornerRadius = gs.CornerRadius;
            SaveGroup();
        });
        switches.Children.Add(cornerRow);
        root.Children.Add(switches);

        // 基本
        var basic = MakeSection(_loc["ZoneProp.Section.Basic"]);
        basic.Children.Add(MakeTextRow(_loc["MergedGroupProp.Name"], gm.DisplayName,
            v => { gm.DisplayName = v ?? ""; SaveGroup(); }));
        basic.Children.Add(MakeColorRow(_loc["MergedGroupProp.NameColor"], gs.TitleTextColor,
            v => { gs.TitleTextColor = v; SaveGroup(); }));
        basic.Children.Add(MakeIconRow(_loc["MergedGroupProp.Icon"], gm.Icon,
            v => { gm.Icon = v; SaveGroup(); }, Helpers.IconGlyph.Merged));
        basic.Children.Add(MakeColorRow(_loc["MergedGroupProp.IconColor"],
            string.IsNullOrEmpty(gs.IconColor) ? "#FFFFFF" : gs.IconColor,
            v => { gs.IconColor = v; SaveGroup(); }));

        var sizeGrid = new Grid { Margin = new Thickness(0, 6, 0, 0) };
        sizeGrid.ColumnDefinitions.Add(new ColumnDefinition { Width = new GridLength(1, GridUnitType.Star) });
        sizeGrid.ColumnDefinitions.Add(new ColumnDefinition { Width = new GridLength(10) });
        sizeGrid.ColumnDefinitions.Add(new ColumnDefinition { Width = new GridLength(1, GridUnitType.Star) });
        var widthGrid = MakeNumberSubBlock(_loc["ZoneProp.Width"], z.Width, v => { z.Width = v; SaveGroup(); });
        Grid.SetColumn(widthGrid, 0);
        sizeGrid.Children.Add(widthGrid);
        var heightGrid = MakeNumberSubBlock(_loc["ZoneProp.Height"], z.Height, v => { z.Height = v; SaveGroup(); });
        Grid.SetColumn(heightGrid, 2);
        sizeGrid.Children.Add(heightGrid);
        var widthBox = (TextBox)widthGrid.Children[1];
        var heightBox = (TextBox)heightGrid.Children[1];
        basic.Children.Add(sizeGrid);

        var gridGrid = new Grid { Margin = new Thickness(0, 6, 0, 0) };
        gridGrid.ColumnDefinitions.Add(new ColumnDefinition { Width = new GridLength(1, GridUnitType.Star) });
        gridGrid.ColumnDefinitions.Add(new ColumnDefinition { Width = new GridLength(10) });
        gridGrid.ColumnDefinitions.Add(new ColumnDefinition { Width = new GridLength(1, GridUnitType.Star) });
        var gridBlock = MakeNumberSubBlock(_loc["ZoneProp.GridSize"], z.GridSize,
            v => { z.GridSize = (int)v; SaveGroup(); }, asInt: true, hint: _loc["ZoneProp.GridSizeHint"]);
        Grid.SetColumn(gridBlock, 0);
        gridGrid.Children.Add(gridBlock);
        var snapStack = new StackPanel { VerticalAlignment = VerticalAlignment.Bottom, Margin = new Thickness(0, 18, 0, 4) };
        snapStack.Children.Add(MakeCheckRow(_loc["ZoneProp.SnapToGrid"], z.SnapToGrid,
            v => { z.SnapToGrid = v; SaveGroup(); }));
        Grid.SetColumn(snapStack, 2);
        gridGrid.Children.Add(snapStack);
        basic.Children.Add(gridGrid);
        root.Children.Add(basic);

        // 标题栏 — 统一(两种模式都生效，因为保留原有填充只保留主体填充)。
        // 独立标题栏填充同样作用于两层标题栏（最上方 24px + 子分区标签栏 24px）。
        var tb = MakeSection(_loc["ZoneProp.Section.TitleBar"]);
        titleBarIndependentCb = MakeCheckRow(_loc["ZoneProp.TitleBarFillIndependent"],
            gs.TitleBarFillIndependent,
            v => { gs.TitleBarFillIndependent = v; z.TitleBarFillIndependent = v; SaveGroup(); });
        tb.Children.Add(titleBarIndependentCb);
        tb.Children.Add(MakeColorRow(_loc["ZoneProp.TitleBarColor"], gs.TitleBarFillColor,
            v => { gs.TitleBarFillColor = v; SaveGroup(); }));
        tb.Children.Add(MakeSliderRow(_loc["ZoneProp.TitleBarOpacity"], 0, 100, 5,
            ParsePercent(gs.TitleBarFillColor, 6),
            p => { gs.TitleBarFillColor = SetPercent(gs.TitleBarFillColor, p, "FFFFFF"); SaveGroup(); }));
        tb.Children.Add(MakeColorRow(_loc["ZoneProp.ButtonColor"], gs.ButtonColor,
            v => { gs.ButtonColor = v; SaveGroup(); }));
        tb.Children.Add(MakeSliderRow(_loc["ZoneProp.ButtonOpacity"], 5, 100, 5,
            gs.ControlOpacity,
            v => { gs.ControlOpacity = v; SaveGroup(); }));
        root.Children.Add(tb);
        _tileGated.Add(tb); // 磁贴模式 = 无标题栏 → 整组禁用

        // 边框与填充 — 边框统一(两种模式都生效)；只有「填充」行在保留原有填充时
        // 失效(主体填充改回各子分区自己的填充)。
        var bf = MakeSection(_loc["ZoneProp.Section.BorderFill"]);
        bf.Children.Add(MakeTextRow(_loc["ZoneProp.BorderThickness"], gs.BorderThickness.ToString("0.0", CultureInfo.InvariantCulture),
            v => { if (double.TryParse(v, NumberStyles.Float, CultureInfo.InvariantCulture, out var d)) { gs.BorderThickness = d; SaveGroup(); } }));
        bf.Children.Add(MakeColorRow(_loc["ZoneProp.BorderColor"], gs.BorderColor,
            v => { gs.BorderColor = v; SaveGroup(); }));
        bf.Children.Add(MakeSliderRow(_loc["ZoneProp.BorderOpacity"], 0, 100, 5,
            ParsePercent(gs.BorderColor, 25),
            p => { gs.BorderColor = SetPercent(gs.BorderColor, p, "FFFFFF"); SaveGroup(); }));
        var fillColorRow = MakeColorRow(_loc["ZoneProp.FillColor"], gs.FillColor,
            v => { gs.FillColor = v; SaveGroup(); });
        bf.Children.Add(fillColorRow);
        var fillOpacityRow = MakeSliderRow(_loc["ZoneProp.FillOpacity"], 0, 100, 5,
            ParsePercent(gs.FillColor, 8),
            p => { gs.FillColor = SetPercent(gs.FillColor, p, "000000"); SaveGroup(); });
        bf.Children.Add(fillOpacityRow);
        _unifiedGated.Add(fillColorRow);
        _unifiedGated.Add(fillOpacityRow);
        root.Children.Add(bf);

        // 液态玻璃 — window-level, edits the master's own glass (per-window effect).
        // 保留原有填充时禁用：玻璃属于统一填充表现的一部分。
        var lg = MakeSection(_loc["ZoneProp.Section.LiquidGlass"]);
        var lgRow = MakeCheckRowWithSideBtn(_loc["ZoneProp.LiquidGlass"], z.EnableLiquidGlass,
            v => { z.EnableLiquidGlass = v; SaveGroup(); },
            _loc["ZoneProp.LiquidGlassSettingsEllipsis"], _ => OpenLiquidGlassDialog(z));
        lg.Children.Add(lgRow);
        _unifiedGated.Add(lgRow);
        root.Children.Add(lg);

        // 背景图片 — group-level BgImage (统一填充专属；保留原有填充时禁用整行)。
        var bg = MakeSection(_loc["ZoneProp.Section.BgImage"]);
        var bgRow = MakeBgImageRow("", new BgImageBinding
        {
            GetPath = () => gs.BackgroundImagePath,
            SetPath = v => gs.BackgroundImagePath = v ?? "",
            GetOpacity = () => gs.BackgroundImageOpacity,
            SetOpacity = v => gs.BackgroundImageOpacity = v,
            GetZoom = () => gs.BgImageZoom,
            SetZoom = v => gs.BgImageZoom = v,
            GetOffsetX = () => gs.BgImageOffsetX,
            SetOffsetX = v => gs.BgImageOffsetX = v,
            GetOffsetY = () => gs.BgImageOffsetY,
            SetOffsetY = v => gs.BgImageOffsetY = v,
            GetWidth = () => z.Width, GetHeight = () => z.Height,
            CropShape = "Rectangle",
            // 组合分区两层标题栏(24+24) + 文件夹映射头部行(26px)：内部分界线 24/48。
            TitleBarHeight = gs.TileMode ? 0 : 48 + (gs.FolderMappingEnabled ? 26 : 0),
            TitleBarInnerDividerHeights = gs.TileMode
                ? Array.Empty<double>()
                : gs.FolderMappingEnabled ? new[] { 24.0, 48.0 } : new[] { 24.0 },
            OnSave = SaveGroup,
            SetZoneSize = (w, h) =>
            {
                z.Width = w; z.Height = h; SaveGroup();
                widthBox.Text = w.ToString("0.##", CultureInfo.InvariantCulture);
                heightBox.Text = h.ToString("0.##", CultureInfo.InvariantCulture);
            },
        });
        bg.Children.Add(bgRow);
        _unifiedGated.Add(bgRow);
        root.Children.Add(bg);

        // 主体内容 — 保留原有填充时禁用（主体填充回到各子分区自己的填充，统一内容色无意义）。
        root.Children.Add(BuildBodyContentSection(
            () => gs.TextColor, v => { gs.TextColor = v; SaveGroup(); }, SaveGroup, _unifiedGated));

        // 组合分区编辑器不再提供文件夹映射（用户 2026-08-2x：功能与选项一并移除）。
        // 组级映射仍保留在分区窗口层（MergedGroupStyle 字段不动，窗口头部行照常工作）。

        FieldScroller.Content = root;
        SetUnifiedGating(gs.UseUnifiedFill, animate: false);
        ApplyTileGating(gs.TileMode);
    }

    // ── Field tree for SubFolder zone items ──
    //
    // ponytail 2026-08-26: Task 7 — SubFolder 专属字段编辑器。镜像 BuildZoneFields
    // 形状但只暴露 SubFolder 专属 14 字段 + Name,分 5 组:
    //   A 基础:Name / IconSizeAutoGrow / CornerRounded
    //   B 动效:HoverAutoExpand 开关 + 右侧"…"按钮打开二级窗口 MotionSettingsDialog
    //          (动画类型/速度在二级窗口里,同分区)
    //   C 布局:GridSize / SnapToGrid / AutoArrange
    //   D 外观:FillFollowsZone (级联门控) + FillColorOverride + FillOpacityOverride
    //          + EnableLiquidGlass + BackgroundImagePath
    //   预设:入口统一走底部按钮栏的"加载预设/保存预设"(预设卡在 LoadPresetDialog
    //         二级界面里,参考分区卡片含名称/日期信息栏)。
    // 预览走 Save(sub) → Preview?.Invoke(sub);Apply 时才 Commit → Persist 落盘,
    // host dispatcher 已有 ZoneItem 分支(_zoneManager.UpdateZone(parent))。

    void BuildSubfolderFields(ZoneItem sub)
    {
        _fillGated.Clear();
        _tileGated.Clear(); // SubFolder 无磁贴模式 — 清掉上一目标的标题栏引用
        var root = new StackPanel { Margin = new Thickness(16, 12, 16, 12) };

        // A: 基础
        var basic = MakeSection(_loc["SubfolderProp.Section.Basic"]);
        basic.Children.Add(MakeTextRow(_loc["SubfolderProp.Name"], sub.Name,
            v => { sub.Name = v ?? ""; Save(sub); }));
        // ponytail: 图标锁死 1×1(用户取消尺寸自适应),不再暴露 IconSizeAutoGrow 开关。
        basic.Children.Add(MakeCornerStyleRow(sub.CornerRounded, rounded =>
        {
            sub.CornerRounded = rounded;
            Save(sub);
        }));
        root.Children.Add(basic);

        // B: 动效 — 参考分区的做法:动效类型/速度收敛到二级窗口 MotionSettingsDialog,
        // 行内只留"鼠标悬停自动展开"开关 + 右侧"…"按钮打开二级窗口。
        var motion = MakeSection(_loc["SubfolderProp.Section.Motion"]);
        motion.Children.Add(MakeCheckRowWithSideBtn(_loc["Motion.HoverAutoExpand"], sub.HoverAutoExpand,
            v => { sub.HoverAutoExpand = v; Save(sub); },
            _loc["Motion.SettingsEllipsis"], _ => OpenSubfolderMotionDialog(sub)));
        root.Children.Add(motion);

        // C: 布局 — 镜像 BuildZoneFields 的 GridSize + SnapToGrid/AutoArrange 二列布局。
        var layout = MakeSection(_loc["SubfolderProp.Section.Layout"]);
        var layoutGrid = new Grid { Margin = new Thickness(0, 6, 0, 0) };
        layoutGrid.ColumnDefinitions.Add(new ColumnDefinition { Width = new GridLength(1, GridUnitType.Star) });
        layoutGrid.ColumnDefinitions.Add(new ColumnDefinition { Width = new GridLength(10) });
        layoutGrid.ColumnDefinitions.Add(new ColumnDefinition { Width = new GridLength(1, GridUnitType.Star) });
        var gridBlock = MakeNumberSubBlock(_loc["SubfolderProp.GridSize"], sub.GridSize,
            v => { sub.GridSize = (int)v; Save(sub); }, asInt: true, hint: _loc["ZoneProp.GridSizeHint"]);
        Grid.SetColumn(gridBlock, 0);
        layoutGrid.Children.Add(gridBlock);
        var snapStack = new StackPanel { VerticalAlignment = VerticalAlignment.Bottom, Margin = new Thickness(0, 18, 0, 4) };
        snapStack.Children.Add(MakeCheckRow(_loc["SubfolderProp.SnapToGrid"], sub.SnapToGrid,
            v => { sub.SnapToGrid = v; Save(sub); }));
        snapStack.Children.Add(MakeCheckRow(_loc["SubfolderProp.AutoArrange"], sub.AutoArrange,
            v => { sub.AutoArrange = v; Save(sub); }));
        Grid.SetColumn(snapStack, 2);
        layoutGrid.Children.Add(snapStack);
        layout.Children.Add(layoutGrid);
        root.Children.Add(layout);

        // D: 外观 — FillFollowsZone 控制 FillColor/FillOpacityOverride 行门控。
        var appearance = MakeSection(_loc["SubfolderProp.Section.Appearance"]);
        appearance.Children.Add(MakeCheckRow(_loc["SubfolderProp.FillFollowsZone"], sub.FillFollowsZone,
            v =>
            {
                sub.FillFollowsZone = v;
                // ponytail: 首次开启 override 时 FillOpacityOverride 还是 -1 (跟随),
                // 给个 100% 默认值,避免滑块停在 0 让 SubFolder 透明不可见。
                if (!v && sub.FillOpacityOverride < 0) sub.FillOpacityOverride = 100;
                Save(sub);
                SetFillGating(v, animate: true);
            }));
        var fillColorRow = MakeColorRow(_loc["SubfolderProp.FillColor"],
            string.IsNullOrEmpty(sub.FillColorOverride) ? "#08000000" : sub.FillColorOverride,
            v => { sub.FillColorOverride = v; Save(sub); });
        appearance.Children.Add(fillColorRow);
        _fillGated.Add(fillColorRow);
        var fillOpacityRow = MakeSliderRow(_loc["SubfolderProp.FillOpacity"], 0, 100, 5,
            sub.FillOpacityOverride < 0 ? 100 : sub.FillOpacityOverride,
            p => { sub.FillOpacityOverride = p; Save(sub); });
        appearance.Children.Add(fillOpacityRow);
        _fillGated.Add(fillOpacityRow);
        // 液态玻璃 — 与主分区同款:开关 + 右侧"…"按钮打开玻璃设置二级对话框。
        // ponytail 2026-08-26: 开启"填充跟随主分区"后本行与图片行一起禁用
        // (跟随 = 玻璃/图片完全取自主分区)。
        var glassRow = MakeCheckRowWithSideBtn(_loc["SubfolderProp.LiquidGlass"], sub.EnableLiquidGlass,
            v => { sub.EnableLiquidGlass = v; Save(sub); },
            _loc["ZoneProp.LiquidGlassSettingsEllipsis"], _ => OpenSubfolderGlassDialog(sub));
        appearance.Children.Add(glassRow);
        _fillGated.Add(glassRow);
        // ponytail: SubFolder 背景图片预览尺寸固定 128×128 (flyout 内部格大小),
        // 不像 Zone 跟随 Width/Height。TitleBarHeight=0 因为 SubFolder 没有标题栏。
        var bgRow = MakeBgImageRow("", new BgImageBinding
        {
            GetPath = () => sub.BackgroundImagePath,
            SetPath = v => sub.BackgroundImagePath = v ?? "",
            GetOpacity = () => sub.BackgroundImageOpacity < 0 ? 30 : sub.BackgroundImageOpacity,
            SetOpacity = v => sub.BackgroundImageOpacity = v,
            GetZoom = () => 1.0,
            SetZoom = _ => { },
            GetOffsetX = () => 0,
            SetOffsetX = _ => { },
            GetOffsetY = () => 0,
            SetOffsetY = _ => { },
            GetWidth = () => 128,
            GetHeight = () => 128,
            CropShape = "Rectangle",
            TitleBarHeight = 0,
            OnSave = () => Save(sub),
        });
        appearance.Children.Add(bgRow);
        _fillGated.Add(bgRow);
        // ponytail 2026-08-26: "背景图片透明度"独立行删除 — 多出来的设置(背景图
        // 不透明度回到模型默认值,不再单列一行)。
        root.Children.Add(appearance);

        // ponytail 2026-08-26: 预设入口统一走底部按钮栏的"加载预设/保存预设"
        // (所有 target 共用,已存在)。面板里不再放预设卡列表 — 预设卡挪到加载预设
        // 的二级界面(LoadPresetDialog 新增 SubfolderCardTemplate)。

        FieldScroller.Content = root;
        SetFillGating(sub.FillFollowsZone, animate: false);
    }

    /// <summary>统一填充/保留原有填充 — sliding-highlight Segmented pill.
    /// onToggle fires with the new UseUnifiedFill value (true = 统一填充).</summary>
    FrameworkElement MakeUnifiedFillRow(bool unified, Action<bool> onToggle)
    {
        var seg = new Segmented
        {
            Margin = new Thickness(0, 4, 0, 2),
            HorizontalAlignment = HorizontalAlignment.Left,
        };
        seg.Items.Add(new SegmentItem { Text = _loc["MergedGroupProp.UnifiedFill"] });
        seg.Items.Add(new SegmentItem { Text = _loc["MergedGroupProp.KeepOriginalFill"] });
        // Subscribe AFTER setting SelectedIndex so the initial assignment
        // doesn't fire onToggle.
        seg.SelectedIndex = unified ? 0 : 1;
        seg.SelectedIndexChanged += (_, _) => onToggle(seg.SelectedIndex == 0);
        return seg;
    }

    /// <summary>圆角/尖角 — same sliding-highlight Segmented pill as the group's
    /// 统一填充/保留原有填充 row. onToggle fires with true = 圆角.</summary>
    FrameworkElement MakeCornerStyleRow(bool rounded, Action<bool> onToggle)
    {
        var seg = new Segmented
        {
            Margin = new Thickness(0, 4, 0, 2),
            HorizontalAlignment = HorizontalAlignment.Left,
        };
        seg.Items.Add(new SegmentItem { Text = _loc["ZoneProp.CornerRounded"] });
        seg.Items.Add(new SegmentItem { Text = _loc["ZoneProp.CornerSharp"] });
        seg.SelectedIndex = rounded ? 0 : 1;
        seg.SelectedIndexChanged += (_, _) => onToggle(seg.SelectedIndex == 0);
        return seg;
    }

    /// <summary>Fade + disable the unified-fill-only rows (填充/液态玻璃/背景图片).
    /// 统一填充 → opacity 1, enabled; 保留原有填充 → opacity 0.4, disabled, 160ms ease-out.</summary>
    void SetUnifiedGating(bool unified, bool animate)
    {
        foreach (var el in _unifiedGated)
        {
            if (el == null) continue;
            el.IsEnabled = unified;
            double to = unified ? 1.0 : 0.4;
            if (!animate)
            {
                el.Opacity = to;
                continue;
            }
            var anim = new System.Windows.Media.Animation.DoubleAnimation(el.Opacity, to, TimeSpan.FromMilliseconds(160))
            {
                EasingFunction = new System.Windows.Media.Animation.CubicEase
                {
                    EasingMode = System.Windows.Media.Animation.EasingMode.EaseOut
                }
            };
            el.BeginAnimation(OpacityProperty, anim);
        }
    }

    /// <summary>SubFolder 填充门控 — Follows=true 时 FillColor / FillOpacityOverride
    /// 两行灰显 + IsEnabled=false,镜像 SetUnifiedGating 的动画曲线。</summary>
    void SetFillGating(bool follows, bool animate)
    {
        foreach (var el in _fillGated)
        {
            if (el == null) continue;
            el.IsEnabled = !follows;
            double to = follows ? 0.4 : 1.0;
            if (!animate)
            {
                el.Opacity = to;
                continue;
            }
            var anim = new System.Windows.Media.Animation.DoubleAnimation(el.Opacity, to, TimeSpan.FromMilliseconds(160))
            {
                EasingFunction = new System.Windows.Media.Animation.CubicEase
                {
                    EasingMode = System.Windows.Media.Animation.EasingMode.EaseOut
                }
            };
            el.BeginAnimation(OpacityProperty, anim);
        }
    }

    // ── 文件夹映射 (folder mapping) section builder ──
    //
    // 勾选启用 + 路径行（可手输/选择文件夹）。磁盘与文件夹走同一个选择对话框
    // （系统文件夹选择器可以直接选磁盘根目录），不再单独提供磁盘入口。

    void AddFolderMappingSection(StackPanel section,
        Func<bool> getEnabled, Action<bool> setEnabled,
        Func<string> getPath, Action<string> setPath,
        Action onChanged)
    {
        TextBox? pathBox = null;
        section.Children.Add(MakeCheckRow(_loc["ZoneProp.FolderMapping"], getEnabled(), v =>
        {
            setEnabled(v);
            onChanged();
            // 启用后若还没有映射路径，直接弹出选择对话框。
            if (v && string.IsNullOrWhiteSpace(getPath()))
                PickFolderMappingPath(setPath, onChanged,
                    () => { if (pathBox != null) pathBox.Text = getPath() ?? ""; });
        }));
        section.Children.Add(MakeFolderMappingPathRow(getPath, setPath, onChanged, out pathBox));
        section.Children.Add(new TextBlock
        {
            Text = _loc["ZoneProp.FolderMappingHint"],
            FontSize = 10,
            Foreground = (Brush)FindResource("Brush.Text.Tertiary"),
            TextWrapping = TextWrapping.Wrap,
            Margin = new Thickness(0, 8, 0, 0),
        });
    }

    Grid MakeFolderMappingPathRow(Func<string> getPath, Action<string> setPath, Action onChanged,
        out TextBox pathBox)
    {
        var grid = new Grid { Margin = new Thickness(0, 6, 0, 0) };
        grid.ColumnDefinitions.Add(new ColumnDefinition { Width = new GridLength(1, GridUnitType.Star) });
        grid.ColumnDefinitions.Add(new ColumnDefinition { Width = GridLength.Auto });
        var tb = new TextBox
        {
            Text = getPath() ?? "",
            Background = (Brush)FindResource("Brush.Bg.Input"),
            Foreground = (Brush)FindResource("Brush.Text.Primary"),
            BorderBrush = (Brush)FindResource("Brush.Border.Subtle"),
            BorderThickness = new Thickness(1),
            Padding = new Thickness(6, 4, 6, 4),
            FontSize = 12,
            VerticalContentAlignment = VerticalAlignment.Center,
        };
        pathBox = tb;
        tb.LostFocus += (_, _) =>
        {
            string raw = (tb.Text ?? "").Trim();
            if (raw.Length == 2 && char.IsLetter(raw[0]) && raw[1] == ':') raw += "\\";
            if (Directory.Exists(raw))
            {
                if (raw != getPath()) { setPath(raw); onChanged(); }
            }
            else
            {
                tb.Text = getPath() ?? ""; // invalid — revert
            }
        };
        tb.KeyDown += (_, e) => { if (e.Key == Key.Enter) Keyboard.ClearFocus(); };
        Grid.SetColumn(tb, 0);
        grid.Children.Add(tb);
        var btn = new Button
        {
            Content = _loc["ZoneProp.FolderMappingChoose"],
            Margin = new Thickness(6, 0, 0, 0),
            Padding = new Thickness(10, 4, 10, 4),
            Background = (Brush)FindResource("Brush.Bg.Input"),
            Foreground = (Brush)FindResource("Brush.Text.Secondary"),
            BorderBrush = (Brush)FindResource("Brush.Border.Subtle"),
            BorderThickness = new Thickness(1),
            Cursor = Cursors.Hand,
            FontSize = 11,
        };
        btn.Click += (_, _) => PickFolderMappingPath(setPath, onChanged,
            () => tb.Text = getPath() ?? "");
        Grid.SetColumn(btn, 1);
        grid.Children.Add(btn);
        return grid;
    }

    void PickFolderMappingPath(Action<string> setPath, Action onChanged, Action? afterPick = null)
    {
        var owner = CachedOwner ?? Window.GetWindow(this);
        var dlg = new Microsoft.Win32.OpenFolderDialog
        {
            Title = _loc["FolderMap.ChooseTitle"],
            Multiselect = false,
        };
        bool? ok;
        try { ok = owner != null ? dlg.ShowDialog(owner) : dlg.ShowDialog(); }
        catch { ok = null; }
        if (ok == true && !string.IsNullOrEmpty(dlg.FolderName))
        {
            setPath(dlg.FolderName);
            onChanged();
            afterPick?.Invoke();
        }
    }

    // ── Field tree for DesktopClock ──
    //
    // ponytail 2026-08-25: rebuilt to the 时钟设置 spec (属性字段分类新.txt).
    // Sections mirror the Zone editor (same builders, same loc keys):
    // 开关区 / 基本 / 标题栏 / 边框与填充 / 液态玻璃 / 背景图片.
    void BuildClockFields(DesktopClock c)
    {
        _tileGated.Clear();
        var root = new StackPanel { Margin = new Thickness(16, 12, 16, 12) };

        // 开关区
        var switches = MakeSection(_loc["ZoneProp.Section.Switches"]);
        switches.Children.Add(MakeCheckRow(_loc["ZoneProp.TileMode"], c.TileMode,
            v =>
            {
                c.TileMode = v;
                Save(c);
                ApplyTileGating(v);
            }));
        switches.Children.Add(MakeCheckRowWithSideBtn(_loc["ZoneProp.RestoreButton"], c.EnableRestoreButton,
            v => { c.EnableRestoreButton = v; Save(c); },
            _loc["Motion.SettingsEllipsis"], _ => OpenMotionDialog(c, () => BuildClockFields(c))));
        switches.Children.Add(MakeCheckRow(_loc["Motion.HoverAutoExpand"], c.HoverAutoExpand,
            v => { c.HoverAutoExpand = v; Save(c); }));
        switches.Children.Add(MakeCheckRow(_loc["ClockProp.Use24Hour"], c.Use24Hour,
            v => { c.Use24Hour = v; Save(c); }));
        switches.Children.Add(MakeCheckRow(_loc["ClockProp.ShowSeconds"], c.ShowSeconds,
            v => { c.ShowSeconds = v; Save(c); }));
        switches.Children.Add(MakeCornerStyleRow(c.CornerRadius > 0, rounded =>
        {
            c.CornerRadius = rounded ? (c.CornerRadius > 0 ? c.CornerRadius : 10) : 0;
            Save(c);
        }));
        root.Children.Add(switches);

        // 基本
        var basic = MakeSection(_loc["ZoneProp.Section.Basic"]);
        basic.Children.Add(MakeIconRow(_loc["ZoneProp.Icon"], c.IconChar,
            v => { c.IconChar = v; Save(c); }, Helpers.IconGlyph.Clock));
        basic.Children.Add(MakeColorRow(_loc["ZoneProp.IconColor"],
            string.IsNullOrEmpty(c.IconColor) ? "#FFFFFF" : c.IconColor,
            v => { c.IconColor = v; Save(c); }));
        var sizeGrid = MakeSizeGrid(
            c.Width, v => { c.Width = v; Save(c); },
            c.Height, v => { c.Height = v; Save(c); });
        basic.Children.Add(sizeGrid);
        var widthBox = (TextBox)((Grid)sizeGrid.Children[0]).Children[1];
        var heightBox = (TextBox)((Grid)sizeGrid.Children[1]).Children[1];
        root.Children.Add(basic);

        // 标题栏
        var tb = MakeSection(_loc["ZoneProp.Section.TitleBar"]);
        tb.Children.Add(MakeColorRow(_loc["ZoneProp.ButtonColor"], c.ButtonColor,
            v => { c.ButtonColor = v; Save(c); }));
        tb.Children.Add(MakeSliderRow(_loc["ZoneProp.ButtonOpacity"], 5, 100, 5,
            c.ControlOpacity,
            v => { c.ControlOpacity = v; Save(c); }));
        root.Children.Add(tb);
        _tileGated.Add(tb); // 磁贴模式 = 无标题栏 → 整组禁用

        // 边框与填充
        var bf = MakeSection(_loc["ZoneProp.Section.BorderFill"]);
        bf.Children.Add(MakeTextRow(_loc["ZoneProp.BorderThickness"], c.BorderThickness.ToString("0.0", CultureInfo.InvariantCulture),
            v => { if (double.TryParse(v, NumberStyles.Float, CultureInfo.InvariantCulture, out var d)) { c.BorderThickness = d; Save(c); } }));
        bf.Children.Add(MakeColorRow(_loc["ZoneProp.BorderColor"], c.BorderColor,
            v => { c.BorderColor = v; Save(c); }));
        bf.Children.Add(MakeSliderRow(_loc["ZoneProp.BorderOpacity"], 0, 100, 5,
            ParsePercent(c.BorderColor, 25),
            p => { c.BorderColor = SetPercent(c.BorderColor, p, "FFFFFF"); Save(c); }));
        bf.Children.Add(MakeColorRow(_loc["ClockProp.DigitalFill"], c.DigitalFillColor,
            v => { c.DigitalFillColor = v; Save(c); }));
        bf.Children.Add(MakeSliderRow(_loc["ClockProp.DigitalFillOpacity"], 0, 100, 5,
            ParsePercent(c.DigitalFillColor, 8),
            p => { c.DigitalFillColor = SetPercent(c.DigitalFillColor, p, "000000"); Save(c); }));
        bf.Children.Add(MakeColorRow(_loc["ClockProp.AnalogFill"], c.AnalogFillColor,
            v => { c.AnalogFillColor = v; Save(c); }));
        bf.Children.Add(MakeSliderRow(_loc["ClockProp.AnalogFillOpacity"], 0, 100, 5,
            ParsePercent(c.AnalogFillColor, 8),
            p => { c.AnalogFillColor = SetPercent(c.AnalogFillColor, p, "000000"); Save(c); }));
        root.Children.Add(bf);

        // 液态玻璃
        var lg = MakeSection(_loc["ZoneProp.Section.LiquidGlass"]);
        lg.Children.Add(MakeCheckRowWithSideBtn(_loc["ZoneProp.LiquidGlass"], c.EnableLiquidGlass,
            v => { c.EnableLiquidGlass = v; Save(c); },
            _loc["ZoneProp.LiquidGlassSettingsEllipsis"], _ => OpenLiquidGlassDialog(c)));
        root.Children.Add(lg);

        // 背景图片 — 数字 / 钟表 two independent images
        var bg = MakeSection(_loc["ZoneProp.Section.BgImage"]);
        bg.Children.Add(MakeBgImageRow(_loc["ClockProp.DigitalBgImage"], new BgImageBinding
        {
            GetPath = () => c.DigitalBackgroundImagePath,
            SetPath = v => c.DigitalBackgroundImagePath = v ?? "",
            GetOpacity = () => c.DigitalBackgroundImageOpacity,
            SetOpacity = v => c.DigitalBackgroundImageOpacity = v,
            GetZoom = () => c.DigitalBgImageZoom,
            SetZoom = v => c.DigitalBgImageZoom = v,
            GetOffsetX = () => c.DigitalBgImageOffsetX,
            SetOffsetX = v => c.DigitalBgImageOffsetX = v,
            GetOffsetY = () => c.DigitalBgImageOffsetY,
            SetOffsetY = v => c.DigitalBgImageOffsetY = v,
            GetWidth = () => c.Width, GetHeight = () => c.Height,
            CropShape = "Rectangle",
            OnSave = () => Save(c),
            SetZoneSize = (w, h) =>
            {
                c.Width = w; c.Height = h; Save(c);
                widthBox.Text = w.ToString("0.##", CultureInfo.InvariantCulture);
                heightBox.Text = h.ToString("0.##", CultureInfo.InvariantCulture);
            },
        }));
        bg.Children.Add(MakeBgImageRow(_loc["ClockProp.AnalogBgImage"], new BgImageBinding
        {
            GetPath = () => c.BackgroundImagePath,
            SetPath = v => c.BackgroundImagePath = v ?? "",
            GetOpacity = () => c.BackgroundImageOpacity,
            SetOpacity = v => c.BackgroundImageOpacity = v,
            GetZoom = () => c.BgImageZoom,
            SetZoom = v => c.BgImageZoom = v,
            GetOffsetX = () => c.BgImageOffsetX,
            SetOffsetX = v => c.BgImageOffsetX = v,
            GetOffsetY = () => c.BgImageOffsetY,
            SetOffsetY = v => c.BgImageOffsetY = v,
            GetWidth = () => 200, GetHeight = () => 200,
            CropShape = "Circle",
            OnSave = () => Save(c),
        }));
        root.Children.Add(bg);

        // 主体内容 — 固定色 + 透明度；秒针单独设置（秒针不随主体内容颜色）。
        var bodyContent = MakeSection(_loc["ZoneProp.Section.BodyContent"]);
        bodyContent.Children.Add(MakeColorRow(_loc["ZoneProp.BodyContentColor"], c.TextColor,
            v => { c.TextColor = v; Save(c); }));
        bodyContent.Children.Add(MakeSliderRow(_loc["ZoneProp.BodyContentOpacity"], 0, 100, 5,
            ParsePercent(c.TextColor, 100),
            p => { c.TextColor = SetPercent(c.TextColor, p, "FFFFFF"); Save(c); }));
        bodyContent.Children.Add(MakeColorRow(_loc["ZoneProp.SecondHandColor"], c.SecondHandColor,
            v => { c.SecondHandColor = v; Save(c); }));
        bodyContent.Children.Add(MakeSliderRow(_loc["ZoneProp.SecondHandOpacity"], 0, 100, 5,
            ParsePercent(c.SecondHandColor, 100),
            p => { c.SecondHandColor = SetPercent(c.SecondHandColor, p, "FF6666"); Save(c); }));
        root.Children.Add(bodyContent);

        FieldScroller.Content = root;
        ApplyTileGating(c.TileMode);
    }

    // ── Field tree for DesktopCalendar ──
    //
    // ponytail 2026-08-25: rebuilt to the 日历设置 spec (属性字段分类新.txt).
    // Same section structure as the Zone editor.
    void BuildCalendarFields(DesktopCalendar cal)
    {
        _tileGated.Clear();
        var root = new StackPanel { Margin = new Thickness(16, 12, 16, 12) };

        // 开关区
        var switches = MakeSection(_loc["ZoneProp.Section.Switches"]);
        switches.Children.Add(MakeCheckRow(_loc["ZoneProp.TileMode"], cal.TileMode,
            v =>
            {
                cal.TileMode = v;
                Save(cal);
                ApplyTileGating(v);
            }));
        switches.Children.Add(MakeCheckRowWithSideBtn(_loc["ZoneProp.RestoreButton"], cal.EnableRestoreButton,
            v => { cal.EnableRestoreButton = v; Save(cal); },
            _loc["Motion.SettingsEllipsis"], _ => OpenMotionDialog(cal, () => BuildCalendarFields(cal))));
        switches.Children.Add(MakeCheckRow(_loc["Motion.HoverAutoExpand"], cal.HoverAutoExpand,
            v => { cal.HoverAutoExpand = v; Save(cal); }));
        switches.Children.Add(MakeCheckRow(_loc["CalendarProp.StartOnMonday"], cal.StartOnMonday,
            v => { cal.StartOnMonday = v; Save(cal); }));
        switches.Children.Add(MakeCornerStyleRow(cal.CornerRadius > 0, rounded =>
        {
            cal.CornerRadius = rounded ? (cal.CornerRadius > 0 ? cal.CornerRadius : 10) : 0;
            Save(cal);
        }));
        root.Children.Add(switches);

        // 基本
        var basic = MakeSection(_loc["ZoneProp.Section.Basic"]);
        basic.Children.Add(MakeIconRow(_loc["ZoneProp.Icon"], cal.IconChar,
            v => { cal.IconChar = v; Save(cal); }, Helpers.IconGlyph.Calendar));
        basic.Children.Add(MakeColorRow(_loc["ZoneProp.IconColor"],
            string.IsNullOrEmpty(cal.IconColor) ? "#FFFFFF" : cal.IconColor,
            v => { cal.IconColor = v; Save(cal); }));
        var sizeGrid = MakeSizeGrid(
            cal.Width, v => { cal.Width = v; Save(cal); },
            cal.Height, v => { cal.Height = v; Save(cal); });
        basic.Children.Add(sizeGrid);
        var widthBox = (TextBox)((Grid)sizeGrid.Children[0]).Children[1];
        var heightBox = (TextBox)((Grid)sizeGrid.Children[1]).Children[1];
        root.Children.Add(basic);

        // 标题栏
        var tb = MakeSection(_loc["ZoneProp.Section.TitleBar"]);
        tb.Children.Add(MakeColorRow(_loc["ZoneProp.ButtonColor"], cal.ButtonColor,
            v => { cal.ButtonColor = v; Save(cal); }));
        tb.Children.Add(MakeSliderRow(_loc["ZoneProp.ButtonOpacity"], 5, 100, 5,
            cal.ControlOpacity,
            v => { cal.ControlOpacity = v; Save(cal); }));
        root.Children.Add(tb);
        _tileGated.Add(tb); // 磁贴模式 = 无标题栏 → 整组禁用

        // 边框与填充
        var bf = MakeSection(_loc["ZoneProp.Section.BorderFill"]);
        bf.Children.Add(MakeTextRow(_loc["ZoneProp.BorderThickness"], cal.BorderThickness.ToString("0.0", CultureInfo.InvariantCulture),
            v => { if (double.TryParse(v, NumberStyles.Float, CultureInfo.InvariantCulture, out var d)) { cal.BorderThickness = d; Save(cal); } }));
        bf.Children.Add(MakeColorRow(_loc["ZoneProp.BorderColor"], cal.BorderColor,
            v => { cal.BorderColor = v; Save(cal); }));
        bf.Children.Add(MakeSliderRow(_loc["ZoneProp.BorderOpacity"], 0, 100, 5,
            ParsePercent(cal.BorderColor, 25),
            p => { cal.BorderColor = SetPercent(cal.BorderColor, p, "FFFFFF"); Save(cal); }));
        bf.Children.Add(MakeColorRow(_loc["ZoneProp.FillColor"], cal.FillColor,
            v => { cal.FillColor = v; Save(cal); }));
        bf.Children.Add(MakeSliderRow(_loc["ZoneProp.FillOpacity"], 0, 100, 5,
            ParsePercent(cal.FillColor, 8),
            p => { cal.FillColor = SetPercent(cal.FillColor, p, "000000"); Save(cal); }));
        root.Children.Add(bf);

        // 液态玻璃
        var lg = MakeSection(_loc["ZoneProp.Section.LiquidGlass"]);
        lg.Children.Add(MakeCheckRowWithSideBtn(_loc["ZoneProp.LiquidGlass"], cal.EnableLiquidGlass,
            v => { cal.EnableLiquidGlass = v; Save(cal); },
            _loc["ZoneProp.LiquidGlassSettingsEllipsis"], _ => OpenLiquidGlassDialog(cal)));
        root.Children.Add(lg);

        // 背景图片
        var bg = MakeSection(_loc["ZoneProp.Section.BgImage"]);
        bg.Children.Add(MakeBgImageRow("", new BgImageBinding
        {
            GetPath = () => cal.BackgroundImagePath,
            SetPath = v => cal.BackgroundImagePath = v ?? "",
            GetOpacity = () => cal.BackgroundImageOpacity,
            SetOpacity = v => cal.BackgroundImageOpacity = v,
            GetZoom = () => cal.BgImageZoom,
            SetZoom = v => cal.BgImageZoom = v,
            GetOffsetX = () => cal.BgImageOffsetX,
            SetOffsetX = v => cal.BgImageOffsetX = v,
            GetOffsetY = () => cal.BgImageOffsetY,
            SetOffsetY = v => cal.BgImageOffsetY = v,
            GetWidth = () => cal.Width, GetHeight = () => cal.Height,
            CropShape = "Rectangle",
            OnSave = () => Save(cal),
            SetZoneSize = (w, h) =>
            {
                cal.Width = w; cal.Height = h; Save(cal);
                widthBox.Text = w.ToString("0.##", CultureInfo.InvariantCulture);
                heightBox.Text = h.ToString("0.##", CultureInfo.InvariantCulture);
            },
        }));
        root.Children.Add(bg);

        // 主体内容 — 替代原「主体内容颜色自适应」的固定色 + 透明度。
        root.Children.Add(BuildBodyContentSection(
            () => cal.TextColor, v => cal.TextColor = v, () => Save(cal)));

        FieldScroller.Content = root;
        ApplyTileGating(cal.TileMode);
    }

    // ── Field tree for StickyNote ──
    //
    // ponytail 2026-08-25: rebuilt to the 便签设置 spec (属性字段分类新.txt).
    // Same section structure as the Zone editor.
    void BuildNoteFields(StickyNote note)
    {
        _tileGated.Clear(); // 便签磁贴模式会砍掉标题栏 — 标题栏 section 注册进 _tileGated 门控
        var root = new StackPanel { Margin = new Thickness(16, 12, 16, 12) };

        // 开关区（行为）
        var switches = MakeSection(_loc["ZoneProp.Section.Switches"]);
        switches.Children.Add(MakeCheckRow(_loc["NoteProp.Pinned"], note.PinnedTop,
            v => { note.PinnedTop = v; Save(note); }));
        switches.Children.Add(MakeCheckRow(_loc["ZoneProp.TileMode"], note.TileMode,
            v => { note.TileMode = v; Save(note); }));
        switches.Children.Add(MakeCheckRowWithSideBtn(_loc["ZoneProp.RestoreButton"], note.EnableRestoreButton,
            v => { note.EnableRestoreButton = v; Save(note); },
            _loc["Motion.SettingsEllipsis"], _ => OpenMotionDialog(note, () => BuildNoteFields(note))));
        switches.Children.Add(MakeCheckRow(_loc["Motion.HoverAutoExpand"], note.HoverAutoExpand,
            v => { note.HoverAutoExpand = v; Save(note); }));
        switches.Children.Add(MakeCornerStyleRow(note.CornerRadius > 0, rounded =>
        {
            note.CornerRadius = rounded ? (note.CornerRadius > 0 ? note.CornerRadius : 10) : 0;
            Save(note);
        }));
        root.Children.Add(switches);

        // 基本
        var basic = MakeSection(_loc["ZoneProp.Section.Basic"]);
        basic.Children.Add(MakeTextRow(_loc["NoteProp.Name"], note.Title,
            v => { note.Title = v ?? ""; Save(note); }));
        basic.Children.Add(MakeColorRow(_loc["NoteProp.NameColor"], note.TitleTextColor,
            v => { note.TitleTextColor = v; Save(note); }));
        basic.Children.Add(MakeIconRow(_loc["ZoneProp.Icon"], note.IconChar,
            v => { note.IconChar = v; Save(note); }, Helpers.IconGlyph.Sticky));
        basic.Children.Add(MakeColorRow(_loc["ZoneProp.IconColor"],
            string.IsNullOrEmpty(note.IconColor) ? "#FFFFFF" : note.IconColor,
            v => { note.IconColor = v; Save(note); }));
        var sizeGrid = MakeSizeGrid(
            note.Width, v => { note.Width = v; Save(note); },
            note.Height, v => { note.Height = v; Save(note); });
        basic.Children.Add(sizeGrid);
        var widthBox = (TextBox)((Grid)sizeGrid.Children[0]).Children[1];
        var heightBox = (TextBox)((Grid)sizeGrid.Children[1]).Children[1];
        root.Children.Add(basic);

        // 标题栏
        var tb = MakeSection(_loc["ZoneProp.Section.TitleBar"]);
        tb.Children.Add(MakeCheckRow(_loc["ZoneProp.TitleBarFillIndependent"], note.TitleBarFillIndependent,
            v => { note.TitleBarFillIndependent = v; Save(note); }));
        tb.Children.Add(MakeColorRow(_loc["ZoneProp.TitleBarColor"], note.TitleBarFillColor,
            v => { note.TitleBarFillColor = v; Save(note); }));
        tb.Children.Add(MakeSliderRow(_loc["ZoneProp.TitleBarOpacity"], 0, 100, 5,
            ParsePercent(note.TitleBarFillColor, 6),
            p => { note.TitleBarFillColor = SetPercent(note.TitleBarFillColor, p, "FFFFFF"); Save(note); }));
        tb.Children.Add(MakeColorRow(_loc["ZoneProp.ButtonColor"], note.ButtonColor,
            v => { note.ButtonColor = v; Save(note); }));
        tb.Children.Add(MakeSliderRow(_loc["ZoneProp.ButtonOpacity"], 5, 100, 5,
            note.ControlOpacity,
            v => { note.ControlOpacity = v; Save(note); }));
        root.Children.Add(tb);
        _tileGated.Add(tb); // 磁贴模式 = 砍掉标题栏 → 标题栏 section 整组禁用

        // 边框与填充
        var bf = MakeSection(_loc["ZoneProp.Section.BorderFill"]);
        bf.Children.Add(MakeTextRow(_loc["ZoneProp.BorderThickness"], note.BorderThickness.ToString("0.0", CultureInfo.InvariantCulture),
            v => { if (double.TryParse(v, NumberStyles.Float, CultureInfo.InvariantCulture, out var d)) { note.BorderThickness = d; Save(note); } }));
        bf.Children.Add(MakeColorRow(_loc["ZoneProp.BorderColor"], note.BorderColor,
            v => { note.BorderColor = v; Save(note); }));
        bf.Children.Add(MakeSliderRow(_loc["ZoneProp.BorderOpacity"], 0, 100, 5,
            ParsePercent(note.BorderColor, 25),
            p => { note.BorderColor = SetPercent(note.BorderColor, p, "FFFFFF"); Save(note); }));
        bf.Children.Add(MakeColorRow(_loc["ZoneProp.FillColor"], note.FillColor,
            v => { note.FillColor = v; Save(note); }));
        bf.Children.Add(MakeSliderRow(_loc["ZoneProp.FillOpacity"], 0, 100, 5,
            ParsePercent(note.FillColor, 8),
            p => { note.FillColor = SetPercent(note.FillColor, p, "000000"); Save(note); }));
        root.Children.Add(bf);

        // 液态玻璃
        var lg = MakeSection(_loc["ZoneProp.Section.LiquidGlass"]);
        lg.Children.Add(MakeCheckRowWithSideBtn(_loc["ZoneProp.LiquidGlass"], note.EnableLiquidGlass,
            v => { note.EnableLiquidGlass = v; Save(note); },
            _loc["ZoneProp.LiquidGlassSettingsEllipsis"], _ => OpenLiquidGlassDialog(note)));
        root.Children.Add(lg);

        // 背景图片
        var bg = MakeSection(_loc["ZoneProp.Section.BgImage"]);
        bg.Children.Add(MakeBgImageRow("", new BgImageBinding
        {
            GetPath = () => note.BackgroundImagePath,
            SetPath = v => note.BackgroundImagePath = v ?? "",
            GetOpacity = () => note.BackgroundImageOpacity,
            SetOpacity = v => note.BackgroundImageOpacity = v,
            GetZoom = () => note.BgImageZoom,
            SetZoom = v => note.BgImageZoom = v,
            GetOffsetX = () => note.BgImageOffsetX,
            SetOffsetX = v => note.BgImageOffsetX = v,
            GetOffsetY = () => note.BgImageOffsetY,
            SetOffsetY = v => note.BgImageOffsetY = v,
            GetWidth = () => note.Width, GetHeight = () => note.Height,
            CropShape = "Rectangle",
            // 便签两行标题栏(28 标题行 + 28 字体工具栏)：内部 28px 分界，共 56px。
            TitleBarHeight = 56,
            TitleBarInnerDividerHeights = new[] { 28.0 },
            OnSave = () => Save(note),
            SetZoneSize = (w, h) =>
            {
                note.Width = w; note.Height = h; Save(note);
                widthBox.Text = w.ToString("0.##", CultureInfo.InvariantCulture);
                heightBox.Text = h.ToString("0.##", CultureInfo.InvariantCulture);
            },
        }));
        root.Children.Add(bg);

        FieldScroller.Content = root;
    }

    // ── Field tree for PanelConfig ──
    //
    // ponytail 2026-08-25: rebuilt to the 面板设置 spec (属性字段分类新.txt).
    // Same section structure as the Zone editor.
    void BuildPanelFields(PanelConfig p)
    {
        _tileGated.Clear(); // 面板无磁贴模式 — 清掉上一目标的标题栏引用
        var root = new StackPanel { Margin = new Thickness(16, 12, 16, 12) };

        // 开关区 — 左 = 圆角/尖角,右(中部) = 面板弹出动效设置按钮。
        var switches = MakeSection(_loc["ZoneProp.Section.Switches"]);
        var cornerRow = MakeCornerStyleRow(p.PanelCornerRadius > 0, rounded =>
        {
            p.PanelCornerRadius = rounded ? (p.PanelCornerRadius > 0 ? p.PanelCornerRadius : 10) : 0;
            Save(p);
        });
        var motionBtn = MakeSideButton(_loc["Motion.SettingsEllipsis"], _ => OpenPanelPopupMotionDialog(p));
        var switchRow = new Grid { Margin = new Thickness(0, 2, 0, 2) };
        switchRow.ColumnDefinitions.Add(new ColumnDefinition { Width = new GridLength(1, GridUnitType.Star) });
        switchRow.ColumnDefinitions.Add(new ColumnDefinition { Width = GridLength.Auto });
        Grid.SetColumn(cornerRow, 0);
        Grid.SetColumn(motionBtn, 1);
        motionBtn.VerticalAlignment = VerticalAlignment.Center;
        switchRow.Children.Add(cornerRow);
        switchRow.Children.Add(motionBtn);
        switches.Children.Add(switchRow);
        root.Children.Add(switches);

        // 基本
        var basic = MakeSection(_loc["ZoneProp.Section.Basic"]);
        var sizeGrid = MakeSizeGrid(
            p.PanelWidth, v => { p.PanelWidth = v; Save(p); },
            p.PanelHeight, v => { p.PanelHeight = v; Save(p); });
        basic.Children.Add(sizeGrid);
        var widthBox = (TextBox)((Grid)sizeGrid.Children[0]).Children[1];
        var heightBox = (TextBox)((Grid)sizeGrid.Children[1]).Children[1];
        root.Children.Add(basic);

        // 标题栏
        var tb = MakeSection(_loc["ZoneProp.Section.TitleBar"]);
        tb.Children.Add(MakeCheckRow(_loc["ZoneProp.TitleBarFillIndependent"], p.PanelTitleBarFillIndependent,
            v => { p.PanelTitleBarFillIndependent = v; Save(p); }));
        tb.Children.Add(MakeColorRow(_loc["ZoneProp.TitleBarColor"], p.PanelTitleBarFillColor,
            v => { p.PanelTitleBarFillColor = v; Save(p); }));
        tb.Children.Add(MakeSliderRow(_loc["ZoneProp.TitleBarOpacity"], 0, 100, 5,
            ParsePercent(p.PanelTitleBarFillColor, 6),
            pct => { p.PanelTitleBarFillColor = SetPercent(p.PanelTitleBarFillColor, pct, "FFFFFF"); Save(p); }));
        tb.Children.Add(MakeColorRow(_loc["ZoneProp.ButtonColor"], p.PanelButtonColor,
            v => { p.PanelButtonColor = v; Save(p); }));
        tb.Children.Add(MakeSliderRow(_loc["ZoneProp.ButtonOpacity"], 5, 100, 5,
            p.PanelControlOpacity,
            v => { p.PanelControlOpacity = v; Save(p); }));
        root.Children.Add(tb);

        // 边框与填充
        var bf = MakeSection(_loc["ZoneProp.Section.BorderFill"]);
        bf.Children.Add(MakeTextRow(_loc["ZoneProp.BorderThickness"], p.PanelBorderThickness.ToString("0.0", CultureInfo.InvariantCulture),
            v => { if (double.TryParse(v, NumberStyles.Float, CultureInfo.InvariantCulture, out var d)) { p.PanelBorderThickness = d; Save(p); } }));
        bf.Children.Add(MakeColorRow(_loc["ZoneProp.BorderColor"], p.PanelBorderColor,
            v => { p.PanelBorderColor = v; Save(p); }));
        bf.Children.Add(MakeSliderRow(_loc["ZoneProp.BorderOpacity"], 0, 100, 5,
            ParsePercent(p.PanelBorderColor, 25),
            pct => { p.PanelBorderColor = SetPercent(p.PanelBorderColor, pct, "FFFFFF"); Save(p); }));
        bf.Children.Add(MakeColorRow(_loc["ZoneProp.FillColor"], p.PanelFillColor,
            v => { p.PanelFillColor = v; Save(p); }));
        bf.Children.Add(MakeSliderRow(_loc["ZoneProp.FillOpacity"], 0, 100, 5,
            ParsePercent(p.PanelFillColor, 8),
            pct => { p.PanelFillColor = SetPercent(p.PanelFillColor, pct, "000000"); Save(p); }));
        root.Children.Add(bf);

        // 液态玻璃
        var lg = MakeSection(_loc["ZoneProp.Section.LiquidGlass"]);
        lg.Children.Add(MakeCheckRowWithSideBtn(_loc["ZoneProp.LiquidGlass"], p.PanelEnableLiquidGlass,
            v => { p.PanelEnableLiquidGlass = v; Save(p); },
            _loc["ZoneProp.LiquidGlassSettingsEllipsis"], _ => OpenPanelGlassDialog(p)));
        root.Children.Add(lg);

        // 背景图片
        var bg = MakeSection(_loc["ZoneProp.Section.BgImage"]);
        bg.Children.Add(MakeBgImageRow("", new BgImageBinding
        {
            GetPath = () => p.PanelBackgroundImagePath,
            SetPath = v => p.PanelBackgroundImagePath = v ?? "",
            GetOpacity = () => p.PanelBackgroundImageOpacity,
            SetOpacity = v => p.PanelBackgroundImageOpacity = v,
            GetZoom = () => p.PanelBgImageZoom,
            SetZoom = v => p.PanelBgImageZoom = v,
            GetOffsetX = () => p.PanelBgImageOffsetX,
            SetOffsetX = v => p.PanelBgImageOffsetX = v,
            GetOffsetY = () => p.PanelBgImageOffsetY,
            SetOffsetY = v => p.PanelBgImageOffsetY = v,
            GetWidth = () => p.PanelWidth, GetHeight = () => p.PanelHeight,
            CropShape = "Rectangle",
            TitleBarHeight = 44,
            OnSave = () => Save(p),
            SetZoneSize = (w, h) =>
            {
                p.PanelWidth = w; p.PanelHeight = h; Save(p);
                widthBox.Text = w.ToString("0.##", CultureInfo.InvariantCulture);
                heightBox.Text = h.ToString("0.##", CultureInfo.InvariantCulture);
            },
        }));
        root.Children.Add(bg);

        // 主体内容 — 替代原「主体内容颜色自适应」的固定色 + 透明度。
        root.Children.Add(BuildBodyContentSection(
            () => p.PanelTextColor, v => p.PanelTextColor = v, () => Save(p)));

        FieldScroller.Content = root;
    }

    // ── Section + row builders ──

    StackPanel MakeSection(string title)
    {
        var section = new StackPanel { Margin = new Thickness(0, 0, 0, 12) };
        section.Children.Add(new Border
        {
            Height = 1,
            Background = (Brush)FindResource("Brush.Border.Subtle"),
            Margin = new Thickness(0, 0, 0, 8),
        });
        section.Children.Add(new TextBlock
        {
            Text = title,
            FontSize = 11,
            FontWeight = FontWeights.SemiBold,
            Foreground = (Brush)FindResource("Brush.Text.Tertiary"),
            Margin = new Thickness(0, 0, 0, 8),
        });
        return section;
    }

    CheckBox MakeCheckRow(string label, bool value, Action<bool> onChange)
    {
        var cb = new CheckBox
        {
            IsChecked = value,
            Margin = new Thickness(0, 2, 0, 2),
            Foreground = (Brush)FindResource("Brush.Text.Primary"),
        };
        cb.SetValue(ContentProperty, label);
        cb.Checked += (_, _) => onChange(true);
        cb.Unchecked += (_, _) => onChange(false);
        return cb;
    }

    FrameworkElement MakeCheckRowWithSideBtn(string label, bool value,
        Action<bool> onChange, string btnText, Action<RoutedEventArgs> onBtnClick)
    {
        var grid = new Grid();
        grid.ColumnDefinitions.Add(new ColumnDefinition { Width = new GridLength(1, GridUnitType.Star) });
        grid.ColumnDefinitions.Add(new ColumnDefinition { Width = GridLength.Auto });
        var cb = new CheckBox
        {
            IsChecked = value,
            Margin = new Thickness(0, 2, 8, 2),
            Foreground = (Brush)FindResource("Brush.Text.Primary"),
        };
        cb.SetValue(ContentProperty, label);
        cb.Checked += (_, _) => onChange(true);
        cb.Unchecked += (_, _) => onChange(false);
        Grid.SetColumn(cb, 0);
        grid.Children.Add(cb);
        var btn = MakeSideButton(btnText, onBtnClick);
        Grid.SetColumn(btn, 1);
        grid.Children.Add(btn);
        return grid;
    }

    /// <summary>行内右侧小字说明 — 把已有控件包成「控件 + 说明文字」的行。
    /// 文字用 Brush.Text.Tertiary（主题自适应），随字段树重建刷新。</summary>
    FrameworkElement MakeRowWithHint(FrameworkElement row, string hint)
    {
        var grid = new Grid();
        grid.ColumnDefinitions.Add(new ColumnDefinition { Width = new GridLength(1, GridUnitType.Star) });
        grid.ColumnDefinitions.Add(new ColumnDefinition { Width = GridLength.Auto });
        Grid.SetColumn(row, 0);
        grid.Children.Add(row);
        var hintText = new TextBlock
        {
            Text = hint,
            FontSize = 10,
            Foreground = (Brush)FindResource("Brush.Text.Tertiary"),
            TextWrapping = TextWrapping.Wrap,
            MaxWidth = 220,
            VerticalAlignment = VerticalAlignment.Center,
            Margin = new Thickness(8, 0, 0, 0),
        };
        Grid.SetColumn(hintText, 1);
        grid.Children.Add(hintText);
        return grid;
    }

    /// <summary>行内右侧的「…」按钮,与 MakeCheckRowWithSideBtn 同款视觉。</summary>
    Button MakeSideButton(string text, Action<RoutedEventArgs> onClick)
    {
        var btn = new Button
        {
            Content = text,
            Margin = new Thickness(0, 2, 0, 2),
            Padding = new Thickness(10, 4, 10, 4),
            Background = (Brush)FindResource("Brush.Bg.Input"),
            Foreground = (Brush)FindResource("Brush.Text.Secondary"),
            BorderBrush = (Brush)FindResource("Brush.Border.Subtle"),
            BorderThickness = new Thickness(1),
            Cursor = System.Windows.Input.Cursors.Hand,
            FontSize = 11,
        };
        btn.Click += (_, e) => onClick(e);
        return btn;
    }

    Grid MakeSizeGrid(double width, Action<double> onWidth, double height, Action<double> onHeight)
    {
        var grid = new Grid { Margin = new Thickness(0, 6, 0, 0) };
        grid.ColumnDefinitions.Add(new ColumnDefinition { Width = new GridLength(1, GridUnitType.Star) });
        grid.ColumnDefinitions.Add(new ColumnDefinition { Width = new GridLength(10) });
        grid.ColumnDefinitions.Add(new ColumnDefinition { Width = new GridLength(1, GridUnitType.Star) });
        var wBlock = MakeNumberSubBlock(_loc["ZoneProp.Width"], width, onWidth);
        Grid.SetColumn(wBlock, 0);
        grid.Children.Add(wBlock);
        var hBlock = MakeNumberSubBlock(_loc["ZoneProp.Height"], height, onHeight);
        Grid.SetColumn(hBlock, 2);
        grid.Children.Add(hBlock);
        return grid;
    }

    /// <summary>
    /// 图标行 — 左半是「预览 + 半宽输入框」，右半是「预设按钮 + 最多 2 字符提示」。
    /// 输入框只承载 emoji（用户手输，MaxLength=2）；原生图标（"@zones" 等）通过预设按钮
    /// 选中后存进模型，输入框显示为空、预览区显示矢量图形。
    /// </summary>
    Grid MakeIconRow(string label, string value, Action<string> onChange, string defaultIcon)
    {
        var grid = new Grid { Margin = new Thickness(0, 4, 0, 0) };
        grid.RowDefinitions.Add(new RowDefinition { Height = GridLength.Auto });
        grid.RowDefinitions.Add(new RowDefinition { Height = GridLength.Auto });
        grid.Children.Add(new TextBlock
        {
            Text = label,
            FontSize = 11,
            Foreground = (Brush)FindResource("Brush.Text.Secondary"),
            Margin = new Thickness(0, 0, 0, 4),
        });

        var row = new Grid();
        row.ColumnDefinitions.Add(new ColumnDefinition { Width = new GridLength(1, GridUnitType.Star) });
        row.ColumnDefinitions.Add(new ColumnDefinition { Width = new GridLength(1, GridUnitType.Star) });
        Grid.SetRow(row, 1);
        grid.Children.Add(row);

        // 左半：预览 + 半宽输入框（原输入框砍一半）。
        var left = new StackPanel { Orientation = Orientation.Horizontal };
        var previewBorder = new Border
        {
            Width = 28, Height = 28,
            CornerRadius = new CornerRadius(6),
            Background = (Brush)FindResource("Brush.Accent.Wash"),
            VerticalAlignment = VerticalAlignment.Center,
            Margin = new Thickness(0, 0, 6, 0),
        };
        var previewHost = new Grid();
        var previewText = new TextBlock
        {
            FontSize = 14,
            HorizontalAlignment = HorizontalAlignment.Center,
            VerticalAlignment = VerticalAlignment.Center,
            Foreground = (Brush)FindResource("Brush.Text.Primary"),
        };
        var previewPath = new System.Windows.Shapes.Path
        {
            Width = 16, Height = 16,
            Stretch = Stretch.Uniform,
            Stroke = (Brush)FindResource("Brush.Accent"),
            StrokeThickness = 1.5,
            StrokeLineJoin = PenLineJoin.Round,
            HorizontalAlignment = HorizontalAlignment.Center,
            VerticalAlignment = VerticalAlignment.Center,
            Visibility = Visibility.Collapsed,
        };
        previewHost.Children.Add(previewText);
        previewHost.Children.Add(previewPath);
        previewBorder.Child = previewHost;

        var tb = new TextBox
        {
            Text = IconGlyph.IsNative(value) ? "" : value ?? "",
            MaxLength = 2,
            Background = (Brush)FindResource("Brush.Bg.Input"),
            Foreground = (Brush)FindResource("Brush.Text.Primary"),
            BorderBrush = (Brush)FindResource("Brush.Border.Subtle"),
            BorderThickness = new Thickness(1),
            Padding = new Thickness(6, 4, 6, 4),
            FontSize = 12,
            VerticalAlignment = VerticalAlignment.Center,
        };
        left.Children.Add(previewBorder);
        left.Children.Add(tb);

        // 右半：预设按钮 + 小字提示。
        var right = new StackPanel { Margin = new Thickness(12, 0, 0, 0), VerticalAlignment = VerticalAlignment.Top };
        var presetBtn = MakeSideButton(_loc["ZoneProp.IconPreset"], _ =>
        {
            var owner = CachedOwner ?? Window.GetWindow(this);
            var dlg = new IconPickerDialog { Owner = owner };
            if (dlg.ShowDialog() == true && dlg.SelectedIcon != null)
            {
                var icon = dlg.SelectedIcon;
                tb.Text = IconGlyph.IsNative(icon) ? "" : icon;
                Commit(icon);
            }
        });
        right.Children.Add(presetBtn);
        right.Children.Add(new TextBlock
        {
            Text = _loc["ZoneProp.IconMaxChars"],
            FontSize = 10,
            Foreground = (Brush)FindResource("Brush.Text.Tertiary"),
            Margin = new Thickness(0, 4, 0, 0),
        });

        Grid.SetColumn(left, 0);
        Grid.SetColumn(right, 1);
        row.Children.Add(left);
        row.Children.Add(right);

        void RefreshPreview(string? icon)
            => IconGlyph.Apply(previewText, previewPath, string.IsNullOrEmpty(icon) ? defaultIcon : icon, (Brush)FindResource("Brush.Accent"), 16);

        void Commit(string icon)
        {
            onChange(icon);
            RefreshPreview(icon);
        }

        RefreshPreview(value);
        tb.LostFocus += (_, _) => Commit(tb.Text);
        tb.KeyDown += (_, e) => { if (e.Key == Key.Enter) { Commit(tb.Text); Keyboard.ClearFocus(); } };
        return grid;
    }

    Grid MakeTextRow(string label, string value, Action<string?> onChange, int maxLen = 0)
    {
        var grid = new Grid { Margin = new Thickness(0, 4, 0, 0) };
        grid.RowDefinitions.Add(new RowDefinition { Height = GridLength.Auto });
        grid.RowDefinitions.Add(new RowDefinition { Height = GridLength.Auto });
        grid.Children.Add(new TextBlock
        {
            Text = label,
            FontSize = 11,
            Foreground = (Brush)FindResource("Brush.Text.Secondary"),
            Margin = new Thickness(0, 0, 0, 4),
        });
        var tb = new TextBox
        {
            Text = value ?? "",
            Background = (Brush)FindResource("Brush.Bg.Input"),
            Foreground = (Brush)FindResource("Brush.Text.Primary"),
            BorderBrush = (Brush)FindResource("Brush.Border.Subtle"),
            BorderThickness = new Thickness(1),
            Padding = new Thickness(6, 4, 6, 4),
            FontSize = 12,
        };
        if (maxLen > 0) tb.MaxLength = maxLen;
        tb.LostFocus += (_, _) => onChange(tb.Text);
        tb.KeyDown += (_, e) => { if (e.Key == Key.Enter) { onChange(tb.Text); Keyboard.ClearFocus(); } };
        Grid.SetRow(tb, 1);
        grid.Children.Add(tb);
        return grid;
    }

    Grid MakeColorRow(string label, string value, Action<string> onChange)
    {
        // ponytail 2026-08-25: 小方框从第二行 → 与文字同行（横向）。
        // 文字 11pt ≈ 14px 高，中间 ≈ y=7；ColorSwatchButton 24x24。
        // 外 margin(0,4,0,0) → (0,0,0,0)：上移 ~4px，让小方框下边缘到下一行的距离保持不变。
        // 小方框 Margin(0,7,0,0)：上边缘对齐到文字的中间。
        var grid = new Grid { Margin = new Thickness(0, 0, 0, 0) };
        grid.ColumnDefinitions.Add(new ColumnDefinition { Width = GridLength.Auto });
        grid.ColumnDefinitions.Add(new ColumnDefinition { Width = GridLength.Auto });
        grid.Children.Add(new TextBlock
        {
            Text = label,
            FontSize = 11,
            Foreground = (Brush)FindResource("Brush.Text.Secondary"),
            VerticalAlignment = VerticalAlignment.Top,
            Margin = new Thickness(0, 0, 8, 0),
        });
        var swatch = new ColorSwatchButton
        {
            CurrentColor = value ?? "#00000000",
            VerticalAlignment = VerticalAlignment.Top,
            Margin = new Thickness(0, 7, 0, 0), // 文字中间 ≈ y=7（11pt ≈ 14px 高）
        };
        // ponytail: ColorSwatchButton doesn't expose a CLR change event; subscribe via
        // DependencyPropertyDescriptor so popup swatch clicks fire our callback.
        DependencyPropertyDescriptor.FromProperty(ColorSwatchButton.CurrentColorProperty, typeof(ColorSwatchButton))
            .AddValueChanged(swatch, (_, _) => onChange(swatch.CurrentColor));
        Grid.SetColumn(swatch, 1);
        grid.Children.Add(swatch);
        return grid;
    }

    /// <summary>数字输入块。hint 非空时在输入框下方追加一行小字说明
    /// （Brush.Text.Tertiary 主题自适应，随字段树重建刷新）。</summary>
    Grid MakeNumberSubBlock(string label, double value, Action<double> onChange, bool asInt = false, string? hint = null)
    {
        var grid = new Grid();
        grid.RowDefinitions.Add(new RowDefinition { Height = GridLength.Auto });
        grid.RowDefinitions.Add(new RowDefinition { Height = GridLength.Auto });
        grid.Children.Add(new TextBlock
        {
            Text = label,
            FontSize = 11,
            Foreground = (Brush)FindResource("Brush.Text.Secondary"),
            Margin = new Thickness(0, 0, 0, 4),
        });
        var tb = new TextBox
        {
            Text = asInt
                ? ((int)value).ToString(CultureInfo.InvariantCulture)
                : value.ToString("0.##", CultureInfo.InvariantCulture),
            Background = (Brush)FindResource("Brush.Bg.Input"),
            Foreground = (Brush)FindResource("Brush.Text.Primary"),
            BorderBrush = (Brush)FindResource("Brush.Border.Subtle"),
            BorderThickness = new Thickness(1),
            Padding = new Thickness(6, 4, 6, 4),
            FontSize = 12,
        };
        tb.LostFocus += (_, _) => TryParse(tb.Text);
        tb.KeyDown += (_, e) => { if (e.Key == Key.Enter) { TryParse(tb.Text); Keyboard.ClearFocus(); } };
        Grid.SetRow(tb, 1);
        grid.Children.Add(tb);
        if (hint != null)
        {
            grid.RowDefinitions.Add(new RowDefinition { Height = GridLength.Auto });
            var hintText = new TextBlock
            {
                Text = hint,
                FontSize = 10,
                Foreground = (Brush)FindResource("Brush.Text.Tertiary"),
                TextWrapping = TextWrapping.Wrap,
                Margin = new Thickness(0, 4, 0, 0),
            };
            Grid.SetRow(hintText, 2);
            grid.Children.Add(hintText);
        }
        return grid;

        void TryParse(string s)
        {
            if (asInt) { if (int.TryParse(s, NumberStyles.Integer, CultureInfo.InvariantCulture, out var i)) onChange(i); }
            else { if (double.TryParse(s, NumberStyles.Float, CultureInfo.InvariantCulture, out var d)) onChange(d); }
        }
    }

    Grid MakeSliderRow(string label, double min, double max, double tick, double value, Action<double> onChange)
    {
        var grid = new Grid { Margin = new Thickness(0, 6, 0, 0) };
        grid.RowDefinitions.Add(new RowDefinition { Height = GridLength.Auto });
        grid.RowDefinitions.Add(new RowDefinition { Height = GridLength.Auto });
        grid.Children.Add(new TextBlock
        {
            Text = label,
            FontSize = 11,
            Foreground = (Brush)FindResource("Brush.Text.Secondary"),
            Margin = new Thickness(0, 0, 0, 4),
        });
        var slider = new SliderWithValue
        {
            Min = min,
            Max = max,
            Tick = tick,
            Value = value,
        };
        // ponytail: SliderWithValue has no ValueChanged event; subscribe via DP descriptor.
        DependencyPropertyDescriptor.FromProperty(SliderWithValue.ValueProperty, typeof(SliderWithValue))
            .AddValueChanged(slider, (_, _) => onChange(slider.Value));
        Grid.SetRow(slider, 1);
        grid.Children.Add(slider);
        return grid;
    }

    /// <summary>主体内容板块：主体内容颜色（色板）+ 主体内容透明度（滑块）。透明度参考现有
    /// 透明度行（ParsePercent/SetPercent），直接操作颜色 alpha 通道。gated 非空时把两行加进去，
    /// 供组合分区「保留原有填充」灰显禁用。</summary>
    StackPanel BuildBodyContentSection(Func<string> getColor, Action<string> setColor, Action save,
        List<FrameworkElement>? gated = null)
    {
        var sec = MakeSection(_loc["ZoneProp.Section.BodyContent"]);
        var colorRow = MakeColorRow(_loc["ZoneProp.BodyContentColor"], getColor(),
            v => { setColor(v); save(); });
        sec.Children.Add(colorRow);
        var opacityRow = MakeSliderRow(_loc["ZoneProp.BodyContentOpacity"], 0, 100, 5,
            ParsePercent(getColor(), 100),
            p => { setColor(SetPercent(getColor(), p, "FFFFFF")); save(); });
        sec.Children.Add(opacityRow);
        if (gated != null) { gated.Add(colorRow); gated.Add(opacityRow); }
        return sec;
    }

    /// <summary>
    /// Model-agnostic background-image field binding. Get/set delegates let one
    /// row builder serve Zone / Clock (digital + analog) / Calendar / Note /
    /// Panel without per-model overloads. Width/Height/CropShape feed the crop
    /// preview; OnSave pushes the edit back through the owning editor's Persist chain.
    /// </summary>
    sealed class BgImageBinding
    {
        public Func<string> GetPath = () => "";
        public Action<string> SetPath = _ => { };
        public Func<double> GetOpacity = () => 30;
        public Action<double> SetOpacity = _ => { };
        public Func<double> GetZoom = () => 1.0;
        public Action<double> SetZoom = _ => { };
        public Func<double> GetOffsetX = () => 0;
        public Action<double> SetOffsetX = _ => { };
        public Func<double> GetOffsetY = () => 0;
        public Action<double> SetOffsetY = _ => { };
        public Func<double> GetWidth = () => 400;
        public Func<double> GetHeight = () => 300;
        /// <summary>裁剪窗口「调整至图片大小」按钮的尺寸回写回调：(宽, 高)。
        /// 实现负责改模型 + Save 实时预览；null 表示该目标不支持自动调整，隐藏按钮。</summary>
        public Action<double, double>? SetZoneSize;
        /// <summary>Crop outline shape for ImageCropPreviewWindow: "Rectangle",
        /// "Circle" (analog clock face) or "Ellipse".</summary>
        public string CropShape = "Rectangle";
        /// <summary>真实窗口标题栏高度（DIP）。裁剪预览据此绘制标题栏/主体分界线并吸附；
        /// 0 = 无标题栏（时钟/日历）。Zone=24、便签=28、面板=44、组合分区=48；
        /// 开启文件夹映射时加上映射头部行 26px。</summary>
        public double TitleBarHeight = 0;
        /// <summary>标题栏内部的分界线高度（DIP）——组合分区最上方标题栏与子分区
        /// 标签栏之间的分界（24）；开启文件夹映射时再加映射头部行分界
        /// （普通分区 24，组合分区 48）。空 = 无内部标题栏分界。</summary>
        public double[] TitleBarInnerDividerHeights = Array.Empty<double>();
        public Action OnSave = () => { };
    }

    Grid MakeBgImageRow(string label, BgImageBinding b)
    {
        var outer = new Grid { Margin = new Thickness(0, 4, 0, 0) };
        outer.RowDefinitions.Add(new RowDefinition { Height = GridLength.Auto });
        outer.RowDefinitions.Add(new RowDefinition { Height = GridLength.Auto });
        int row = 0;
        if (!string.IsNullOrEmpty(label))
        {
            outer.Children.Add(new TextBlock
            {
                Text = label,
                FontSize = 11,
                Foreground = (Brush)FindResource("Brush.Text.Secondary"),
                Margin = new Thickness(0, 0, 0, 4),
            });
            row = 1;
        }

        // 地址框 / 裁剪 / 选图 / 清除 — 裁剪按钮放在选图左侧，用剪刀图标
        // （无文字），地址框为星号列会自动压缩给裁剪按钮让位。
        var grid = new Grid();
        grid.ColumnDefinitions.Add(new ColumnDefinition { Width = new GridLength(1, GridUnitType.Star) });
        grid.ColumnDefinitions.Add(new ColumnDefinition { Width = GridLength.Auto });
        grid.ColumnDefinitions.Add(new ColumnDefinition { Width = GridLength.Auto });
        grid.ColumnDefinitions.Add(new ColumnDefinition { Width = GridLength.Auto });
        var tb = new TextBox
        {
            Text = b.GetPath() ?? "",
            IsReadOnly = true,
            Background = (Brush)FindResource("Brush.Bg.Input"),
            Foreground = (Brush)FindResource("Brush.Text.Primary"),
            BorderBrush = (Brush)FindResource("Brush.Border.Subtle"),
            BorderThickness = new Thickness(1),
            Padding = new Thickness(6, 4, 6, 4),
            FontSize = 12,
        };
        Grid.SetColumn(tb, 0);
        grid.Children.Add(tb);
        var crop = new Button
        {
            Width = 28,
            Margin = new Thickness(6, 0, 0, 0),
            Padding = new Thickness(0),
            Background = Brushes.Transparent,
            BorderThickness = new Thickness(0),
            Cursor = System.Windows.Input.Cursors.Hand,
            ToolTip = _loc["BgImage.Crop"],
        };
        var cropIcon = new System.Windows.Shapes.Path
        {
            Width = 14,
            Height = 14,
            Stretch = Stretch.Uniform,
            StrokeThickness = 1.5,
            StrokeLineJoin = PenLineJoin.Round,
            Data = (Geometry)FindResource("Icon.Scissors"),
        };
        // Stroke follows the button Foreground so the disabled state (no image
        // selected yet) grays the scissors out via the default Button template.
        cropIcon.SetBinding(System.Windows.Shapes.Shape.StrokeProperty,
            new System.Windows.Data.Binding("Foreground")
            {
                RelativeSource = new System.Windows.Data.RelativeSource(
                    System.Windows.Data.RelativeSourceMode.FindAncestor, typeof(Button), 1)
            });
        crop.Foreground = (Brush)FindResource("Brush.Text.Secondary");
        crop.Content = cropIcon;
        crop.IsEnabled = !string.IsNullOrEmpty(b.GetPath()) && System.IO.File.Exists(b.GetPath());
        crop.Click += (_, _) => OpenCropDialog(b);
        Grid.SetColumn(crop, 1);
        grid.Children.Add(crop);
        var browse = new Button
        {
            Content = _loc["BgImage.Browse"],
            Margin = new Thickness(6, 0, 0, 0),
            Padding = new Thickness(10, 4, 10, 4),
            Background = (Brush)FindResource("Brush.Bg.Input"),
            Foreground = (Brush)FindResource("Brush.Text.Secondary"),
            BorderBrush = (Brush)FindResource("Brush.Border.Subtle"),
            BorderThickness = new Thickness(1),
            Cursor = System.Windows.Input.Cursors.Hand,
            FontSize = 11,
        };
        browse.Click += (_, _) =>
        {
            OpenImagePicker(b, tb);
            crop.IsEnabled = !string.IsNullOrEmpty(b.GetPath()) && System.IO.File.Exists(b.GetPath());
        };
        Grid.SetColumn(browse, 2);
        grid.Children.Add(browse);
        var clear = new Button
        {
            Content = _loc["BgImage.Clear"],
            Margin = new Thickness(6, 0, 0, 0),
            Padding = new Thickness(10, 4, 10, 4),
            Background = Brushes.Transparent,
            Foreground = (Brush)FindResource("Brush.Text.Tertiary"),
            BorderThickness = new Thickness(0),
            Cursor = System.Windows.Input.Cursors.Hand,
            FontSize = 11,
        };
        clear.Click += (_, _) => { b.SetPath(""); tb.Text = ""; crop.IsEnabled = false; b.OnSave(); };
        Grid.SetColumn(clear, 3);
        grid.Children.Add(clear);
        Grid.SetRow(grid, row);
        outer.Children.Add(grid);
        return outer;
    }

    // ponytail: per-type copy helpers used by Cancel / inner preset-cancel /
    // preset apply. Ported from historical WidgetSettingsDialog (commit 89c7bb5)
    // and expanded to cover the CURRENT field trees so Cancel reverts every
    // field the editor can touch. UseGlobalAppearance / Global* fields were
    // removed in commit e4bd2cf (delete global appearance). Identity fields
    // (Id) and position (X/Y) are never copied; size IS reverted because the
    // editors expose Width/Height.

    void CopyZoneFields(Zone src, Zone dst)
    {
        // Full style restore: every field the Zone editor touches. Identity /
        // position / content / membership are never copied.
        CloneHelper.CopyBaseProperties<AppearanceModel>(src, dst);
        dst.Name = src.Name;
        dst.Width = src.Width;
        dst.Height = src.Height;
        dst.BorderThickness = src.BorderThickness;
        dst.CornerRadius = src.CornerRadius;
        dst.GridSize = src.GridSize;
        dst.SnapToGrid = src.SnapToGrid;
        dst.IconChar = src.IconChar;
        dst.TitleBarFillColor = src.TitleBarFillColor;
        dst.ControlOpacity = src.ControlOpacity;
        dst.BackgroundImageOpacity = src.BackgroundImageOpacity;
        dst.AutoArrange = src.AutoArrange;
        dst.IconColor = src.IconColor;
        dst.TitleTextColor = src.TitleTextColor;
        dst.TileMode = src.TileMode;
        dst.HideAppName = src.HideAppName;
        dst.CustomIcon = src.CustomIcon;
        dst.ButtonColor = src.ButtonColor;
        dst.TextColor = src.TextColor;
        dst.TitleBarFillIndependent = src.TitleBarFillIndependent;
        dst.FolderMappingEnabled = src.FolderMappingEnabled;
        dst.FolderMappingPath = src.FolderMappingPath;
        CloneHelper.CopyBaseProperties<MergedGroupStyle>(src.MergedGroupStyle, dst.MergedGroupStyle);
    }

    // ponytail 2026-08-26: SubFolder 取消还原 — 镜像 spec §4.5 SubFolder 专属 14 字段 + Name。
    // SubItems (内容) 不还原 — 镜像 SubfolderPreset 不含 SubItems 的做法,避免取消意外清空内容。
    // 身份字段 (Id / X / Y / IconPath / Type / TargetPath) 不还原。
    void CopySubfolderFields(ZoneItem src, ZoneItem dst)
    {
        dst.Name = src.Name;
        dst.IconSizeAutoGrow = src.IconSizeAutoGrow;
        dst.CornerRounded = src.CornerRounded;
        dst.FillFollowsZone = src.FillFollowsZone;
        dst.FillColorOverride = src.FillColorOverride;
        dst.FillOpacityOverride = src.FillOpacityOverride;
        dst.BackgroundImagePath = src.BackgroundImagePath;
        dst.BackgroundImageOpacity = src.BackgroundImageOpacity;
        dst.EnableLiquidGlass = src.EnableLiquidGlass;
        dst.GlassBlurAmount = src.GlassBlurAmount;
        dst.GlassTintOpacity = src.GlassTintOpacity;
        dst.GlassTintLuminosity = src.GlassTintLuminosity;
        dst.GlassColorMode = src.GlassColorMode;
        dst.GridSize = src.GridSize;
        dst.SnapToGrid = src.SnapToGrid;
        dst.AutoArrange = src.AutoArrange;
        dst.HoverAnimation = src.HoverAnimation;
        dst.HoverExpandSpeed = src.HoverExpandSpeed;
        dst.HoverAutoExpand = src.HoverAutoExpand;
    }

    /// <summary>Cancel-restore for the merged-group editor: everything the group
    /// editor touches — group style + membership display fields + the master's
    /// window-level behavior/size fields. Identity (GroupId / SubZoneIds /
    /// TabOrder) is never copied.</summary>
    void CopyMergedGroupFields(Zone src, Zone dst)
    {
        CloneHelper.CopyBaseProperties<AppearanceModel>(src, dst);
        dst.TileMode = src.TileMode;
        dst.ButtonColor = src.ButtonColor;
        dst.TextColor = src.TextColor;
        dst.TitleBarFillIndependent = src.TitleBarFillIndependent;
        dst.TitleBarFillColor = src.TitleBarFillColor;
        dst.TitleTextColor = src.TitleTextColor;
        dst.IconColor = src.IconColor;
        dst.ControlOpacity = src.ControlOpacity;
        dst.CornerRadius = src.CornerRadius;
        dst.Width = src.Width;
        dst.Height = src.Height;
        dst.GridSize = src.GridSize;
        dst.SnapToGrid = src.SnapToGrid;
        dst.MergedGroupMembership.DisplayName = src.MergedGroupMembership.DisplayName;
        dst.MergedGroupMembership.Icon = src.MergedGroupMembership.Icon;
        dst.FolderMappingEnabled = src.FolderMappingEnabled;
        dst.FolderMappingPath = src.FolderMappingPath;
        CloneHelper.CopyBaseProperties<MergedGroupStyle>(src.MergedGroupStyle, dst.MergedGroupStyle);
    }

    void CopyClockFields(DesktopClock src, DesktopClock dst)
    {
        // Full restore for every field the Clock editor touches. Identity /
        // position (Id/X/Y) stay untouched; size/behavior ARE reverted because
        // the editor exposes Width/Height/Use24Hour/ShowSeconds/TileMode.
        CloneHelper.CopyBaseProperties<AppearanceModel>(src, dst);
        dst.BorderThickness = src.BorderThickness;
        dst.CornerRadius = src.CornerRadius;
        dst.BackgroundImageOpacity = src.BackgroundImageOpacity;
        dst.AnalogFillColor = src.AnalogFillColor;
        dst.DigitalFillColor = src.DigitalFillColor;
        dst.DigitalBackgroundImagePath = src.DigitalBackgroundImagePath;
        dst.DigitalBgImageStretch = src.DigitalBgImageStretch;
        dst.DigitalBgImageZoom = src.DigitalBgImageZoom;
        dst.DigitalBgImageOffsetX = src.DigitalBgImageOffsetX;
        dst.DigitalBgImageOffsetY = src.DigitalBgImageOffsetY;
        dst.DigitalBackgroundImageOpacity = src.DigitalBackgroundImageOpacity;
        dst.TextColor = src.TextColor;
        dst.ButtonColor = src.ButtonColor;
        dst.SecondHandColor = src.SecondHandColor;
        dst.TileMode = src.TileMode;
        dst.ControlOpacity = src.ControlOpacity;
        dst.Use24Hour = src.Use24Hour;
        dst.ShowSeconds = src.ShowSeconds;
        dst.Width = src.Width;
        dst.Height = src.Height;
        dst.DigitalWidth = src.DigitalWidth;
        dst.DigitalHeight = src.DigitalHeight;
        dst.AnalogWidth = src.AnalogWidth;
        dst.AnalogHeight = src.AnalogHeight;
    }

    void CopyCalendarFields(DesktopCalendar src, DesktopCalendar dst)
    {
        // Full restore for every field the Calendar editor touches. Identity /
        // position (Id/X/Y) stay untouched; size/behavior ARE reverted.
        CloneHelper.CopyBaseProperties<AppearanceModel>(src, dst);
        dst.BorderThickness = src.BorderThickness;
        dst.CornerRadius = src.CornerRadius;
        dst.BackgroundImageOpacity = src.BackgroundImageOpacity;
        dst.TextColor = src.TextColor;
        dst.ButtonColor = src.ButtonColor;
        dst.TodayColor = src.TodayColor;
        dst.FontSize = src.FontSize;
        dst.TileMode = src.TileMode;
        dst.ControlOpacity = src.ControlOpacity;
        dst.StartOnMonday = src.StartOnMonday;
        dst.Width = src.Width;
        dst.Height = src.Height;
    }

    // ponytail: StickyNote has too many fields for an explicit whitelist.
    // Use reflection over public mutable properties, skipping user data and
    // identity. _noteExcluded is the same whitelist used historically in
    // WidgetSettingsDialog; cleaned up to drop hotkey fields that no longer
    // exist (commit e4bd2cf removed the global appearance + cleanup pass).
    static readonly System.Collections.Generic.HashSet<string> _noteExcluded =
        new(System.StringComparer.Ordinal)
        {
            nameof(StickyNote.Id),
            nameof(StickyNote.X), nameof(StickyNote.Y),
            nameof(StickyNote.IsVisible),
            nameof(StickyNote.Title), nameof(StickyNote.Content),
            nameof(StickyNote.NoteColor),
            nameof(StickyNote.FontSize),
            nameof(StickyNote.PinnedTop),
            nameof(StickyNote.LastSavePath),
            nameof(StickyNote.HotkeyEnabled),
            nameof(StickyNote.HotkeyModifiers),
            nameof(StickyNote.HotkeyKey),
            nameof(StickyNote.CustomHotkeys),
            nameof(StickyNote.CreatedAt), nameof(StickyNote.ModifiedAt),
        };

    static void CopyNoteFields(StickyNote src, StickyNote dst)
    {
        // POCOs expose public mutable properties — reflection assignment is
        // enough. Skips user-data fields per _noteExcluded whitelist.
        foreach (var prop in typeof(StickyNote).GetProperties())
        {
            if (!prop.CanRead || !prop.CanWrite) continue;
            if (prop.GetSetMethod(true) == null) continue;
            if (_noteExcluded.Contains(prop.Name)) continue;
            prop.SetValue(dst, prop.GetValue(src));
        }
    }

    void CopyPanelConfigFields(PanelPresetConfig src, PanelConfig dst)
    {
        // ponytail: Target is PanelConfig (extracted from AppConfig in the
        // Panel POCO refactor). src is PanelPresetConfig. Position (PanelX /
        // PanelY) is never copied — it isn't edited in the panel; size IS
        // reverted/applied because the editor exposes Width/Height. Field
        // naming differs between the two: PanelConfig uses PanelGlass* prefix
        // (e.g. PanelGlassBlurAmount); PanelPresetConfig uses bare Glass*
        // (e.g. GlassBlurAmount). Map them explicitly.
        dst.PanelWidth = src.PanelWidth;
        dst.PanelHeight = src.PanelHeight;
        dst.PanelFillColor = src.PanelFillColor;
        dst.PanelBorderColor = src.PanelBorderColor;
        dst.PanelBorderThickness = src.PanelBorderThickness;
        dst.PanelCornerRadius = src.PanelCornerRadius;
        dst.PanelTitleBarFillColor = src.PanelTitleBarFillColor;
        dst.PanelTitleBarFillIndependent = src.PanelTitleBarFillIndependent;
        dst.PanelButtonColor = src.PanelButtonColor;
        dst.PanelTextColor = src.PanelTextColor;
        dst.PanelControlOpacity = src.PanelControlOpacity;
        dst.PanelBackgroundImagePath = src.PanelBackgroundImagePath;
        dst.PanelBgImageStretch = src.PanelBgImageStretch;
        dst.PanelBackgroundImageOpacity = src.PanelBackgroundImageOpacity;
        dst.PanelBgImageZoom = src.PanelBgImageZoom;
        dst.PanelBgImageOffsetX = src.PanelBgImageOffsetX;
        dst.PanelBgImageOffsetY = src.PanelBgImageOffsetY;
        dst.PanelEnableLiquidGlass = src.EnableLiquidGlass;
        dst.PanelGlassBlurAmount = src.GlassBlurAmount;
        dst.PanelGlassTintOpacity = src.GlassTintOpacity;
        dst.PanelGlassTintLuminosity = src.GlassTintLuminosity;
        dst.PanelGlassColorMode = src.GlassColorMode;
        dst.PanelPopupMotion = src.PanelPopupMotion;
        dst.PanelPopupOrigin = src.PanelPopupOrigin;
        dst.PanelPopupSpeed = src.PanelPopupSpeed;
    }

    /// <summary>
    /// Preview-mode edit sink. Field rows mutate the in-memory model and call
    /// this to repaint the live desktop window WITHOUT writing to disk. Disk
    /// persistence is deferred to Apply (<see cref="Commit"/>). The method name
    /// is kept as "Save" so the ~140 field-row call sites don't churn; its
    /// contract is now the historical PushToWidget / PushToZone (live preview),
    /// not persistence.
    /// </summary>
    void Save(object target)
    {
        try { Preview?.Invoke(target); }
        catch (Exception ex)
        {
            DzTrace.Log($"[SubEdit] Preview 异常: {ex}");
            System.Diagnostics.Debug.WriteLine($"[PropertyPanel] Preview failed: {ex}");
        }
        if (target is ZoneItem sub && sub.Type == ItemType.SubFolder)
            DzTrace.Log($"[SubEdit] Save: id={sub.Id.ToString("N")[..8]} FillFollows={sub.FillFollowsZone} Corner={sub.CornerRounded} Hover={sub.HoverAutoExpand} Fill={sub.FillColorOverride} Opacity={sub.FillOpacityOverride}");
    }

    /// <summary>Commit the current preview state to disk via the host's Persist
    /// dispatcher (UpdateZone / UpdateClock / UpdateCalendar / UpdateNote /
    /// SaveConfig / UpdateZone for merged &amp; subfolder). Called by Apply only.
    /// Returns false (and keeps the window open) when persistence throws.</summary>
    bool Commit(object target)
    {
        try { Persist?.Invoke(target); return true; }
        catch (Exception ex)
        {
            // ponytail: surface to debug + show inline error dot in panel header (not a popup)
            System.Diagnostics.Debug.WriteLine($"[PropertyPanel] Persist failed: {ex}");
            ShowPersistError(true);
            return false;
        }
    }

    void ShowPersistError(bool on)
    {
        if (PersistErrorDot != null) PersistErrorDot.Visibility = on ? Visibility.Visible : Visibility.Collapsed;
    }

    // ── Color alpha helpers ──

    static double ParsePercent(string hex, double fallback)
    {
        if (string.IsNullOrEmpty(hex) || hex.Length < 3 || hex[0] != '#') return fallback;
        try
        {
            // ponytail 2026-08-25: 兼容 #AARRGGBB（9 字符）和 #RRGGBB（7 字符）两种格式。
            // 模型默认 "#08000000" 是 #AARRGGBB，但 ColorSwatchButton 预设（"#FFA726" 等）
            // 和 ColorPickerDialog 返回的都是 #RRGGBB — 这俩混用导致拖透明度滑块改色相的 bug。
            // #RRGGBB 没显式 alpha，按 100% 不透明对待（这也是 WPF ColorConverter 的默认）。
            var a = hex.Length >= 9
                ? byte.Parse(hex.Substring(1, 2), NumberStyles.HexNumber, CultureInfo.InvariantCulture)
                : (byte)255;
            return Math.Round(a / 255.0 * 100);
        }
        catch { return fallback; }
    }

    static string SetPercent(string hex, double percent, string fallbackRgb)
    {
        // ponytail 2026-08-25: 跟 ParsePercent 配对 — 两种格式的 RGB 提取位置不同：
        //   #AARRGGBB（9 字符）：跳过前 3 字符 "#AA"，取后 6 位 "RRGGBB"
        //   #RRGGBB  （7 字符）：跳 "#" 即可，整段 6 位就是 RGB
        // 之前 length>=7 一律从 Substring(3) 取，把 #RRGGBB 的 R 通道当 alpha 吞了，
        // 用户拖滑块时 R 被砍半 → "橙色变绿色"。
        // 输出统一归一化为 #AARRGGBB，下一次 ParsePercent / ColorConverter 都不会再误读。
        string rgb;
        if (hex != null && hex.Length >= 9)
            rgb = hex.Substring(3);
        else if (hex != null && hex.Length >= 7)
            rgb = hex.Substring(1);
        else
            rgb = fallbackRgb;
        var a = (int)Math.Clamp(Math.Round(percent / 100.0 * 255), 0, 255);
        return $"#{a:X2}{rgb}";
    }

    // ── Secondary window openers ──
    //
    // ponytail: each helper gets the host Window via Window.GetWindow(this) so it can
    // own the dialog (modal). Persist via Save(z) at the end so the property panel's
    // subscriber pushes the change back to ZoneManager and the live zone window
    // re-reads on ZonesChanged.

    void OpenMotionDialog(AppearanceModel m, Action rebuildFields)
    {
        var owner = CachedOwner ?? Window.GetWindow(this);
        if (owner == null) { MessageBox.Show(_loc["Dialog.NoOwnerWindow"]); return; }
        var dlg = new MotionSettingsDialog(m.HoverExpandAnimation, m.HoverExpandOrigin, m.HoverExpandSpeed)
        {
            Owner = owner
        };
        if (dlg.ShowDialog() != true) return;
        m.HoverExpandOrigin = dlg.ResultHoverExpandOrigin;
        m.HoverExpandAnimation = dlg.ResultHoverExpandAnimation;
        m.HoverExpandSpeed = dlg.ResultHoverExpandSpeed;
        Save(m);
        // ponytail: 2026-08-21 — notify live HoverExpandBehavior instances so the
        // new kind/origin/speed take effect on the next expand/collapse. Without
        // this the live behaviour kept its ctor-time origin (ButtonCenter) and
        // ButtonCorner never took effect, and Scale=0 from the previous kind
        // leaked into the new one as a 36×36 ghost frame.
        m.RaiseHoverExpandSettingsChanged();
        // Re-build fields so the checkbox reflects any change to HoverAutoExpand.
        rebuildFields();
    }

    /// <summary>SubFolder 动效二级窗口。复用分区的 MotionSettingsDialog,但 SubFolder
    /// 没有展开原点概念(原点永远是 SubFolder 图标自身,spec §5 不暴露),所以隐藏原点
    /// 选择行;只回写动画类型 + 速度。</summary>
    void OpenSubfolderMotionDialog(ZoneItem sub)
    {
        var owner = CachedOwner ?? Window.GetWindow(this);
        if (owner == null) { MessageBox.Show(_loc["PropertyPanel.NoOwnerWindow"]); return; }
        var dlg = new MotionSettingsDialog(sub.HoverAnimation, HoverExpandOrigin.ButtonCenter, sub.HoverExpandSpeed, showOrigin: false)
        {
            Owner = owner
        };
        if (dlg.ShowDialog() != true) return;
        sub.HoverAnimation = dlg.ResultHoverExpandAnimation;
        sub.HoverExpandSpeed = dlg.ResultHoverExpandSpeed;
        Save(sub);
        // ponytail: flyout 每次打开都从 live ZoneItem 读 kind/speed,无需像
        // AppearanceModel 那样广播 RaiseHoverExpandSettingsChanged。
    }

    /// <summary>面板弹出动效二级窗口。展开原点 = 桌面四角之一(焦点显示器工作区),
    /// 动画类型与其他窗口同款;回写原点 + 动画类型 + 速度。</summary>
    void OpenPanelPopupMotionDialog(PanelConfig p)
    {
        var owner = CachedOwner ?? Window.GetWindow(this);
        if (owner == null) { MessageBox.Show(_loc["PropertyPanel.NoOwnerWindow"]); return; }
        var dlg = new PanelPopupMotionDialog(p.PanelPopupOrigin, p.PanelPopupMotion, p.PanelPopupSpeed)
        {
            Owner = owner
        };
        if (dlg.ShowDialog() != true) return;
        p.PanelPopupOrigin = dlg.ResultOrigin;
        p.PanelPopupMotion = dlg.ResultAnimation;
        p.PanelPopupSpeed = dlg.ResultSpeed;
        Save(p);
        // 面板每次打开/关闭都从 live PanelConfig 读动效配置,无需额外广播。
    }

    void OpenLiquidGlassDialog(AppearanceModel m)
    {
        var owner = CachedOwner ?? Window.GetWindow(this);
        if (owner == null) { MessageBox.Show(_loc["PropertyPanel.NoOwnerWindow"]); return; }
        int blur = m.GlassBlurAmount;
        int tint = m.GlassTintOpacity;
        int lum = m.GlassTintLuminosity;
        string mode = m.GlassColorMode;
        var cn = LocalizationService.Instance.CurrentLanguage == "zh";
        if (!AcrylicHelper.ShowLiquidGlassDialog(owner, _loc["ZoneProp.Section.LiquidGlass"],
            ref blur, ref tint, ref lum, ref mode, cn)) return;
        m.GlassBlurAmount = blur;
        m.GlassTintOpacity = tint;
        m.GlassTintLuminosity = lum;
        m.GlassColorMode = mode;
        Save(m);
    }

    /// <summary>ponytail 2026-08-26: SubFolder 版液态玻璃设置 — ZoneItem 自带玻璃
    /// 四参数(与 AppearanceModel 同款),对话框复用 AcrylicHelper.ShowLiquidGlassDialog。</summary>
    void OpenSubfolderGlassDialog(ZoneItem sub)
    {
        var owner = CachedOwner ?? Window.GetWindow(this);
        if (owner == null) { MessageBox.Show(_loc["PropertyPanel.NoOwnerWindow"]); return; }
        int blur = sub.GlassBlurAmount;
        int tint = sub.GlassTintOpacity;
        int lum = sub.GlassTintLuminosity;
        string mode = sub.GlassColorMode;
        var cn = LocalizationService.Instance.CurrentLanguage == "zh";
        if (!AcrylicHelper.ShowLiquidGlassDialog(owner, _loc["ZoneProp.Section.LiquidGlass"],
            ref blur, ref tint, ref lum, ref mode, cn)) return;
        sub.GlassBlurAmount = blur;
        sub.GlassTintOpacity = tint;
        sub.GlassTintLuminosity = lum;
        sub.GlassColorMode = mode;
        Save(sub);
    }

    // ponytail 2026-08-25: PanelConfig is not an AppearanceModel — the panel
    // glass knobs live on the Panel POCO, so this opener mirrors the generic
    // one with Panel-prefixed fields.
    void OpenPanelGlassDialog(PanelConfig p)
    {
        var owner = CachedOwner ?? Window.GetWindow(this);
        if (owner == null) { MessageBox.Show(_loc["PropertyPanel.NoOwnerWindow"]); return; }
        int blur = p.PanelGlassBlurAmount;
        int tint = p.PanelGlassTintOpacity;
        int lum = p.PanelGlassTintLuminosity;
        string mode = p.PanelGlassColorMode;
        var cn = LocalizationService.Instance.CurrentLanguage == "zh";
        if (!AcrylicHelper.ShowLiquidGlassDialog(owner, _loc["ZoneProp.Section.LiquidGlass"],
            ref blur, ref tint, ref lum, ref mode, cn)) return;
        p.PanelGlassBlurAmount = blur;
        p.PanelGlassTintOpacity = tint;
        p.PanelGlassTintLuminosity = lum;
        p.PanelGlassColorMode = mode;
        Save(p);
    }

    void OpenImagePicker(BgImageBinding b, TextBox pathBox)
    {
        var owner = CachedOwner ?? Window.GetWindow(this);
        if (owner == null) { MessageBox.Show(_loc["Dialog.NoOwnerWindow"]); return; }
        var dlg = new Microsoft.Win32.OpenFileDialog
        {
            Title = _loc["BgImage.ChooseTitle"],
            Filter = _loc["BgImage.Filter"],
            CheckFileExists = true,
        };
        if (dlg.ShowDialog(owner) != true) return;

        // 选图只负责选图 + 实时同步；裁剪交给旁边的剪刀按钮单独触发，
        // 与重构前的 WidgetSettingsDialog / ZoneSettingsDialog 行为一致。
        b.SetPath(dlg.FileName);
        if (pathBox != null) pathBox.Text = dlg.FileName;
        b.OnSave();
    }

    void OpenCropDialog(BgImageBinding b)
    {
        var owner = CachedOwner ?? Window.GetWindow(this);
        if (owner == null) { MessageBox.Show(_loc["Dialog.NoOwnerWindow"]); return; }
        var path = b.GetPath();
        if (string.IsNullOrEmpty(path) || !System.IO.File.Exists(path)) return;

        // ponytail 2026-08-26: per-widget crop specialization restored from the
        // pre-PropertyPanel settings dialogs (git 552fc24^). b.CropShape + Width/
        // Height mirror each widget's real fill area: zone = window rect, digital
        // clock = 320×140 rect, analog clock = 200×200 circle, calendar/note/panel
        // = their live size rect. ImageCropPreviewWindow owns drag/zoom/opacity.
        var crop = new ImageCropPreviewWindow(
            path,
            b.GetWidth(), b.GetHeight(),
            b.GetOffsetX(), b.GetOffsetY(),
            b.GetZoom(), b.GetOpacity(),
            b.CropShape, b.TitleBarHeight, b.TitleBarInnerDividerHeights,
            b.SetZoneSize)
        {
            Owner = owner
        };
        if (crop.ShowDialog() != true) return;

        if (crop.Result is { } r)
        {
            b.SetOffsetX(r.OffsetX);
            b.SetOffsetY(r.OffsetY);
            b.SetZoom(r.Zoom);
            b.SetOpacity(r.Opacity);
        }
        b.OnSave();
    }
}