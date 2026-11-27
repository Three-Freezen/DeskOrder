using System;
using System.Collections.Generic;
using System.Linq;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Input;
using System.Windows.Media;
using DeskOrder.PluginApi;
using DesktopZones.Helpers;
using DesktopZones.Models;
using DesktopZones.Services;
using DesktopZones.Views.Pages;

namespace DesktopZones.Views;

/// <summary>
/// 「全部更新」的聚合确认框（v1.2.3）：一次列出所有待更新插件，用户确认后顺序执行。
///
/// 与单条更新确认的区别：这里没有「逐个勾选联网同意」的能力，所以新增 <c>network</c> 权限的
/// 条目**一律不进入批量**，只在对话框里标记「需逐个确认」并跳过 —— 联网权限必须像单条更新
/// 那样由用户在 <see cref="PluginUpdateDialog"/> 里显式勾选，批量不能成为绕过安全闸门的入口。
/// 新增其它权限 / 新增能力只做提示，不阻断（与单条更新策略一致）。
/// </summary>
public partial class PluginBatchUpdateDialog : Window
{
    static readonly LocalizationService Loc = LocalizationService.Instance;

    readonly PluginsPage.BatchUpdatePlan _plan;

    public bool Confirmed { get; private set; }

    internal PluginBatchUpdateDialog(PluginsPage.BatchUpdatePlan plan)
    {
        InitializeComponent();
        _plan = plan;

        SubtitleText.Text = plan.Batched.Count > 0
            ? Loc.Get("Plugin.Batch.Subtitle", plan.Batched.Count)
            : Loc["Plugin.Batch.NothingToApply"];

        if (plan.NeedsConsent.Count > 0)
        {
            ConsentBar.Visibility = Visibility.Visible;
            ConsentBarText.Text = Loc.Get("Plugin.Batch.ConsentNotice", plan.NeedsConsent.Count);
        }

        if (plan.Batched.Count > 0)
        {
            AddSectionHeader(Loc["Plugin.Batch.BatchedSection"], ThemeBrushes.TextTertiaryModern);
            foreach (var item in plan.Batched) AddItemRow(item, needsConsent: false);
        }

        if (plan.NeedsConsent.Count > 0)
        {
            AddSectionHeader(Loc["Plugin.Batch.ConsentSection"], ThemeBrushes.DangerModern);
            foreach (var item in plan.NeedsConsent) AddItemRow(item, needsConsent: true);
        }

        if (plan.Skipped.Count > 0)
        {
            AddSectionHeader(Loc.Get("Plugin.Batch.SkippedSection", plan.Skipped.Count),
                ThemeBrushes.TextTertiaryModern);
            foreach (var skipped in plan.Skipped) AddSkippedRow(skipped);
        }

        ConfirmButton.Content = Loc.Get("Plugin.Batch.Confirm", plan.Batched.Count);
        ConfirmButton.IsEnabled = plan.Batched.Count > 0;
        ConfirmButton.Opacity = plan.Batched.Count > 0 ? 1.0 : 0.45;
    }

    void AddSectionHeader(string text, Brush brush)
    {
        ItemsHost.Children.Add(new TextBlock
        {
            Text = text,
            FontSize = 11.5,
            FontWeight = FontWeights.SemiBold,
            Margin = new Thickness(0, ItemsHost.Children.Count == 0 ? 0 : 10, 0, 6),
            Foreground = brush,
            TextWrapping = TextWrapping.Wrap
        });
    }

    /// <summary>一条待更新项：名称 + 旧→新版本 + changelog 摘要 + 权限/能力变化提示。</summary>
    void AddItemRow(PluginsPage.BatchUpdateItem item, bool needsConsent)
    {
        AddRow(item.Record, item.Entry, needsConsent, skipText: null,
            addedPermissions: item.AddedPermissions, removedPermissions: item.RemovedPermissions,
            addedKinds: item.AddedKinds);
    }

    /// <summary>一条被跳过的更新：名称 + 旧→新版本 + 原因（不参与批量）。</summary>
    void AddSkippedRow(PluginsPage.BatchUpdateSkippedItem skipped)
    {
        var reason = skipped.Kind == PluginsPage.BatchUpdateSkipKind.Blocked
            ? Loc["Plugin.Batch.SkipBlocked"]
            : (skipped.Incompatibility ?? Loc["Plugin.Batch.SkipIncompatible"]);
        AddRow(skipped.Record, skipped.Entry, needsConsent: false, skipText: reason,
            addedPermissions: Array.Empty<string>(), removedPermissions: Array.Empty<string>(),
            addedKinds: Array.Empty<PluginKind>());
    }

    void AddRow(
        PluginRecord record,
        PluginCatalogEntry entry,
        bool needsConsent,
        string? skipText,
        IReadOnlyList<string> addedPermissions,
        IReadOnlyList<string> removedPermissions,
        IReadOnlyList<PluginKind> addedKinds)
    {
        var name = PluginsPage.DisplayNameOf(entry);
        var stack = new StackPanel();
        stack.Children.Add(new TextBlock
        {
            Text = name,
            FontSize = 13,
            FontWeight = FontWeights.SemiBold,
            Foreground = ThemeBrushes.TextPrimaryModern,
            TextTrimming = TextTrimming.CharacterEllipsis
        });
        stack.Children.Add(new TextBlock
        {
            Text = Loc.Get("Plugin.Update.From", record.Version, entry.Version),
            FontSize = 11,
            Margin = new Thickness(0, 3, 0, 0),
            FontFamily = new FontFamily("Cascadia Code, Consolas, monospace"),
            Foreground = ThemeBrushes.TextSecondaryModern
        });

        var changelog = entry.Changelog?.Get(Loc.CurrentLanguage);
        if (!string.IsNullOrWhiteSpace(changelog))
        {
            var summary = changelog.Trim();
            if (summary.Length > 160) summary = summary[..160] + "…";
            stack.Children.Add(new TextBlock
            {
                Text = summary,
                FontSize = 11,
                Margin = new Thickness(0, 5, 0, 0),
                TextWrapping = TextWrapping.Wrap,
                Foreground = ThemeBrushes.TextTertiaryModern
            });
        }

        if (skipText != null)
        {
            stack.Children.Add(new TextBlock
            {
                Text = skipText,
                FontSize = 11,
                Margin = new Thickness(0, 5, 0, 0),
                TextWrapping = TextWrapping.Wrap,
                Foreground = ThemeBrushes.TextTertiaryModern
            });
        }

        if (needsConsent)
        {
            stack.Children.Add(new TextBlock
            {
                Text = Loc["Plugin.Batch.NeedsConsent"],
                FontSize = 11,
                FontWeight = FontWeights.SemiBold,
                Margin = new Thickness(0, 5, 0, 0),
                TextWrapping = TextWrapping.Wrap,
                Foreground = ThemeBrushes.DangerModern
            });
        }

        if (addedPermissions.Count > 0)
        {
            stack.Children.Add(new TextBlock
            {
                Text = Loc.Get("Plugin.Batch.AddedPermissions",
                    string.Join(" · ", addedPermissions.Select(PluginDisplay.DescribePermission))),
                FontSize = 11,
                Margin = new Thickness(0, 5, 0, 0),
                Foreground = addedPermissions.Contains("network")
                    ? ThemeBrushes.DangerModern
                    : ThemeBrushes.TextSecondaryModern,
                TextWrapping = TextWrapping.Wrap
            });
        }

        if (removedPermissions.Count > 0)
        {
            // 被移除的权限只是提示（变少了不需要二次确认），用弱化颜色。
            stack.Children.Add(new TextBlock
            {
                Text = Loc.Get("Plugin.Update.Permissions.Removed",
                    string.Join(" · ", removedPermissions.Select(PluginDisplay.DescribePermission))),
                FontSize = 11,
                Margin = new Thickness(0, 5, 0, 0),
                Foreground = ThemeBrushes.TextTertiaryModern,
                TextWrapping = TextWrapping.Wrap
            });
        }

        if (addedKinds.Count > 0)
        {
            stack.Children.Add(new TextBlock
            {
                Text = Loc.Get("Plugin.Batch.AddedKinds",
                    string.Join(" + ", addedKinds.Select(kind => Loc[kind.I18nKey()]))),
                FontSize = 11,
                Margin = new Thickness(0, 5, 0, 0),
                Foreground = ThemeBrushes.AccentModern,
                TextWrapping = TextWrapping.Wrap
            });
        }

        ItemsHost.Children.Add(new Border
        {
            Padding = new Thickness(10, 8, 10, 8),
            Margin = new Thickness(0, 0, 0, 6),
            CornerRadius = new CornerRadius(6),
            Background = ThemeBrushes.BgSurfaceModern,
            BorderThickness = new Thickness(1),
            BorderBrush = needsConsent ? ThemeBrushes.DangerModern : ThemeBrushes.BorderSubtleModern,
            Child = stack
        });
    }

    // ── 自测台只读访问器 ──

    internal int BatchedCountForLab => _plan.Batched.Count;
    internal int NeedsConsentCountForLab => _plan.NeedsConsent.Count;
    internal int SkippedCountForLab => _plan.Skipped.Count;
    internal bool ConfirmEnabledForLab => ConfirmButton.IsEnabled;

    void Confirm_Click(object sender, RoutedEventArgs e)
    {
        Confirmed = true;
        DialogResult = true;
        Close();
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
}
