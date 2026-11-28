#if DEBUG
using System;
using System.IO;
using System.Linq;
using System.Security.Cryptography;
using System.Text;
using System.Text.Json;
using System.Text.Json.Serialization;
using System.Threading.Tasks;
using System.Windows;
using System.Windows.Controls.Primitives;
using DesktopZones.Helpers;
using DesktopZones.Models;
using DesktopZones.Services;
using DesktopZones.Views.Pages;

namespace DesktopZones;

/// <summary>
/// 自测台「settings」用例：设置页「插件」分组（启动时加载开关 / 索引镜像地址 / 图片缓存）
/// 与图片缓存 50 MB LRU 清理。单独成文件，避免主自测台文件继续膨胀。
/// </summary>
public partial class App
{
    async Task RunSettingsLabCaseAsync(LabCase item, string dir)
    {
        var catalog = PluginService.Instance.Catalog;

        // ── 1) ShouldLoadCodePlugins 真值表 ──
        // 语义 = 三个条件同时满足才加载代码插件；声明式包不走这个判断（见 Initialize）。
        item.Check(PluginService.ShouldLoadCodePlugins(true, true, false),
            "code plugins load when the master switch is on and nothing suppresses them");
        item.Check(!PluginService.ShouldLoadCodePlugins(false, true, false),
            "the master switch off blocks code plugins");
        item.Check(!PluginService.ShouldLoadCodePlugins(true, false, false),
            "「load at startup」off blocks code plugins");
        item.Check(!PluginService.ShouldLoadCodePlugins(true, true, true),
            "--no-plugins / safe mode blocks code plugins");
        item.Check(!PluginService.ShouldLoadCodePlugins(false, false, false),
            "both switches off still blocks code plugins");
        item.Check(!PluginService.ShouldLoadCodePlugins(false, true, true),
            "the master switch off plus safe mode blocks code plugins");
        item.Check(!PluginService.ShouldLoadCodePlugins(true, false, true),
            "「load at startup」off plus safe mode blocks code plugins");
        item.Check(!PluginService.ShouldLoadCodePlugins(false, false, true),
            "all three conditions false blocks code plugins");

        // ── 2) 配置往返：默认 true / 显式 false 经 JSON 序列化后保留 ──
        // 只动内存里的新对象，不碰真实用户配置（选项与 ConfigService 一致，否则 NaN 会抛）。
        var jsonOptions = new JsonSerializerOptions
        {
            NumberHandling = JsonNumberHandling.AllowNamedFloatingPointLiterals
        };
        var fresh = new AppConfig();
        item.Check(fresh.LoadPluginsAtStartup, "LoadPluginsAtStartup defaults to true");
        var defaultJson = JsonSerializer.Serialize(fresh, jsonOptions);
        var defaultBack = JsonSerializer.Deserialize<AppConfig>(defaultJson, jsonOptions);
        item.Check(defaultBack?.LoadPluginsAtStartup == true,
            "the default value survives a JSON round-trip");
        fresh.LoadPluginsAtStartup = false;
        var disabledJson = JsonSerializer.Serialize(fresh, jsonOptions);
        var disabledBack = JsonSerializer.Deserialize<AppConfig>(disabledJson, jsonOptions);
        item.Check(disabledJson.Contains("\"LoadPluginsAtStartup\":false"),
            "an explicit false is serialized");
        item.Check(disabledBack?.LoadPluginsAtStartup == false,
            "an explicit false survives a JSON round-trip");

        // ── 3) 索引镜像地址校验规则（纯函数） ──
        item.Check(SettingsPage.IsAcceptableRegistryUrl("https://example.com/registry.json"),
            "an https registry url is accepted");
        item.Check(SettingsPage.IsAcceptableRegistryUrl("http://localhost:8080/registry.json"),
            "http://localhost is accepted for local testing");
        item.Check(SettingsPage.IsAcceptableRegistryUrl("http://127.0.0.1:9/registry.json"),
            "http://127.0.0.1 is accepted for local testing");
        item.Check(!SettingsPage.IsAcceptableRegistryUrl("http://example.com/registry.json"),
            "plain http to a remote host is rejected");
        item.Check(!SettingsPage.IsAcceptableRegistryUrl("ftp://example.com/registry.json"),
            "a non-http scheme is rejected");
        item.Check(!SettingsPage.IsAcceptableRegistryUrl("registry.json"),
            "a relative path is rejected");
        item.Check(!SettingsPage.IsAcceptableRegistryUrl(""),
            "an empty string is not treated as an url by the validator");

        // ── 4) 图片缓存：LRU 上限 + 统计 + 清理 ──
        // 全部在隔离 data-root 的 Plugins\catalog\images 下进行；上限临时调小到 10 KB，
        // 这样三张小假文件就能真实跑到「超限 → 按时间删最旧」的整条路径。
        PluginPaths.EnsureCreated();
        var oldMax = catalog.MaxImageCacheBytes;
        var imagesDir = PluginPaths.ImagesDir;
        try
        {
            catalog.ClearImageCache();
            Directory.CreateDirectory(imagesDir);
            catalog.MaxImageCacheBytes = 10 * 1024;

            // 6 KB（最旧）+ 6 KB（中间）+ 1 KB（最新）= 13 KB > 10 KB；
            // 清理目标是 90% = 9 KB，删掉最旧那张后剩 7 KB。另写一个残留 .tmp 验证会被扫掉。
            var oldest = WriteFakeImage(imagesDir, "lab-cache-oldest.png", 6 * 1024, DateTime.UtcNow.AddHours(-3));
            var middle = WriteFakeImage(imagesDir, "lab-cache-middle.png", 6 * 1024, DateTime.UtcNow.AddHours(-2));
            var newest = WriteFakeImage(imagesDir, "lab-cache-newest.png", 1 * 1024, DateTime.UtcNow.AddHours(-1));
            var staleTmp = WriteFakeImage(imagesDir, "lab-cache-stale.png.tmp", 2 * 1024, DateTime.UtcNow);
            var before = catalog.GetImageCacheStats();
            item.Check(before.Files == 3 && before.Bytes == 13 * 1024,
                $"the cache stats count real files only ({before.Files} file(s), {before.Bytes} bytes)");

            catalog.TrimImageCache();
            var trimmed = catalog.GetImageCacheStats();
            item.Check(!File.Exists(oldest), "the oldest cached image is evicted first");
            item.Check(File.Exists(middle) && File.Exists(newest),
                "the newer cached images are kept");
            item.Check(!File.Exists(staleTmp), "leftover *.tmp files are swept by the trim");
            item.Check(trimmed.Bytes <= catalog.MaxImageCacheBytes,
                $"the cache is trimmed below the limit ({trimmed.Bytes} <= {catalog.MaxImageCacheBytes})");
            item.Check(trimmed.Bytes <= (long)(catalog.MaxImageCacheBytes * 0.9),
                $"the trim goes down to about 90% of the limit ({trimmed.Bytes} bytes)");
            item.Check(trimmed.Files == 2, $"two cached images remain ({trimmed.Files})");

            var onDisk = new DirectoryInfo(imagesDir)
                .GetFiles("*", SearchOption.TopDirectoryOnly)
                .Where(f => !f.Name.EndsWith(".tmp", StringComparison.OrdinalIgnoreCase))
                .ToList();
            item.Check(trimmed.Files == onDisk.Count && trimmed.Bytes == onDisk.Sum(f => f.Length),
                "GetImageCacheStats matches the actual directory contents");

            // LRU touch：命中本地缓存必须刷新 LastWriteTimeUtc（下次清理时它就算「最近用过」）。
            const string touchUrl =
                "https://raw.githubusercontent.com/Three-Freezen/DeskOrder-Plugins/main/images/lab-touch.png";
            var touchHash = Convert.ToHexString(SHA1.HashData(Encoding.UTF8.GetBytes(touchUrl))).ToLowerInvariant();
            var touchPath = Path.Combine(imagesDir, touchHash + ".png");
            File.WriteAllBytes(touchPath, new byte[512]);
            var ancient = DateTime.UtcNow.AddHours(-5);
            File.SetLastWriteTimeUtc(touchPath, ancient);
            var resolved = await catalog.GetImageAsync(touchUrl);
            item.Check(resolved == touchPath, "a cached image is served from disk without a network call");
            item.Check(File.GetLastWriteTimeUtc(touchPath) > ancient.AddMinutes(1),
                "a cache hit refreshes LastWriteTimeUtc (LRU touch)");

            catalog.ClearImageCache();
            item.Check(catalog.GetImageCacheStats() == (0, 0L), "clearing the cache resets the stats to zero");
            item.Check(!Directory.Exists(imagesDir) || Directory.GetFiles(imagesDir).Length == 0,
                "the image cache directory is empty after clearing");
        }
        finally
        {
            catalog.MaxImageCacheBytes = oldMax;
            try { PluginPaths.EnsureCreated(); } catch { }
        }

        // ── 5) 设置页 UI：控件同步 / 镜像校验交互 / 截图 ──
        var config = _configService!.Load();
        var originalRegistryUrl = config.PluginRegistryUrl;
        try
        {
            EnsureManagementWindow();
            var window = ManagementWindow;
            if (window == null)
            {
                item.Check(false, "management window exists for the settings screenshot");
                return;
            }

            window.Show();
            window.ShowSection("settings");
            await Task.Delay(1200);

            if (window.MainContent.Content is not SettingsPage settingsPage)
            {
                item.Check(false, "the settings page is shown");
                return;
            }

            // ponytail 2026-11-28: 插件相关设置从「常规」卡片里独立成一栏（用户要求「单独开一栏」）。
            // 用视觉树祖先关系断言分区归属：比如开关跑回了常规卡片，这里就会亮红灯。
            item.Check(settingsPage.PluginsSectionHeader.Text == LocalizationService.Instance["Settings.Plugins"],
                "the plugin settings section has its own localized heading");
            item.Check(IsInVisualSubtree(settingsPage.PluginsEnabledBox, settingsPage.PluginsSectionCard)
                       && IsInVisualSubtree(settingsPage.LoadPluginsAtStartupBox, settingsPage.PluginsSectionCard)
                       && IsInVisualSubtree(settingsPage.MirrorUrlBox, settingsPage.PluginsSectionCard)
                       && IsInVisualSubtree(settingsPage.ClearImageCacheButton, settingsPage.PluginsSectionCard)
                       && IsInVisualSubtree(settingsPage.PluginSettingsButton, settingsPage.PluginsSectionCard),
                "all plugin settings controls live in the dedicated plugin section");
            item.Check(!IsInVisualSubtree(settingsPage.PluginsEnabledBox, settingsPage.GeneralSectionCard)
                       && !IsInVisualSubtree(settingsPage.MirrorUrlBox, settingsPage.GeneralSectionCard)
                       && !IsInVisualSubtree(settingsPage.ClearImageCacheButton, settingsPage.GeneralSectionCard),
                "the general settings card no longer contains plugin controls");

            item.Check(settingsPage.LoadPluginsAtStartupBox.IsChecked == config.LoadPluginsAtStartup,
                "the load-at-startup checkbox reflects the config");
            item.Check(settingsPage.MirrorUrlBox.Text == originalRegistryUrl,
                "the mirror box shows the configured registry url");
            item.Check(settingsPage.CacheSizeText.Text.Contains("MB"),
                "the image cache size line is formatted with a unit");

            // 非法地址：只出错误提示，不写配置。
            settingsPage.MirrorUrlBox.Text = "ftp://example.invalid/registry.json";
            settingsPage.MirrorApplyButton.RaiseEvent(new RoutedEventArgs(ButtonBase.ClickEvent));
            item.Check(_configService.Load().PluginRegistryUrl == originalRegistryUrl,
                "an invalid mirror url is not saved");
            item.Check(settingsPage.MirrorStatusText.Visibility == Visibility.Visible
                       && settingsPage.MirrorStatusText.Text.Length > 0,
                "an invalid mirror url shows an error hint");

            // 本机回环 http：允许保存，并立刻同步到运行中的 catalog。
            settingsPage.MirrorUrlBox.Text = "http://127.0.0.1:9/registry.json";
            settingsPage.MirrorApplyButton.RaiseEvent(new RoutedEventArgs(ButtonBase.ClickEvent));
            item.Check(_configService.Load().PluginRegistryUrl == "http://127.0.0.1:9/registry.json",
                "a loopback http mirror url is saved");
            item.Check(PluginService.Instance.Catalog.RegistryUrl == "http://127.0.0.1:9/registry.json",
                "the running catalog picks up the new mirror url immediately");

            // 「恢复默认」= 配置写空串 + 运行态回到内置地址。
            settingsPage.MirrorResetButton.RaiseEvent(new RoutedEventArgs(ButtonBase.ClickEvent));
            item.Check(_configService.Load().PluginRegistryUrl.Length == 0,
                "reset clears the configured mirror url");
            item.Check(PluginService.Instance.Catalog.RegistryUrl == PluginCatalogService.DefaultRegistryUrl,
                "reset restores the built-in registry url");

            // 截图前把新控件滚进可视区：设置页很长，不滚的话插件分组可能落在视口外。
            // 用分组里最后一行（缓存按钮）定位，这样「启动时加载 / 镜像 / 缓存」三块都尽量进画面。
            settingsPage.ClearImageCacheButton.BringIntoView();
            await Task.Delay(500);
            var shotPath = Path.Combine(dir, "shot-settings-plugins.png");
            SaveScreenshot(window, shotPath);
            item.Check(File.Exists(shotPath),
                "settings screenshot with the plugin controls written");
        }
        finally
        {
            // 配置还原：镜像地址无论断言成败都恢复原值；运行态同步回默认/原值。
            var restore = _configService!.Load();
            restore.PluginRegistryUrl = originalRegistryUrl;
            _configService.Save(restore);
            PluginService.Instance.Catalog.RegistryUrl = string.IsNullOrWhiteSpace(originalRegistryUrl)
                ? PluginCatalogService.DefaultRegistryUrl
                : originalRegistryUrl.Trim();
        }
    }

    /// <summary>node 是否在 ancestor 的视觉子树里（设置页分区归属断言用）。</summary>
    static bool IsInVisualSubtree(DependencyObject? node, DependencyObject ancestor)
    {
        while (node != null)
        {
            if (ReferenceEquals(node, ancestor)) return true;
            node = System.Windows.Media.VisualTreeHelper.GetParent(node);
        }
        return false;
    }

    /// <summary>写一张指定大小、指定「最近使用时间」的假图片文件，返回绝对路径。</summary>
    static string WriteFakeImage(string dir, string name, int bytes, DateTime lastWriteUtc)
    {
        var path = Path.Combine(dir, name);
        var payload = new byte[bytes];
        for (int i = 0; i < bytes; i++) payload[i] = (byte)(i & 0xFF);
        File.WriteAllBytes(path, payload);
        File.SetLastWriteTimeUtc(path, lastWriteUtc);
        return path;
    }
}
#endif
