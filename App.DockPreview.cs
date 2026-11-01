using System;
using System.IO;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Media;
using System.Windows.Media.Imaging;
using DesktopZones.Models;
using DesktopZones.Views.Components;

namespace DesktopZones;

/// <summary>
/// 自测/截图专用的离屏渲染器（<c>--render-dock-preview=&lt;dir&gt;</c> 调用）。
///
/// 单独一个 partial 文件而不是塞进 App.xaml.cs：那边 import 了 <c>System.Drawing</c>
/// （Color / Size 和 WPF 撞名），这里要大量构造 WPF 视觉树，混在一起可读性差还容易歧义。
/// 这些方法只做"搭一棵树 + RenderTargetBitmap + 存 PNG"，不参与任何运行时逻辑。
/// </summary>
public partial class App
{
    /// <summary>属性面板截图：确认快捷栏字段树（悬停动效 / 显示倒影 / 磁贴模式…）的文案与排布。
    /// 面板里的字段是"Loaded + Target 变化"之后才建出来的，所以必须放进一个真窗口里
    /// Show 一次再截，光 Measure/Arrange 会截到一张空图。</summary>
    async System.Threading.Tasks.Task RenderDockPanelShotAsync(string dir, Zone probe,
        string logName = "dock-panel-smoke.txt")
    {
        Window? win = null;
        try
        {
            var panel = new PropertyPanel { Width = 340, Height = 980 };
            probe.Name = "Dock panel probe";
            win = new Window
            {
                Width = 340,
                Height = 980,
                Left = -6000,
                Top = -6000,
                WindowStyle = WindowStyle.None,
                AllowsTransparency = true,
                Background = Brushes.Transparent,
                ShowInTaskbar = false,
                ShowActivated = false,
                Content = new Border
                {
                    // 浅色底：属性面板跟随应用主题（默认浅色）取字色，
                    // 预览底板必须是浅色，否则截出来是深底深字。
                    Background = new SolidColorBrush(Color.FromRgb(0xF4, 0xF5, 0xF7)),
                    Child = panel,
                },
            };
            win.Show();
            panel.Target = probe;
            win.UpdateLayout();
            await System.Threading.Tasks.Task.Delay(600);
            win.UpdateLayout();
            SavePng((FrameworkElement)win.Content, Path.Combine(dir, "dock-panel.png"), 2);
            File.AppendAllText(Path.Combine(dir, logName), "dock-panel-ok\n");
        }
        catch (Exception ex)
        {
            File.AppendAllText(Path.Combine(dir, logName), "dock-panel-FAILED:\n" + ex + "\n");
        }
        finally
        {
            win?.Close();
        }
    }

    /// <summary>预设卡截图：三种典型样式各一张卡，确认「一根长条」的缩略图观感。</summary>
    void RenderDockPresetCards(string dir, Func<Zone> makeSample)
    {
        var row = new StackPanel
        {
            Orientation = Orientation.Horizontal,
            Margin = new Thickness(28),
        };
        var styles = new (string Name, Action<Zone> Tweak)[]
        {
            ("默认胶囊", _ => { }),
            ("磁贴（全透明）", z => z.TileMode = true),
            ("倒影 + 尖角", z => { z.DockReflection = true; z.CornerRadius = 0; z.FillColor = "#CC1B2233"; }),
        };
        foreach (var (name, tweak) in styles)
        {
            var sample = makeSample();
            sample.Name = name;
            tweak(sample);
            row.Children.Add(BuildDockPresetCard(sample, name));
        }
        var stage = new Border
        {
            Background = new SolidColorBrush(Color.FromRgb(0x10, 0x12, 0x18)),
            Child = row,
        };
        stage.Measure(new Size(double.PositiveInfinity, double.PositiveInfinity));
        stage.Arrange(new Rect(stage.DesiredSize));
        stage.UpdateLayout();
        SavePng(stage, Path.Combine(dir, "dock-preset-cards.png"), 2);
    }

    /// <summary>版式与 LoadPresetDialog 的 DockCardTemplate 一致：
    /// 180×220 深色卡 + 内嵌长条缩略图 + 名称/日期信息栏。</summary>
    static Border BuildDockPresetCard(Zone sample, string name)
    {
        var inner = new Border
        {
            Margin = new Thickness(10, 10, 10, 2),
            CornerRadius = new CornerRadius(8),
            BorderBrush = new SolidColorBrush(Color.FromArgb(0x24, 0xFF, 0xFF, 0xFF)),
            BorderThickness = new Thickness(1),
            Background = new SolidColorBrush(Color.FromRgb(0x0B, 0x0B, 0x10)),
            Child = new DockPresetPreview { Zone = sample, Height = 150 },
        };
        var info = new StackPanel { Margin = new Thickness(12, 4, 12, 8), VerticalAlignment = VerticalAlignment.Top };
        info.Children.Add(new TextBlock
        {
            Text = name,
            FontSize = 12,
            FontWeight = FontWeights.SemiBold,
            Foreground = new SolidColorBrush(Color.FromRgb(0xD6, 0xD6, 0xDC)),
            TextTrimming = TextTrimming.CharacterEllipsis,
        });
        info.Children.Add(new TextBlock
        {
            Text = "2026-10-31",
            FontSize = 10,
            Margin = new Thickness(0, 2, 0, 0),
            Foreground = new SolidColorBrush(Color.FromArgb(0x99, 0xFF, 0xFF, 0xFF)),
        });

        var grid = new Grid();
        grid.RowDefinitions.Add(new RowDefinition { Height = new GridLength(1, GridUnitType.Star) });
        grid.RowDefinitions.Add(new RowDefinition { Height = GridLength.Auto });
        grid.Children.Add(inner);
        Grid.SetRow(info, 1);
        grid.Children.Add(info);

        return new Border
        {
            Width = 180,
            Height = 220,
            Margin = new Thickness(0, 0, 16, 0),
            Background = new SolidColorBrush(Color.FromRgb(0x0E, 0x0E, 0x14)),
            CornerRadius = new CornerRadius(12),
            BorderBrush = new SolidColorBrush(Color.FromArgb(0x15, 0x78, 0x78, 0x78)),
            BorderThickness = new Thickness(1),
            Child = grid,
        };
    }

    /// <summary>预设对话框截图：临时存 3 个快捷栏预设，真开一次
    /// <see cref="LoadPresetDialog"/>（Kind=Dock）截一张，然后把临时预设删掉。</summary>
    async System.Threading.Tasks.Task RenderDockPresetDialogAsync(string dir, Func<Zone> makeSample,
        System.Text.StringBuilder log)
    {
        var svc = DesktopZones.Services.PresetService.For(PresetKind.Dock);
        var created = new System.Collections.Generic.List<Guid>();
        Window? dlg = null;
        try
        {
            foreach (var (name, tweak) in new (string Name, Action<Zone> Tweak)[]
            {
                ("默认胶囊", _ => { }),
                ("磁贴（全透明）", z => z.TileMode = true),
                ("倒影 + 尖角", z => { z.DockReflection = true; z.CornerRadius = 0; z.FillColor = "#CC1B2233"; }),
            })
            {
                var sample = makeSample();
                sample.Name = name;
                tweak(sample);
                var rec = svc.Save(name, sample);
                created.Add(rec.Id);
            }

            dlg = new DesktopZones.Views.LoadPresetDialog(svc)
            {
                Left = -6000,
                Top = -6000,
                ShowInTaskbar = false,
                ShowActivated = false,
            };
            dlg.Show();
            dlg.UpdateLayout();
            await System.Threading.Tasks.Task.Delay(700);
            dlg.UpdateLayout();
            SavePng(dlg, Path.Combine(dir, "dock-preset-dialog.png"), 1.6);
            log.AppendLine($"preset dialog: kind=Dock presets={created.Count} ok");
        }
        catch (Exception ex)
        {
            log.AppendLine("preset dialog FAILED: " + ex);
        }
        finally
        {
            dlg?.Close();
            foreach (var id in created) svc.Delete(id);
        }
    }

    static void SavePng(FrameworkElement element, string path, double scale)
    {
        int w = Math.Max(1, (int)Math.Round(element.ActualWidth * scale));
        int h = Math.Max(1, (int)Math.Round(element.ActualHeight * scale));
        var rtb = new RenderTargetBitmap(w, h, 96 * scale, 96 * scale, PixelFormats.Pbgra32);
        rtb.Render(element);
        var enc = new PngBitmapEncoder();
        enc.Frames.Add(BitmapFrame.Create(rtb));
        using var fs = File.Create(path);
        enc.Save(fs);
    }
}
