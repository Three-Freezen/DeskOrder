using System;
using System.Collections.Generic;
using System.Globalization;
using System.Linq;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Input;
using System.Windows.Media;
using DeskOrder.PluginApi;
using DesktopZones.Helpers;
using DesktopZones.Services;

namespace DesktopZones.Views;

/// <summary>
/// 「插件设置…」窗口（API v5）：把已启用插件通过 <c>host.Settings.Register(...)</c> 声明的
/// schema 全部渲染出来（插件名 + 标题 → 各字段按 <see cref="PluginSettingKind"/> 选控件），
/// 点「保存」后统一校验、写插件级存储、回调 <see cref="PluginSettingsSchema.Changed"/> 并关闭。
///
/// 只读展示 + 编辑，不做业务：值的合法范围由插件在 schema 里声明，宿主负责校验与落盘。
/// 打开时对 schema 集合取快照；打开期间插件被卸载不会让窗口崩（保存写的是旧 entry 的存储）。
/// </summary>
public partial class PluginSettingsDialog : Window
{
    static readonly LocalizationService Loc = LocalizationService.Instance;

    /// <summary>一个字段的控件 + 取值委托（保存时用它把控件值收回来）。</summary>
    sealed class FieldBinding
    {
        public required PluginSettingsEntry Entry { get; init; }
        public required PluginSettingField Field { get; init; }
        public required Func<string> Read { get; init; }
        /// <summary>Int 失焦校验的错误行（其它 Kind 为 null）。</summary>
        public TextBlock? ErrorText { get; init; }
    }

    readonly List<FieldBinding> _bindings = new();
    readonly List<string> _sectionHeaders = new();
    readonly List<string> _fieldLabels = new();

    public PluginSettingsDialog()
    {
        InitializeComponent();
        BuildSections();
        var hasAny = _bindings.Count > 0 || _sectionHeaders.Count > 0;
        EmptyText.Visibility = hasAny ? Visibility.Collapsed : Visibility.Visible;
        ScrollHost.Visibility = hasAny ? Visibility.Visible : Visibility.Collapsed;
        SaveButton.IsEnabled = hasAny;
    }

    // ── 构建 ──

    void BuildSections()
    {
        SectionsHost.Children.Clear();
        _bindings.Clear();
        _sectionHeaders.Clear();
        _fieldLabels.Clear();

        var language = Loc.CurrentLanguage;
        var entries = PluginSettingsService.Instance.Schemas;
        if (entries.Count == 0)
        {
            SectionsHost.Children.Add(new TextBlock
            {
                Text = Loc["Plugin.Settings.Empty"],
                FontSize = 12,
                TextWrapping = TextWrapping.Wrap,
                TextAlignment = TextAlignment.Center,
                HorizontalAlignment = HorizontalAlignment.Center,
                Margin = new Thickness(24, 40, 24, 40),
                Foreground = ThemeBrushes.TextTertiaryModern
            });
            return;
        }

        var valuesCache = new Dictionary<PluginSettingsEntry, Dictionary<string, string>>();
        foreach (var entry in entries)
            valuesCache[entry] = PluginSettingsService.Instance.ReadValues(entry);

        foreach (var entry in entries)
        {
            var section = new StackPanel { Margin = new Thickness(0, 0, 0, 18) };

            var header = entry.DisplayPluginName + " · " + entry.DisplayTitle;
            section.Children.Add(new TextBlock
            {
                Text = header,
                FontSize = 13,
                FontWeight = FontWeights.SemiBold,
                Foreground = ThemeBrushes.TextPrimaryModern,
                TextWrapping = TextWrapping.Wrap,
                Margin = new Thickness(0, 0, 0, 8)
            });
            _sectionHeaders.Add(header);

            var values = valuesCache[entry];
            foreach (var field in entry.Schema.Fields)
            {
                var value = values.TryGetValue(field.Key, out var stored) ? stored : field.DefaultValue ?? "";
                section.Children.Add(BuildField(entry, field, value, language));
            }

            SectionsHost.Children.Add(section);
        }
    }

    FrameworkElement BuildField(PluginSettingsEntry entry, PluginSettingField field, string value, string language)
    {
        var host = new StackPanel { Margin = new Thickness(0, 2, 0, 10) };
        var label = field.Label.Get(language);
        _fieldLabels.Add(label);

        Func<string> read;

        switch (field.Kind)
        {
            case PluginSettingKind.Toggle:
            {
                var check = new CheckBox
                {
                    Content = label,
                    FontSize = 12,
                    Cursor = Cursors.Hand,
                    IsChecked = string.Equals(value, "true", StringComparison.OrdinalIgnoreCase),
                    Foreground = ThemeBrushes.TextPrimaryModern
                };
                host.Children.Add(check);
                read = () => check.IsChecked == true ? "true" : "false";
                break;
            }

            case PluginSettingKind.Int:
            {
                host.Children.Add(BuildLabelRow(label, field.Kind));
                var box = BuildTextBox(value);
                var error = BuildHintText("", ThemeBrushes.DangerModern, bold: false);
                error.Visibility = Visibility.Collapsed;
                // 失焦即校验：范围/整数错误立刻提示，不用等到点保存。
                box.LostFocus += (_, _) =>
                {
                    if (TryValidateInt(field, box.Text, out var normalized, out var message))
                    {
                        box.Text = normalized;
                        error.Visibility = Visibility.Collapsed;
                    }
                    else
                    {
                        error.Text = message ?? "";
                        error.Visibility = Visibility.Visible;
                    }
                };
                host.Children.Add(box);
                host.Children.Add(error);
                read = () => box.Text;
                _bindings.Add(new FieldBinding { Entry = entry, Field = field, Read = read, ErrorText = error });
                AddOptionalHint(host, field, language);
                return host;
            }

            case PluginSettingKind.Choice:
            {
                host.Children.Add(BuildLabelRow(label, field.Kind));
                var combo = ComboBoxHelper.Create(height: 28, fontSize: 12);
                combo.HorizontalAlignment = HorizontalAlignment.Left;
                combo.MinWidth = 220;
                var choices = field.Choices ?? new List<PluginSettingChoice>();
                int selected = 0;
                for (int i = 0; i < choices.Count; i++)
                {
                    var choice = choices[i];
                    combo.Items.Add(new ComboBoxItem
                    {
                        Content = choice.Label.Get(language),
                        Tag = choice.Value
                    });
                    if (string.Equals(choice.Value, value, StringComparison.Ordinal)) selected = i;
                }
                combo.SelectedIndex = choices.Count == 0 ? -1 : Math.Clamp(selected, 0, choices.Count - 1);
                host.Children.Add(combo);
                read = () => (combo.SelectedItem as ComboBoxItem)?.Tag as string ?? "";
                break;
            }

            default:   // Text
            {
                host.Children.Add(BuildLabelRow(label, field.Kind));
                var box = BuildTextBox(value);
                host.Children.Add(box);
                read = () => box.Text;
                break;
            }
        }

        _bindings.Add(new FieldBinding { Entry = entry, Field = field, Read = read });
        AddOptionalHint(host, field, language);
        return host;
    }

    /// <summary>字段标签 + 小号类型说明（让作者/用户一眼看出这是开关还是整数）。</summary>
    static FrameworkElement BuildLabelRow(string label, PluginSettingKind kind)
    {
        var row = new StackPanel { Orientation = Orientation.Horizontal, Margin = new Thickness(0, 0, 0, 4) };
        row.Children.Add(new TextBlock
        {
            Text = label,
            FontSize = 12,
            Foreground = ThemeBrushes.TextSecondaryModern,
            VerticalAlignment = VerticalAlignment.Center
        });
        row.Children.Add(new TextBlock
        {
            Text = KindLabel(kind),
            FontSize = 10,
            Margin = new Thickness(8, 1, 0, 0),
            Foreground = ThemeBrushes.TextTertiaryModern,
            VerticalAlignment = VerticalAlignment.Center
        });
        return row;
    }

    static string KindLabel(PluginSettingKind kind) => kind switch
    {
        PluginSettingKind.Toggle => Loc["Plugin.Settings.Kind.Toggle"],
        PluginSettingKind.Int => Loc["Plugin.Settings.Kind.Int"],
        PluginSettingKind.Choice => Loc["Plugin.Settings.Kind.Choice"],
        _ => Loc["Plugin.Settings.Kind.Text"]
    };

    static TextBox BuildTextBox(string value) => new()
    {
        Text = value,
        Height = 28,
        FontSize = 12,
        Padding = new Thickness(8, 0, 8, 0),
        VerticalContentAlignment = VerticalAlignment.Center,
        Foreground = ThemeBrushes.TextPrimaryModern,
        Background = Application.Current?.TryFindResource("Brush.Bg.Input") as Brush
                     ?? ThemeBrushes.BgSurfaceModern,
        BorderBrush = ThemeBrushes.BorderSubtleModern,
        BorderThickness = new Thickness(1)
    };

    static TextBlock BuildHintText(string text, Brush brush, bool bold)
    {
        var block = new TextBlock
        {
            Text = text,
            FontSize = 10,
            TextWrapping = TextWrapping.Wrap,
            Margin = new Thickness(0, 3, 0, 0),
            Foreground = brush
        };
        if (bold) block.FontWeight = FontWeights.SemiBold;
        return block;
    }

    /// <summary>schema 里的可选说明（Hint），显示在控件下方。</summary>
    static void AddOptionalHint(StackPanel host, PluginSettingField field, string language)
    {
        var hint = field.Hint?.Get(language);
        if (string.IsNullOrWhiteSpace(hint)) return;
        host.Children.Add(BuildHintText(hint!, ThemeBrushes.TextTertiaryModern, bold: false));
    }

    /// <summary>Int 校验（与 <see cref="PluginSettingsService"/> 的保存规则保持一致）。</summary>
    static bool TryValidateInt(PluginSettingField field, string text, out string normalized, out string? message)
    {
        message = null;
        var label = field.Label.Get(Loc.CurrentLanguage);
        if (!int.TryParse(text, NumberStyles.Integer, CultureInfo.InvariantCulture, out var number))
        {
            normalized = text;
            message = Loc.Get("Plugin.Settings.InvalidInt", label);
            return false;
        }
        if (field.Min.HasValue && number < field.Min.Value)
        {
            normalized = text;
            message = Loc.Get("Plugin.Settings.IntBelowMin", label, field.Min.Value);
            return false;
        }
        if (field.Max.HasValue && number > field.Max.Value)
        {
            normalized = text;
            message = Loc.Get("Plugin.Settings.IntAboveMax", label, field.Max.Value);
            return false;
        }
        normalized = number.ToString(CultureInfo.InvariantCulture);
        return true;
    }

    // ── 保存 / 取消 ──

    void Save_Click(object sender, RoutedEventArgs e)
    {
        StatusText.Visibility = Visibility.Collapsed;

        // 先按 entry 分组收集控件值，再逐个 entry 交给服务校验 + 落盘（服务保证整块原子性）。
        var grouped = new Dictionary<PluginSettingsEntry, Dictionary<string, string>>();
        foreach (var binding in _bindings)
        {
            if (!grouped.TryGetValue(binding.Entry, out var values))
            {
                values = new Dictionary<string, string>(StringComparer.Ordinal);
                grouped[binding.Entry] = values;
            }
            values[binding.Field.Key] = binding.Read();
        }

        foreach (var entry in PluginSettingsService.Instance.Schemas)
        {
            if (!grouped.TryGetValue(entry, out var values)) values = new Dictionary<string, string>();
            if (PluginSettingsService.Instance.Save(entry, values, out var error)) continue;
            ShowStatus(error ?? Loc.Get("Plugin.Settings.SaveFailed", ""));
            return;
        }

        StatusText.Text = Loc["Plugin.Settings.Saved"];
        StatusText.Visibility = Visibility.Visible;
        DialogResult = true;
        Close();
    }

    void ShowStatus(string text)
    {
        StatusText.Text = text;
        StatusText.Foreground = ThemeBrushes.DangerModern;
        StatusText.Visibility = Visibility.Visible;
    }

    void Cancel_Click(object sender, RoutedEventArgs e)
    {
        DialogResult = false;
        Close();
    }

    void TitleBar_MouseLeftButtonDown(object sender, MouseButtonEventArgs e)
    {
        if (e.ButtonState == MouseButtonState.Pressed) DragMove();
    }

    // ── 自测台入口（App.PluginLab.PluginUi.cs）──

    /// <summary>渲染出的插件设置块数（= 打开时注册了 schema 的插件数）。</summary>
    internal int SectionCount => _sectionHeaders.Count;

    /// <summary>渲染出的字段控件数。</summary>
    internal int FieldCount => _bindings.Count;

    /// <summary>各设置块的标题（插件名 · 标题，按当前语言解析）。</summary>
    internal IReadOnlyList<string> SectionHeadersForLab => _sectionHeaders;

    /// <summary>各字段标签（按当前语言解析）。</summary>
    internal IReadOnlyList<string> FieldLabelsForLab => _fieldLabels;

    /// <summary>空态是否可见。</summary>
    internal Visibility EmptyStateVisibility => EmptyText.Visibility;

    /// <summary>保存按钮是否可用。</summary>
    internal bool SaveEnabled => SaveButton.IsEnabled;
}
