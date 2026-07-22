using System;
using System.Collections.Generic;
using System.ComponentModel;
using System.Runtime.CompilerServices;

namespace DesktopZones.Services;

public enum Language { Chinese, English }

public class LocalizationService : INotifyPropertyChanged
{
    private static readonly Lazy<LocalizationService> _instance = new(() => new LocalizationService());
    public static LocalizationService Instance => _instance.Value;

    private readonly Dictionary<string, Dictionary<Language, string>> _strings = new()
    {
        ["App.Title"] = new() { [Language.Chinese] = "DeskOrder", [Language.English] = "DeskOrder" },
        ["App.TrayTooltip"] = new() { [Language.Chinese] = "DeskOrder - 秩序桌面", [Language.English] = "DeskOrder - Desktop Order" },

        ["Tray.ShowAll"] = new() { [Language.Chinese] = "显示所有分区", [Language.English] = "Show All Zones" },
        ["Tray.HideAll"] = new() { [Language.Chinese] = "隐藏所有分区", [Language.English] = "Hide All Zones" },
        ["Tray.NewZone"] = new() { [Language.Chinese] = "新建分区", [Language.English] = "New Zone" },
        ["Tray.Manage"] = new() { [Language.Chinese] = "管理分区...", [Language.English] = "Manage Zones..." },
        ["Tray.Exit"] = new() { [Language.Chinese] = "退出", [Language.English] = "Exit" },

        ["Zone.Hide"] = new() { [Language.Chinese] = "最小化分区", [Language.English] = "Minimize Zone" },
        ["Zone.Edit"] = new() { [Language.Chinese] = "编辑分区", [Language.English] = "Edit Zone" },
        ["Zone.Delete"] = new() { [Language.Chinese] = "删除分区", [Language.English] = "Delete Zone" },
        ["Zone.Import"] = new() { [Language.Chinese] = "导入文件...", [Language.English] = "Import Files..." },
        ["Zone.New"] = new() { [Language.Chinese] = "新建", [Language.English] = "New" },
        ["Zone.ImportTitle"] = new() { [Language.Chinese] = "选择要导入的文件或快捷方式", [Language.English] = "Select files or shortcuts to import" },
        ["Zone.ImportFiles"] = new() { [Language.Chinese] = "导入文件...", [Language.English] = "Import Files..." },
        ["Zone.ImportFolder"] = new() { [Language.Chinese] = "导入文件夹...", [Language.English] = "Import Folder..." },
        ["Zone.NewFolder"] = new() { [Language.Chinese] = "新建文件夹...", [Language.English] = "New Folder..." },
        ["Zone.NewTxt"] = new() { [Language.Chinese] = "文本文档 (.txt)", [Language.English] = "Text Document (.txt)" },
        ["Zone.NewDocx"] = new() { [Language.Chinese] = "Word 文档 (.docx)", [Language.English] = "Word Document (.docx)" },
        ["Zone.NewPptx"] = new() { [Language.Chinese] = "PowerPoint (.pptx)", [Language.English] = "PowerPoint (.pptx)" },
        ["Zone.NewXlsx"] = new() { [Language.Chinese] = "Excel 工作簿 (.xlsx)", [Language.English] = "Excel Workbook (.xlsx)" },
        ["Zone.DragHint"] = new() { [Language.Chinese] = "拖拽此处移动分区", [Language.English] = "Drag to move zone" },

        ["Item.Open"] = new() { [Language.Chinese] = "打开", [Language.English] = "Open" },
        ["Item.OpenLocation"] = new() { [Language.Chinese] = "打开文件位置", [Language.English] = "Open File Location" },
        ["Item.Rename"] = new() { [Language.Chinese] = "重命名", [Language.English] = "Rename" },
        ["Item.Delete"] = new() { [Language.Chinese] = "删除", [Language.English] = "Delete" },
        ["Item.FailedToOpen"] = new() { [Language.Chinese] = "无法打开", [Language.English] = "Failed to open" },

        ["Manage.Title"] = new() { [Language.Chinese] = "DeskOrder - 管理分区", [Language.English] = "DeskOrder - Manage Zones" },
        ["Manage.EmptyHint"] = new() { [Language.Chinese] = "还没有分区。点击「+ 新建分区」开始使用。", [Language.English] = "No zones created yet. Click '+ New Zone' to get started." },
        ["Manage.Items"] = new() { [Language.Chinese] = "项目", [Language.English] = "Items" },
        ["Manage.StartWithWindows"] = new() { [Language.Chinese] = "开机启动", [Language.English] = "Start with Windows" },
        ["Manage.NewZone"] = new() { [Language.Chinese] = "+ 新建分区", [Language.English] = "+ New Zone" },
        ["Manage.ShowAll"] = new() { [Language.Chinese] = "显示全部", [Language.English] = "Show All" },
        ["Manage.HideAll"] = new() { [Language.Chinese] = "隐藏全部", [Language.English] = "Hide All" },

        ["Settings.Title"] = new() { [Language.Chinese] = "分区设置", [Language.English] = "Zone Settings" },
        ["Settings.Name"] = new() { [Language.Chinese] = "分区名称", [Language.English] = "Zone Name" },
        ["Settings.Width"] = new() { [Language.Chinese] = "宽度", [Language.English] = "Width" },
        ["Settings.Height"] = new() { [Language.Chinese] = "高度", [Language.English] = "Height" },
        ["Settings.GridSize"] = new() { [Language.Chinese] = "网格大小", [Language.English] = "Grid Size" },
        ["Settings.SnapToGrid"] = new() { [Language.Chinese] = "吸附到网格", [Language.English] = "Snap to Grid" },
        ["Settings.BorderThickness"] = new() { [Language.Chinese] = "边框粗细", [Language.English] = "Border Thickness" },
        ["Settings.BorderColor"] = new() { [Language.Chinese] = "边框颜色", [Language.English] = "Border Color" },
        ["Settings.Icon"] = new() { [Language.Chinese] = "分区图标", [Language.English] = "Zone Icon" },
        ["Settings.FillColor"] = new() { [Language.Chinese] = "内部填充色", [Language.English] = "Fill Color" },
        ["Settings.BgImage"] = new() { [Language.Chinese] = "背景图片", [Language.English] = "Background Image" },
        ["Settings.BrowseBg"] = new() { [Language.Chinese] = "选择背景图片", [Language.English] = "Select Background Image" },
        ["Settings.Save"] = new() { [Language.Chinese] = "保存", [Language.English] = "Save" },
        ["Settings.Apply"] = new() { [Language.Chinese] = "应用", [Language.English] = "Apply" },
        ["Settings.Cancel"] = new() { [Language.Chinese] = "取消", [Language.English] = "Cancel" },
        ["Settings.NameEmpty"] = new() { [Language.Chinese] = "分区名称不能为空。", [Language.English] = "Zone name cannot be empty." },
        ["Settings.WidthRange"] = new() { [Language.Chinese] = "宽度必须在 100 到 4000 之间。", [Language.English] = "Width must be between 100 and 4000." },
        ["Settings.HeightRange"] = new() { [Language.Chinese] = "高度必须在 100 到 4000 之间。", [Language.English] = "Height must be between 100 and 4000." },
        ["Settings.GridRange"] = new() { [Language.Chinese] = "网格大小必须在 32 到 256 之间。", [Language.English] = "Grid size must be between 32 and 256." },
        ["Settings.BorderRange"] = new() { [Language.Chinese] = "边框粗细必须在 0.5 到 10 之间。", [Language.English] = "Border thickness must be between 0.5 and 10." },
        ["Settings.ValidationError"] = new() { [Language.Chinese] = "验证错误", [Language.English] = "Validation Error" },

        ["Dialog.DeleteZoneTitle"] = new() { [Language.Chinese] = "删除分区", [Language.English] = "Delete Zone" },
        ["Dialog.DeleteZoneMsg"] = new() { [Language.Chinese] = "确定要删除分区「{0}」吗？\n\n这会移除该分区及其内部所有项目。此操作不可撤销。", [Language.English] = "Delete zone \"{0}\"?\n\nThis will remove the zone and all items within it. This cannot be undone." },
        ["Dialog.Yes"] = new() { [Language.Chinese] = "是", [Language.English] = "Yes" },
        ["Dialog.No"] = new() { [Language.Chinese] = "否", [Language.English] = "No" },

        ["Menu.Language"] = new() { [Language.Chinese] = "语言 / Language", [Language.English] = "Language / 语言" },
        ["Menu.Chinese"] = new() { [Language.Chinese] = "中文", [Language.English] = "中文" },
        ["Menu.English"] = new() { [Language.Chinese] = "English", [Language.English] = "English" },

        ["Import.Files"] = new() { [Language.Chinese] = "导入文件...", [Language.English] = "Import Files..." },
        ["Import.Folder"] = new() { [Language.Chinese] = "导入文件夹...", [Language.English] = "Import Folder..." },
        ["Import.SelectFolder"] = new() { [Language.Chinese] = "选择文件夹", [Language.English] = "Select Folder" },
        ["Rename.Title"] = new() { [Language.Chinese] = "重命名", [Language.English] = "Rename" },
        ["Rename.Ok"] = new() { [Language.Chinese] = "确定", [Language.English] = "OK" },
        ["Rename.Cancel"] = new() { [Language.Chinese] = "取消", [Language.English] = "Cancel" },

        // ── Sticky Notes ──
        ["Tray.NewNote"] = new() { [Language.Chinese] = "新建便签", [Language.English] = "New Note" },
        ["Note.DefaultTitle"] = new() { [Language.Chinese] = "便签", [Language.English] = "Note" },
        ["Note.PinTop"] = new() { [Language.Chinese] = "置顶", [Language.English] = "Pin Top" },
        ["Note.Unpin"] = new() { [Language.Chinese] = "取消置顶", [Language.English] = "Unpin" },
        ["Note.Delete"] = new() { [Language.Chinese] = "删除便签", [Language.English] = "Delete Note" },
        ["Note.DeleteConfirm"] = new() { [Language.Chinese] = "确定要删除便签「{0}」吗？", [Language.English] = "Delete note \"{0}\"?" },

        // ── Desktop Clock ──
        ["Tray.NewClock"] = new() { [Language.Chinese] = "新建时钟", [Language.English] = "New Clock" },
        ["Clock.DigitalMode"] = new() { [Language.Chinese] = "数字模式", [Language.English] = "Digital Mode" },
        ["Clock.AnalogMode"] = new() { [Language.Chinese] = "钟表模式", [Language.English] = "Analog Mode" },
        ["Clock.ShowSeconds"] = new() { [Language.Chinese] = "显示秒", [Language.English] = "Show Seconds" },
        ["Clock.HideSeconds"] = new() { [Language.Chinese] = "隐藏秒", [Language.English] = "Hide Seconds" },
        ["Clock.Format24h"] = new() { [Language.Chinese] = "24小时制", [Language.English] = "24-Hour" },
        ["Clock.Format12h"] = new() { [Language.Chinese] = "12小时制", [Language.English] = "12-Hour" },
        ["Clock.Delete"] = new() { [Language.Chinese] = "删除时钟", [Language.English] = "Delete Clock" },

        // ── Desktop Calendar ──
        ["Tray.NewCalendar"] = new() { [Language.Chinese] = "新建日历", [Language.English] = "New Calendar" },
        ["Calendar.Today"] = new() { [Language.Chinese] = "今天", [Language.English] = "Today" },
        ["Calendar.AddNote"] = new() { [Language.Chinese] = "添加备注", [Language.English] = "Add Note" },
        ["Calendar.EditNote"] = new() { [Language.Chinese] = "编辑备注", [Language.English] = "Edit Note" },
        ["Calendar.DeleteNote"] = new() { [Language.Chinese] = "删除备注", [Language.English] = "Delete Note" },
        ["Calendar.NoteHint"] = new() { [Language.Chinese] = "输入备注内容...", [Language.English] = "Enter note..." },
        ["Calendar.Delete"] = new() { [Language.Chinese] = "删除日历", [Language.English] = "Delete Calendar" },

        // ── Global Appearance ──
        ["Appearance.Title"] = new() { [Language.Chinese] = "全局外观设置", [Language.English] = "Global Appearance" },
        ["Appearance.EnableAcrylic"] = new() { [Language.Chinese] = "启用毛玻璃效果", [Language.English] = "Enable Acrylic Blur" },
        ["Appearance.BorderColor"] = new() { [Language.Chinese] = "边框颜色", [Language.English] = "Border Color" },
        ["Appearance.FillColor"] = new() { [Language.Chinese] = "填充颜色", [Language.English] = "Fill Color" },
        ["Appearance.BorderThickness"] = new() { [Language.Chinese] = "边框粗细", [Language.English] = "Border Thickness" },
        ["Appearance.Apply"] = new() { [Language.Chinese] = "应用到所有", [Language.English] = "Apply to All" },
        ["Appearance.GlassPreset"] = new() { [Language.Chinese] = "玻璃效果预设", [Language.English] = "Glass Preset" },
        ["Appearance.GlassNone"] = new() { [Language.Chinese] = "关闭", [Language.English] = "Off" },
        ["Appearance.GlassLight"] = new() { [Language.Chinese] = "轻度", [Language.English] = "Light" },
        ["Appearance.GlassStandard"] = new() { [Language.Chinese] = "标准", [Language.English] = "Standard" },
        ["Appearance.GlassHeavy"] = new() { [Language.Chinese] = "重度", [Language.English] = "Heavy" },

        // ── Note Hotkey ──
        ["Note.Hotkey"] = new() { [Language.Chinese] = "快捷键", [Language.English] = "Hotkey" },
        ["Note.HotkeyDisabled"] = new() { [Language.Chinese] = "未设置", [Language.English] = "None" },
        ["Note.SetHotkey"] = new() { [Language.Chinese] = "设置快捷键", [Language.English] = "Set Hotkey" },

        // ── Management Window UI ──
        ["Manage.ShowBtn"] = new() { [Language.Chinese] = "显示", [Language.English] = "Show" },
        ["Manage.HideBtn"] = new() { [Language.Chinese] = "隐藏", [Language.English] = "Hide" },
        ["Manage.DeleteBtn"] = new() { [Language.Chinese] = "删除", [Language.English] = "Del" },
        ["Manage.HotkeySet"] = new() { [Language.Chinese] = "切换", [Language.English] = "Set" },
        ["Manage.MinimizeAll"] = new() { [Language.Chinese] = "最小化全部", [Language.English] = "Minimize All" },
        ["Manage.FullHideAll"] = new() { [Language.Chinese] = "全部隐藏", [Language.English] = "Full Hide All" },
        ["Manage.Notes"] = new() { [Language.Chinese] = "便签", [Language.English] = "Notes" },
        ["Manage.Clocks"] = new() { [Language.Chinese] = "时钟", [Language.English] = "Clocks" },
        ["Manage.Calendars"] = new() { [Language.Chinese] = "日历", [Language.English] = "Calendars" },
        ["Manage.Appearance"] = new() { [Language.Chinese] = "外观设置", [Language.English] = "Appearance" },
        ["Manage.NewNote"] = new() { [Language.Chinese] = "+ 便签", [Language.English] = "+ Note" },
        ["Manage.NewClock"] = new() { [Language.Chinese] = "+ 时钟", [Language.English] = "+ Clock" },
        ["Manage.NewCalendar"] = new() { [Language.Chinese] = "+ 日历", [Language.English] = "+ Calendar" },
        ["Manage.Zones"] = new() { [Language.Chinese] = "分区", [Language.English] = "Zones" },

        // ── Zone Merge ──
        ["Merge.Title"] = new() { [Language.Chinese] = "合并分区", [Language.English] = "Merge Zones" },
        ["Merge.SelectTarget"] = new() { [Language.Chinese] = "选择要合并的目标分区:", [Language.English] = "Select a zone to merge with:" },
        ["Merge.MergeBtn"] = new() { [Language.Chinese] = "🔗 合并", [Language.English] = "🔗 Merge" },
        ["Merge.MergedLabel"] = new() { [Language.Chinese] = "已合并", [Language.English] = "Merged" },
        ["Merge.DisbandAll"] = new() { [Language.Chinese] = "全部分离", [Language.English] = "Disband All" },
        ["Merge.DisbandThis"] = new() { [Language.Chinese] = "分离当前", [Language.English] = "Disband This" },
        ["Merge.DisbandGroup"] = new() { [Language.Chinese] = "分离", [Language.English] = "Disband" },
        ["Merge.ConfirmDisband"] = new() { [Language.Chinese] = "确定要分离合并的分区吗？所有分区将恢复为独立窗口。", [Language.English] = "Disband the merged group? All zones will return to individual windows." },
        ["Merge.NoTargets"] = new() { [Language.Chinese] = "没有可合并的分区。", [Language.English] = "No zones available to merge." },
        ["Merge.SwitchTooltip"] = new() { [Language.Chinese] = "点击切换到此分区", [Language.English] = "Click to switch to this zone" },
        ["Merge.CurrentTab"] = new() { [Language.Chinese] = "当前: ", [Language.English] = "Current: " },

        // ── Panel ──
        ["Manage.NewPanel"] = new() { [Language.Chinese] = "+ 面板", [Language.English] = "+ Panel" },
        ["Manage.PanelOpen"] = new() { [Language.Chinese] = "面板已打开", [Language.English] = "Panel Open" },
        ["Manage.PanelClose"] = new() { [Language.Chinese] = "关闭面板", [Language.English] = "Close Panel" },
        ["Panel.Title"] = new() { [Language.Chinese] = "面板", [Language.English] = "Panel" },
        ["Panel.Import"] = new() { [Language.Chinese] = "导入", [Language.English] = "Import" },
        ["Panel.ImportFiles"] = new() { [Language.Chinese] = "导入文件...", [Language.English] = "Import Files..." },
        ["Panel.ImportFolder"] = new() { [Language.Chinese] = "导入文件夹...", [Language.English] = "Import Folder..." },
        ["Panel.New"] = new() { [Language.Chinese] = "新建", [Language.English] = "New" },
        ["Panel.NewFolder"] = new() { [Language.Chinese] = "新建文件夹...", [Language.English] = "New Folder..." },
        ["Panel.NewTxt"] = new() { [Language.Chinese] = "文本文档 (.txt)", [Language.English] = "Text Document (.txt)" },
        ["Panel.NewDocx"] = new() { [Language.Chinese] = "Word 文档 (.docx)", [Language.English] = "Word Document (.docx)" },
        ["Panel.NewPptx"] = new() { [Language.Chinese] = "PowerPoint (.pptx)", [Language.English] = "PowerPoint (.pptx)" },
        ["Panel.NewXlsx"] = new() { [Language.Chinese] = "Excel 工作表 (.xlsx)", [Language.English] = "Excel Worksheet (.xlsx)" },
        ["Panel.Hide"] = new() { [Language.Chinese] = "隐藏面板", [Language.English] = "Hide Panel" },
        ["Panel.Search"] = new() { [Language.Chinese] = "搜索...", [Language.English] = "Search..." },
        ["Panel.Settings"] = new() { [Language.Chinese] = "面板设置", [Language.English] = "Panel Settings" },
        ["ToolTip.HidePanel"] = new() { [Language.Chinese] = "隐藏面板", [Language.English] = "Hide Panel" },
        ["ToolTip.Language"] = new() { [Language.Chinese] = "语言 / Language", [Language.English] = "Language / 语言" },
        ["ToolTip.Show"] = new() { [Language.Chinese] = "显示", [Language.English] = "Show" },
        ["ToolTip.Minimize"] = new() { [Language.Chinese] = "最小化", [Language.English] = "Minimize" },
        ["ToolTip.Hide"] = new() { [Language.Chinese] = "隐藏", [Language.English] = "Hide" },
        ["ToolTip.Merge"] = new() { [Language.Chinese] = "合并", [Language.English] = "Merge" },
        ["ToolTip.NewPanel"] = new() { [Language.Chinese] = "新建面板", [Language.English] = "New Panel" },
        ["ToolTip.NewNote"] = new() { [Language.Chinese] = "新建便签", [Language.English] = "New Note" },
        ["ToolTip.NewClock"] = new() { [Language.Chinese] = "新建时钟", [Language.English] = "New Clock" },
        ["ToolTip.NewCalendar"] = new() { [Language.Chinese] = "新建日历", [Language.English] = "New Calendar" },
        ["ToolTip.AlignGrid"] = new() { [Language.Chinese] = "对齐网格", [Language.English] = "Align to Grid" },
        ["ToolTip.Import"] = new() { [Language.Chinese] = "导入", [Language.English] = "Import" },

        // ── Glass Preset in Zone Settings ──
        ["Settings.AcrylicBlur"] = new() { [Language.Chinese] = "毛玻璃效果", [Language.English] = "Acrylic Blur" },
        ["Settings.GlassIntensity"] = new() { [Language.Chinese] = "玻璃强度", [Language.English] = "Glass Intensity" },
        ["Appearance.Glassmorphism"] = new() { [Language.Chinese] = "玻璃拟态", [Language.English] = "Glassmorphism" },
        ["Appearance.VisionOS"] = new() { [Language.Chinese] = "空间玻璃", [Language.English] = "VisionOS" },
        ["Appearance.LiquidGlass"] = new() { [Language.Chinese] = "液态玻璃", [Language.English] = "Liquid Glass" },
        ["Appearance.BgImage"] = new() { [Language.Chinese] = "背景图片", [Language.English] = "Background Image" },
        ["Appearance.FillOpacity"] = new() { [Language.Chinese] = "填充透明度", [Language.English] = "Fill Opacity" },
        ["Appearance.Save"] = new() { [Language.Chinese] = "保存", [Language.English] = "Save" },

        // ── Note Formatting Toolbar ──
        ["Note.Toolbar.Hide"] = new() { [Language.Chinese] = "隐藏", [Language.English] = "Hide" },
        ["Note.Hotkey.Record"] = new() { [Language.Chinese] = "录制快捷键", [Language.English] = "Record Hotkey" },
        ["Note.Hotkey.Clear"] = new() { [Language.Chinese] = "清除", [Language.English] = "Clear" },
        ["Note.Hotkey.Current"] = new() { [Language.Chinese] = "当前快捷键:", [Language.English] = "Current:" },
        ["Note.Hotkey.PressKeys"] = new() { [Language.Chinese] = "请按键...", [Language.English] = "Press keys..." },
        ["Note.Hotkey.Settings"] = new() { [Language.Chinese] = "快捷键设置", [Language.English] = "Hotkey Settings" },
        ["Note.Hotkey.Apply"] = new() { [Language.Chinese] = "应用快捷键", [Language.English] = "Apply Hotkey" },

        // ── Panel Hotkey ──
        ["Panel.Hotkey"] = new() { [Language.Chinese] = "面板快捷键", [Language.English] = "Panel Hotkey" },
        ["Panel.HotkeyDisabled"] = new() { [Language.Chinese] = "未设置", [Language.English] = "None" },

        // ── Image Crop Preview ──
        ["CropPreview.Title"] = new() { [Language.Chinese] = "图片裁剪预览", [Language.English] = "Image Crop Preview" },
        ["CropPreview.Confirm"] = new() { [Language.Chinese] = "确认", [Language.English] = "Confirm" },
        ["CropPreview.Cancel"] = new() { [Language.Chinese] = "取消", [Language.English] = "Cancel" },
        ["CropPreview.Reset"] = new() { [Language.Chinese] = "重置", [Language.English] = "Reset" },
        ["CropPreview.Zoom"] = new() { [Language.Chinese] = "缩放", [Language.English] = "Zoom" },
        ["CropPreview.Opacity"] = new() { [Language.Chinese] = "透明度", [Language.English] = "Opacity" },
        ["CropPreview.Stretch"] = new() { [Language.Chinese] = "拉伸模式", [Language.English] = "Stretch Mode" },
    };

    private Language _currentLanguage = Language.Chinese;
    public Language CurrentLanguage
    {
        get => _currentLanguage;
        set
        {
            if (_currentLanguage != value)
            {
                _currentLanguage = value;
                OnPropertyChanged();
                LanguageChanged?.Invoke(value);
            }
        }
    }

    public event Action<Language>? LanguageChanged;

    public string Get(string key)
    {
        if (_strings.TryGetValue(key, out var dict) && dict.TryGetValue(_currentLanguage, out var value))
            return value;
        return key;
    }

    public string Get(string key, params object[] args)
    {
        var template = Get(key);
        return string.Format(template, args);
    }

    public string this[string key] => Get(key);

    public void ToggleLanguage()
    {
        CurrentLanguage = _currentLanguage == Language.Chinese ? Language.English : Language.Chinese;
    }

    public event PropertyChangedEventHandler? PropertyChanged;
    protected void OnPropertyChanged([CallerMemberName] string? name = null)
    {
        PropertyChanged?.Invoke(this, new PropertyChangedEventArgs(name));
    }
}
