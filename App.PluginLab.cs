#if DEBUG
using System;
using System.Collections.Generic;
using System.IO;
using System.IO.Compression;
using System.Linq;
using System.Text.Json;
using System.Threading.Tasks;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Media;
using System.Windows.Media.Imaging;
using DeskOrder.PluginApi;
using DesktopZones.Helpers;
using DesktopZones.Models;
using DesktopZones.Services;
using DesktopZones.Views;
using DesktopZones.Views.Pages;

namespace DesktopZones;

/// <summary>
/// P0 自测台：真桌面、真窗口、真像素。与 --dock-lab 同款，跑完写报告退出。
/// 用法：DeskOrder.exe --plugin-lab=&lt;dir&gt; --plugin-case=all --data-root=&lt;dir&gt;\data
/// 目录里需要 hello-widget-1.0.0.dopkg / crash-widget-1.0.0.dopkg（由 tools\run-plugin-lab.ps1 生成）。
/// </summary>
public partial class App
{
    const string SamplePluginId = "three-freezen.hello-widget";
    const string LanguagePackId = "haru-dev.ja";
    const string LanguagePackCode = "ja";
    const string AppearancePackId = "lab.aurora";

    string? ParsePluginLabArg(string[] args)
    {
        const string prefix = "--plugin-lab=";
        var arg = args.FirstOrDefault(a => a.StartsWith(prefix, StringComparison.OrdinalIgnoreCase));
        return arg == null ? null : arg[prefix.Length..].Trim('"');
    }

    string ParsePluginLabCase(string[] args)
    {
        const string prefix = "--plugin-case=";
        var arg = args.FirstOrDefault(a => a.StartsWith(prefix, StringComparison.OrdinalIgnoreCase));
        return arg == null ? "all" : arg[prefix.Length..].Trim().ToLowerInvariant();
    }

    sealed class LabCase
    {
        public string Name { get; init; } = "";
        public bool Pass { get; set; }
        public List<string> Details { get; } = new();
        public void Check(bool condition, string message)
        {
            Details.Add((condition ? "PASS " : "FAIL ") + message);
            if (!condition) Pass = false;
        }
    }

    async Task RunPluginLabAsync(string dir, string caseName)
    {
        Directory.CreateDirectory(dir);
        // 用例自己构造缓存与禁用名单；禁掉启动后台拉取，避免线上 registry 在用例跑到一半时覆盖它们。
        PluginService.SuppressBackgroundRefreshForLab = true;
        var cases = caseName == "all"
            ? new[] { "install", "load", "restart", "disable", "enable", "hide", "uninstall", "offline", "bad", "crash", "cache", "signature", "signature-policy", "lang-install", "lang-switch", "lang-bad", "lang-override", "lang-uninstall", "appearance-install", "appearance-toggle", "appearance-render", "appearance-bad", "appearance-uninstall", "update", "blocked", "dialog", "ui", "dev", "widget-panel", "widgets-page", "security", "tool", "tool-ui" }
            : new[] { caseName };
        var results = new List<LabCase>();
        foreach (var name in cases)
        {
            var item = new LabCase { Name = name, Pass = true };
            try
            {
                await RunLabCaseAsync(item, name, dir);
            }
            catch (Exception ex)
            {
                item.Pass = false;
                item.Details.Add("EXCEPTION " + ex);
            }
            results.Add(item);
        }

        var report = new
        {
            time = DateTime.Now,
            dataRoot = DataLocator.Root,
            cases = results.Select(r => new { name = r.Name, pass = r.Pass, details = r.Details })
        };
        var json = JsonSerializer.Serialize(report, new JsonSerializerOptions { WriteIndented = true });
        File.WriteAllText(Path.Combine(dir, "plugin-lab-report.json"), json);
        File.WriteAllLines(Path.Combine(dir, "plugin-lab.log"),
            results.SelectMany(r => new[] { $"=== {r.Name} : {(r.Pass ? "PASS" : "FAIL")}" }.Concat(r.Details)));
        DzTrace.Always($"[PluginLab] done: {results.Count(r => r.Pass)}/{results.Count} passed");
    }

    async Task RunLabCaseAsync(LabCase item, string name, string dir)
    {
        var service = PluginService.Instance;
        switch (name)
        {
            case "install":
            {
                var result = await InstallSampleAsync(dir);
                item.Check(result != null && result.Success, "install returns success");
                if (result == null) break;
                var record = service.GetRecord(SamplePluginId);
                item.Check(record != null, "record is written to config");
                var installJson = PluginPackages.ReadInstallInfo(PluginPaths.PluginDir(SamplePluginId));
                item.Check(installJson != null && installJson.Version == "1.0.0", "install.json has version");
                item.Check(Directory.Exists(PluginPaths.VersionDir(SamplePluginId, "1.0.0")), "version directory exists");
                var sha = PluginPackages.Sha256OfFile(FindSamplePackage(dir)!);
                item.Check(installJson?.Sha256 == sha, "install.json sha256 matches the package");
                break;
            }
            case "load":
            {
                await EnsureEnabledAsync(dir);
                var loaded = service.GetLoaded(SamplePluginId);
                item.Check(loaded != null, "plugin is loaded");
                item.Check(service.GetDescriptors(SamplePluginId).Count == 1, "one widget descriptor registered");
                var widget = EnsureWidget(service);
                item.Check(widget != null, "widget instance created");
                await Task.Delay(500);
                var window = widget == null ? null : service.GetWindow(widget.Id);
                item.Check(window != null && window.IsVisible, "widget window is visible");
                if (window != null)
                {
                    SaveScreenshot(window, Path.Combine(dir, "shot-load.png"));
                    item.Check(File.Exists(Path.Combine(dir, "shot-load.png")), "screenshot written");
                }
                break;
            }
            case "restart":
            {
                await EnsureEnabledAsync(dir);
                var widget = EnsureWidget(service);
                if (widget == null) { item.Check(false, "widget exists"); break; }
                widget.SettingsJson = "{\"clicks\":\"7\"}";
                service.SaveWidgets();
                service.ReloadForLab();
                var restored = service.Widgets.FirstOrDefault(w => w.Id == widget.Id);
                item.Check(restored != null, "widget survives restart");
                item.Check(restored?.SettingsJson.Contains("\"7\"") == true, "per-instance settings survive restart");
                await Task.Delay(400);
                item.Check(service.GetWindow(widget.Id)?.IsVisible == true, "window restored after restart");
                break;
            }
            case "disable":
            {
                await EnsureEnabledAsync(dir);
                service.SetEnabled(SamplePluginId, false);
                item.Check(service.GetLoaded(SamplePluginId) == null, "plugin unloaded after disable");
                item.Check(service.GetDescriptors(SamplePluginId).Count == 0, "descriptors removed");
                service.ReloadForLab();
                item.Check(service.GetLoaded(SamplePluginId) == null, "disabled plugin is not loaded after restart");
                item.Check(service.GetRecord(SamplePluginId)?.Enabled == false, "record stays disabled");
                break;
            }
            case "enable":
            {
                service.SetEnabled(SamplePluginId, true);
                service.ReloadForLab();
                item.Check(service.GetLoaded(SamplePluginId) != null, "plugin loads after enable");
                var widget = EnsureWidget(service);
                if (widget != null) service.ShowWidget(widget);
                await Task.Delay(400);
                item.Check(widget != null && service.GetWindow(widget!.Id)?.IsVisible == true, "widget window is back");
                break;
            }
            case "uninstall":
            {
                await EnsureEnabledAsync(dir);
                var widgetCountBefore = service.CountWidgets(SamplePluginId);
                var ok = service.Uninstall(SamplePluginId, deleteData: false, out var error);
                item.Check(ok, "uninstall returns success" + (error == null ? "" : " (" + error + ")"));
                item.Check(!Directory.Exists(PluginPaths.PluginDir(SamplePluginId)), "plugin directory removed");
                item.Check(service.GetRecord(SamplePluginId) == null, "record removed from config");
                item.Check(service.CountWidgets(SamplePluginId) == widgetCountBefore, "widgets are kept as placeholders");
                break;
            }
            case "offline":
            {
                // 隔离外部状态：本用例断言的是「没有缓存时」的降级行为，但社区 registry
                // 已经发布，应用启动后的后台拉取（PluginService.RefreshBlocklistInBackground）
                // 以及前面的 cache/ui 类用例都会往这个缓存里写真实数据。缓存只要在，
                // LoadAsync 失败就会回落到它，条目数不再为 0。先清掉缓存与时间戳。
                // 注意：File.Delete 与 LoadAsync 里同步执行的 File.Exists 之间没有 await，
                // 后台拉取不存在插进这段窗口的机会。
                File.Delete(PluginPaths.RegistryCachePath);
                File.Delete(PluginPaths.RegistryMetaPath);
                var catalog = new PluginCatalogService
                {
                    RegistryUrl = "http://127.0.0.1:9/registry.json"
                };
                var result = await catalog.LoadAsync(force: true);
                item.Details.Add($"entries={result.Entries.Count} fromCache={result.FromCache} " +
                                 $"offline={result.Offline} fetched={result.FetchedAtUtc:O} error={result.Error}");
                item.Check(result.Offline, "offline is reported");
                item.Check(result.Entries.Count == 0, "no entries without cache");
                item.Check(!string.IsNullOrEmpty(result.Error), "error message is kept");
                break;
            }
            case "bad":
            {
                var slip = Path.Combine(dir, "zip-slip.dopkg");
                CreateZipSlipPackage(slip);
                var slipResult = await service.Installer!.InstallFromFileAsync(slip);
                item.Check(!slipResult.Success, "zip slip is rejected");
                item.Check((slipResult.Error ?? "").Contains("unsafe path", StringComparison.OrdinalIgnoreCase)
                           || (slipResult.Error ?? "").Contains("not a valid", StringComparison.OrdinalIgnoreCase),
                    "zip slip error mentions the path");

                var badRefPackage = Path.Combine(dir, "bad-ref-1.0.0.dopkg");
                if (File.Exists(badRefPackage))
                {
                    var badRef = await service.Installer!.InstallFromFileAsync(badRefPackage);
                    item.Check(!badRef.Success, "plugin referencing DeskOrder is rejected");
                    item.Check((badRef.Error ?? "").Contains("must not reference DeskOrder"),
                        "audit explains the rejected reference");
                }
                else
                {
                    item.Check(false, "bad-ref package exists");
                }

                var badManifestDir = Path.Combine(dir, "bad-manifest");
                Directory.CreateDirectory(badManifestDir);
                File.WriteAllText(Path.Combine(badManifestDir, "plugin.json"),
                    "{\"schemaVersion\":1,\"id\":\"BAD ID\",\"version\":\"x\"}");
                var badManifest = PluginPackages.ReadManifest(badManifestDir, out _);
                item.Check(badManifest != null
                           && badManifest.Validate(PluginInstaller.HostVersion, true).Count > 0,
                    "invalid manifest is rejected");
                break;
            }
            case "crash":
            {
                var crashPackage = Path.Combine(dir, "crash-widget-1.0.0.dopkg");
                if (!File.Exists(crashPackage)) { item.Check(false, "crash package exists"); break; }
                var install = await service.Installer!.InstallFromFileAsync(crashPackage);
                item.Check(install.Success, "crash plugin installs");
                service.RefreshRecords();
                var record = service.Records.FirstOrDefault(r => r.Id == "three-freezen.crash-widget");
                if (record == null) { item.Check(false, "crash record exists"); break; }
                service.SetEnabled(record.Id, true);
                service.ReloadForLab();
                record = service.GetRecord(record.Id);
                item.Check(record?.CrashCount == 1, "first failure increments crash count");
                item.Check(service.GetLoaded(record!.Id) == null, "crash plugin is not loaded");
                service.ReloadForLab();
                record = service.GetRecord(record.Id);
                item.Check(record?.CrashCount >= 2, "second failure increments crash count");
                item.Check(record?.Enabled == false, "crash plugin is auto-disabled");
                break;
            }
            case "hide":
            {
                await EnsureEnabledAsync(dir);
                var widget = EnsureWidget(service);
                if (widget == null) { item.Check(false, "widget exists"); break; }
                service.ShowWidget(widget);
                await Task.Delay(300);
                service.HideWidget(widget);
                await Task.Delay(400);
                item.Check(!widget.IsVisible, "model is marked hidden");
                var window = service.GetWindow(widget.Id);
                item.Check(window != null && window.Width <= 50 && window.Height <= 50,
                    "window collapses to the 44x44 restore button");
                if (window != null) SaveScreenshot(window, Path.Combine(dir, "shot-collapsed.png"));
                item.Check(File.Exists(Path.Combine(dir, "shot-collapsed.png")), "collapsed screenshot written");
                service.ShowWidget(widget);
                await Task.Delay(300);
                item.Check(widget.IsVisible && service.GetWindow(widget.Id)?.Width > 50, "restore expands again");
                break;
            }
            case "cache":
            {
                var registry = Path.Combine(dir, "registry.json");
                if (!File.Exists(registry)) { item.Check(false, "registry.json exists"); break; }
                PluginPaths.EnsureCreated();
                File.Copy(registry, PluginPaths.RegistryCachePath, overwrite: true);
                File.WriteAllText(PluginPaths.RegistryMetaPath,
                    "{\"Etag\":null,\"FetchedAtUtc\":\"" + DateTime.UtcNow.ToString("O") + "\"}");
                var catalog = new PluginCatalogService { RegistryUrl = "http://127.0.0.1:9/registry.json" };
                var result = await catalog.LoadAsync(force: false);
                item.Details.Add($"fromCache={result.FromCache} offline={result.Offline} entries={result.Entries.Count} fetched={result.FetchedAtUtc:O} error={result.Error}");
                item.Check(result.Entries.Count == 4, "cached registry has 4 entries");
                break;
            }
            case "lang-install":
            {
                await EnsureLanguagePackAsync(dir);
                var record = service.GetRecord(LanguagePackId);
                item.Check(record != null && record.Enabled, "language record is installed and enabled");
                item.Check(record?.KindValue == PluginKind.Language, "record kind is language");
                var loc = LocalizationService.Instance;
                item.Check(loc.AvailableLanguages.Contains(LanguagePackCode), "ja appears in AvailableLanguages");
                item.Check(loc.NativeLanguageNames.TryGetValue(LanguagePackCode, out var native) && native == "日本語",
                    "native name is 日本語");
                var coverage = loc.GetLanguageCoverage(LanguagePackCode) ?? 0;
                item.Check(coverage > 0.02 && coverage < 0.2, $"coverage is recomputed ({coverage:0.000})");
                item.Check(loc.GetLanguageDisplay(LanguagePackCode).Contains("日本語")
                           && loc.GetLanguageDisplay(LanguagePackCode).Contains("%"),
                    "display text carries coverage");
                item.Check(File.Exists(PluginPaths.LanguageIndexPath), "languages.json index is written");
                break;
            }
            case "lang-switch":
            {
                await EnsureLanguagePackAsync(dir);
                var loc = LocalizationService.Instance;
                loc.SetLanguage(LanguagePackCode);
                item.Check(loc.CurrentLanguage == LanguagePackCode, "switched to ja");
                item.Check(loc["Manage.Zones"] == "ゾーン", "translated key comes from the pack");
                item.Check(loc["Plugin.Count"].Contains("コミュニティ"), "formatted template keeps its placeholders");
                var english = LocalizationService.LoadFromDiskForTest(
                    Path.Combine(LocalizationService.GetI18nDir(), "source.en.json"));
                item.Check(loc["Plugin.RestartNeeded"] == english["Plugin.RestartNeeded"],
                    "untranslated key falls back to built-in English");
                item.Check(loc.Get("Plugin.Count", 3, 1).Contains("3") && loc.Get("Plugin.Count", 3, 1).Contains("1"),
                    "string.Format works with the pack translation");

                EnsureManagementWindow();
                var window = ManagementWindow;
                if (window != null)
                {
                    window.Show();
                    window.ShowSection("settings");
                    await Task.Delay(800);
                    SaveScreenshot(window, Path.Combine(dir, "shot-language-ja.png"));
                    item.Check(File.Exists(Path.Combine(dir, "shot-language-ja.png")), "japanese settings screenshot");
                    window.Hide();
                }

                loc.SetLanguage("zh");
                item.Check(loc["Manage.Zones"] == "分区", "switch back to zh restores built-in strings");
                loc.SetLanguage(LanguagePackCode);
                break;
            }
            case "lang-bad":
            {
                var badPlaceholder = Path.Combine(dir, "lang-bad-placeholder-1.0.0.dopkg");
                if (!File.Exists(badPlaceholder)) { item.Check(false, "bad-placeholder package exists"); break; }
                var first = await service.Installer!.InstallFromFileAsync(badPlaceholder);
                item.Check(!first.Success, "placeholder mismatch is rejected");
                item.Check((first.Error ?? "").Contains("Plugin.Count")
                           && (first.Error ?? "").Contains("{1}"),
                    "error names the key and the expected placeholder");

                var unknownKey = Path.Combine(dir, "lang-unknown-key-1.0.0.dopkg");
                if (!File.Exists(unknownKey)) { item.Check(false, "unknown-key package exists"); break; }
                var second = await service.Installer!.InstallFromFileAsync(unknownKey);
                item.Check(!second.Success, "unknown key is rejected");
                item.Check((second.Error ?? "").Contains("unknown key"), "error mentions the unknown key");
                service.RefreshRecords();
                item.Check(service.GetRecord("lab.bad-placeholder") == null
                           && service.GetRecord("lab.unknown-key") == null,
                    "rejected packs leave no record behind");
                break;
            }
            case "lang-override":
            {
                await EnsureLanguagePackAsync(dir);
                var loc = LocalizationService.Instance;
                loc.SetLanguage(LanguagePackCode);
                var langDir = Path.Combine(DataLocator.Root, "lang");
                Directory.CreateDirectory(langDir);
                var overridePath = Path.Combine(langDir, "ja.json");
                File.WriteAllText(overridePath, "{\"Manage.Zones\":\"ユーザーゾーン\"}");
                try
                {
                    loc.Reload();
                    item.Check(loc["Manage.Zones"] == "ユーザーゾーン", "user override wins over the language pack");
                    item.Check(loc["Manage.Clocks"] == "時計", "other pack keys still apply");
                }
                finally
                {
                    File.Delete(overridePath);
                    loc.Reload();
                }
                break;
            }
            case "lang-uninstall":
            {
                await EnsureLanguagePackAsync(dir);
                var loc = LocalizationService.Instance;
                loc.SetLanguage(LanguagePackCode);
                var ok = service.Uninstall(LanguagePackId, deleteData: true, out var error);
                item.Check(ok, "language pack uninstalls" + (error == null ? "" : " (" + error + ")"));
                item.Check(!Directory.Exists(PluginPaths.PluginDir(LanguagePackId)), "pack directory removed");
                item.Check(service.GetRecord(LanguagePackId) == null, "record removed");
                item.Check(loc.CurrentLanguage == "zh", "falls back to zh when the active pack is removed");
                item.Check(!loc.AvailableLanguages.Contains(LanguagePackCode), "ja disappears from the language list");
                item.Check(loc["Manage.Zones"] == "分区", "UI is not empty after the fallback");
                item.Check(loc.GetLanguageDisplay("zh") == "中文", "built-in display name is intact");
                break;
            }
            case "appearance-install":
            {
                await EnsureAppearancePackAsync(dir);
                var record = service.GetRecord(AppearancePackId);
                item.Check(record != null && record.Enabled, "appearance record is installed and enabled");
                item.Check(record?.KindValue == PluginKind.Appearance, "record kind is appearance");
                var key = AppearancePackService.MaterialKey(AppearancePackId, "aurora.sky");
                var material = AcrylicHelper.FindMaterial(key);
                item.Check(material != null, "pack material is registered");
                item.Check(AcrylicHelper.AllMaterials.Count == AcrylicHelper.Materials.Count + 3,
                    "three pack materials are listed");
                item.Check(material?.ZhName == "极光·天青", "material carries the manifest display name");
                item.Check(AcrylicHelper.GetMaterialDisplayName(key) == "极光·天青",
                    "display name follows the current language");
                var summary = AppearancePackService.Instance.GetSummary(AppearancePackId);
                item.Check(summary?.Materials == 3 && summary?.Presets == 2,
                    "summary reports 3 materials and 2 presets");
                item.Check(PresetService.For(PresetKind.Zone).LoadAll()
                        .Any(p => p.Name == "Aurora Zone" && p.SourcePackId == AppearancePackId),
                    "zone preset is imported");
                item.Check(PresetService.For(PresetKind.Panel).LoadAll()
                        .Any(p => p.Name == "Aurora Panel" && p.SourcePackId == AppearancePackId),
                    "panel preset is imported");

                // 模拟重启：记录仍在 → Initialize 重新注册材质，预设文件本来就在磁盘上。
                service.ReloadForLab();
                item.Check(AcrylicHelper.FindMaterial(key) != null, "materials survive a restart");
                item.Check(PresetService.For(PresetKind.Zone).LoadAll()
                        .Any(p => p.SourcePackId == AppearancePackId), "presets survive a restart");
                break;
            }
            case "appearance-toggle":
            {
                await EnsureAppearancePackAsync(dir);
                var key = AppearancePackService.MaterialKey(AppearancePackId, "aurora.sky");
                service.SetEnabled(AppearancePackId, false);
                item.Check(AcrylicHelper.FindMaterial(key) == null, "disable removes pack materials");
                item.Check(!PresetService.For(PresetKind.Zone).LoadAll()
                        .Any(p => p.SourcePackId == AppearancePackId), "disable removes imported presets");
                service.SetEnabled(AppearancePackId, true);
                item.Check(AcrylicHelper.FindMaterial(key) != null, "enable restores pack materials");
                item.Check(PresetService.For(PresetKind.Zone).LoadAll()
                        .Any(p => p.SourcePackId == AppearancePackId), "enable restores imported presets");
                break;
            }
            case "appearance-render":
            {
                await EnsureAppearancePackAsync(dir);
                var sky = AppearancePackService.MaterialKey(AppearancePackId, "aurora.sky");
                var ice = AppearancePackService.MaterialKey(AppearancePackId, "aurora.ice");
                var skyRecipe = AcrylicHelper.FindMaterial(sky);
                var iceRecipe = AcrylicHelper.FindMaterial(ice);
                item.Check(skyRecipe?.AccentState == AcrylicHelper.AccentStateAcrylic,
                    "acrylic backdrop maps to accent state 4");
                item.Check(skyRecipe?.UseClassicBlur == true, "acrylic backdrop adds classic blur");
                item.Check(Math.Abs((skyRecipe?.NoiseOpacity ?? 0) - 0.06) < 0.0001,
                    "grain maps to noise opacity");
                item.Check(iceRecipe?.AccentState == AcrylicHelper.AccentStateBlur,
                    "blur backdrop maps to accent state 3");
                item.Check(iceRecipe?.UseClassicBlur == false, "blur backdrop has no classic blur");

                // 材质 key 写进分区模型后能原样读回 —— 渲染侧只认 key。
                var zone = new Zone { Name = "Aurora lab", Width = 320, Height = 220, GlassMaterial = sky };
                item.Check(zone.GlassMaterial == sky, "material key persists on a zone model");
                break;
            }
            case "appearance-bad":
            {
                var package = Path.Combine(dir, "appearance-bad-1.0.0.dopkg");
                if (!File.Exists(package)) { item.Check(false, "bad appearance package exists"); break; }
                var result = await service.Installer!.InstallFromFileAsync(package);
                item.Check(!result.Success, "bad appearance pack is rejected");
                item.Check((result.Error ?? "").Contains("grain"), "error mentions the out-of-range grain");
                item.Check((result.Error ?? "").Contains("preset kind"), "error mentions the mismatched preset kind");
                service.RefreshRecords();
                item.Check(service.GetRecord("lab.bad-appearance") == null,
                    "rejected appearance pack leaves no record");
                break;
            }
            case "appearance-uninstall":
            {
                await EnsureAppearancePackAsync(dir);
                var key = AppearancePackService.MaterialKey(AppearancePackId, "aurora.sky");
                var ok = service.Uninstall(AppearancePackId, deleteData: true, out var error);
                item.Check(ok, "appearance pack uninstalls" + (error == null ? "" : " (" + error + ")"));
                item.Check(!Directory.Exists(PluginPaths.PluginDir(AppearancePackId)), "pack directory removed");
                item.Check(service.GetRecord(AppearancePackId) == null, "record removed");
                item.Check(AcrylicHelper.FindMaterial(key) == null, "pack material disappears");
                item.Check(AcrylicHelper.AllMaterials.All(m =>
                        !m.Key.StartsWith("pack:", StringComparison.OrdinalIgnoreCase)),
                    "no pack materials remain");
                item.Check(!PresetService.For(PresetKind.Zone).LoadAll()
                        .Any(p => p.SourcePackId == AppearancePackId), "zone preset removed");
                item.Check(!PresetService.For(PresetKind.Panel).LoadAll()
                        .Any(p => p.SourcePackId == AppearancePackId), "panel preset removed");
                break;
            }
            case "signature":
            {
                // 固定向量由 tools/minisign_lite.py vectors 生成（密钥见 tools/lab-key）。
                const string labKey = "RWRwPFQw853K3AqrRpvS4mIfqHnrtQVkQFj75VjIzgAQZDE6u5FQnmLi";
                var payload = Convert.FromBase64String(
                    "RGVza09yZGVyIHBsdWdpbiBzaWduYXR1cmUgc2VsZi10ZXN0IHBheWxvYWQgdjE=");
                var rawSig = "untrusted comment: signature from minisign secret key\nRWRwPFQw853K3GjeuwXMPqekf45kDvzwXChRyQseYJKxGGcoHcGzalBrvHmASyQ1bWveRKodENvLiHF8zk2ALJg8GGshzY9qfAI=\ntrusted comment: timestamp:1794641899\tfile:selftest.dopkg\nlOAc4c/0ltW/cz3tqycUMmE93BYCfiXWttnH4Yj63K/bBOYmqYzWM8M5bXqwEbVp6b6QOKMs7YCKI+QyIHpkDg==\n";
                var preSig = "untrusted comment: signature from minisign secret key\nRURwPFQw853K3NSwZRVci4MUfMMrRSTry/BARryMQ7nrCm4e5D4CAcmJaUD7PMmSn8jRVnznZvOsNWXQ4cWxur7NrUG6YBoUTwU=\ntrusted comment: timestamp:1794641899\tfile:selftest.dopkg\tprehashed\nZLav8IyexsT3SDAmt7vIq0IFVGq04jRPZ1Tj2s9QHblXAV1kTWMkBBP7iR2for1EMTUqTxNVhl/xWDEwEWWeBw==\n";
                var tamperSig = "untrusted comment: signature from minisign secret key\nRWRwPFQw853K3DoKEg1fipQt/itj7Ev8SaejdTsQ7U25w5Ra/rZ7ovv7FpQgqtKtYTyyKQsG9/3u/08S215wN4+rUfOhVYI9UAA=\ntrusted comment: timestamp:1794641899\tfile:selftest.dopkg\nqcM6SEPNM30sfZWhcD76sYEOBH7amnFZyeYuMlVs2e70REj5r7pVRC0oRrHy+jkI/blmoLz3Vjn7VlQLgLKNCw==\n";
                var keys = new[] { labKey };

                item.Check(MiniSign.VerifyBytes(payload, rawSig, keys).Status == PackageSignature.Valid,
                    "raw (Ed) signature verifies");
                item.Check(MiniSign.VerifyBytes(payload, preSig, keys).Status == PackageSignature.Valid,
                    "prehashed (ED, blake2b) signature verifies");
                item.Check(MiniSign.VerifyBytes(payload, tamperSig, keys).Status == PackageSignature.Invalid,
                    "a signature for other bytes is rejected");
                var flipped = payload.ToArray();
                flipped[0] ^= 1;
                item.Check(MiniSign.VerifyBytes(flipped, rawSig, keys).Status == PackageSignature.Invalid,
                    "payload modification is detected");
                item.Check(MiniSign.VerifyBytes(payload, rawSig, new[] { PluginTrust.OfficialPublicKey }).Status
                        == PackageSignature.Invalid,
                    "a signature from an untrusted key is rejected");
                item.Check(MiniSign.VerifyBytes(payload, null, keys).Status == PackageSignature.None,
                    "no signature is reported as unsigned");

                // ── 跨块回归：>128 字节的真实包才会走到 BLAKE2b 的多块路径 ──
                // （曾经计数器传单块长度，导致所有真实 .dopkg 的预哈希签名必然失败。）
                var longPayload = Convert.FromBase64String(
                    "CzBVep/E6Q4zWH2ix+wRNluApcrvFDleg6jN8hc8YYar0PUaP2SJrtP4HUJnjLHW+yBFao+02f4jSG2St9wBJktwlbrfBClOc5i94gcsUXabwOUKL1R5nsPoDTJXfKHG6xA1Wn+kye4TOF2Cp8zxFjtgharP9Bk+Y4it0vccQWaLsNX6H0RpjrPY/SJHbJG22wAlSm+Uud4DKE1yl7zhBitQdZq/5AkuU3idwucMMVZ7oMXqDzRZfqPI7RI3XIGmy/AVOl+Eqc7zGD1ih6zR9htAZYqv1PkeQ2iNstf8IUZrkLXa/yRJbpO43QInTHGWu+AFKk90mb7jCC1Sd5zB5gswVXqfxOkOM1h9osfsETZbgKXK7xQ5XoOozfIXPGGGq9D1Gj9kia7T+B1C");
                var longRaw = "untrusted comment: signature from minisign secret key\nRWRwPFQw853K3HAXZJYXO3XQdxzfWL6lBWwZ+LJQeLUIX7HMZofA03WsVtGuoLKucC7xI1jyUq3AJ1B5TBMoISlXNRGgwMFAsgg=\ntrusted comment: timestamp:1794644220\tfile:long.dopkg\nZ8c6z3PPHvWmW3pJvkyn0WTSKbFNEY92Kg4ULPLVo2XYtUcEy93H60ImW0GKgbX6Kz8wfvo8r1ipu/HuhoWMCg==\n";
                var longPre = "untrusted comment: signature from minisign secret key\nRURwPFQw853K3AQ7TvaIw1iNvgpXIsB4VeyZw7Os0+SKREbdvGF0Q+npUPkU5OoIKM/9+iWFuY3o2M6iO7E6YkFpv6bX3cyE7gA=\ntrusted comment: timestamp:1794644220\tfile:long.dopkg\tprehashed\nB/keVxF33nJE9d06WfoC+9IRAoC31JNNbWGGcTQF4B+2Y4/t6RSz2wl4eW5nZCPBdSLrK507vqRXb3xeY3IsAA==\n";
                // 同一条预哈希签名去掉第 4 行（全局签名）：主签名仍对，但 comment 没人签 → 必须拒绝。
                var longStripped = "untrusted comment: signature from minisign secret key\nRURwPFQw853K3AQ7TvaIw1iNvgpXIsB4VeyZw7Os0+SKREbdvGF0Q+npUPkU5OoIKM/9+iWFuY3o2M6iO7E6YkFpv6bX3cyE7gA=\ntrusted comment: timestamp:1794644220\tfile:long.dopkg\tprehashed\n";
                item.Check(longPayload.Length == 300, "the long fixture is 300 bytes (crosses the 128-byte block)");
                item.Check(MiniSign.VerifyBytes(longPayload, longRaw, keys).Status == PackageSignature.Valid,
                    "raw signature over a multi-block payload verifies");
                item.Check(MiniSign.VerifyBytes(longPayload, longPre, keys).Status == PackageSignature.Valid,
                    "prehashed signature over a multi-block payload verifies (BLAKE2b counter)");
                var strippedVerdict = MiniSign.VerifyBytes(longPayload, longStripped, keys);
                item.Check(strippedVerdict.Status == PackageSignature.Invalid,
                    "a .minisig with its global signature removed is rejected");

                // ── 直接对 Ed25519 做负例 ──
                var rfcPk = Convert.FromHexString("fc51cd8e6218a1a38da47ed00230f0580816ed13ba3303ac5deb911548908025");
                var rfcSig = Convert.FromHexString("6291d657deec24024827e69c3abe01a30ce548a284743a445e3680d7db5ac3ac18ff9b538d16f290ae67f760984dc6594a7c15e9716ed28dc027beceea1ec40a");
                var rfcMsg = Convert.FromHexString("af82");
                item.Check(Ed25519.Verify(rfcPk, rfcMsg, rfcSig), "RFC 8032 test vector 3 verifies");
                // s + L：数学上同一签名，但 s >= L 必须拒绝。
                var nonCanonical = (byte[])rfcSig.Clone();
                var s = new System.Numerics.BigInteger(nonCanonical.AsSpan(32, 32), isUnsigned: true, isBigEndian: false);
                var sPlusL = s + new System.Numerics.BigInteger(
                    Convert.FromHexString("edd3f55c1a631258d69cf7a2def9de1400000000000000000000000000000010"),
                    isUnsigned: true, isBigEndian: false);
                sPlusL.ToByteArray(isUnsigned: true, isBigEndian: false).CopyTo(nonCanonical, 32);
                item.Check(!Ed25519.Verify(rfcPk, rfcMsg, nonCanonical), "non-canonical s (s + L) is rejected");
                // y >= p 的公钥编码必须拒绝。
                var badY = new byte[32];
                Array.Fill(badY, (byte)0xFF);
                item.Check(!Ed25519.Verify(badY, rfcMsg, rfcSig), "a public key with y >= p is rejected");
                // 全零签名 / 全零公钥。
                item.Check(!Ed25519.Verify(new byte[32], rfcMsg, new byte[64]), "an all-zero key and signature are rejected");

                // 走 PluginTrust：陌生钥匙 = 未签名；加入临时信任后 = 已验证。
                var fixture = Path.Combine(dir, "signature-fixture.bin");
                File.WriteAllBytes(fixture, payload);
                PluginTrust.ResetTemporaryKeys();
                item.Check(PluginTrust.Check(fixture, rawSig).Level == TrustLevel.Unsigned,
                    "unknown key is treated as unsigned, not tampered");
                PluginTrust.AddTemporaryKey(labKey);
                var verdict = PluginTrust.Check(fixture, rawSig);
                item.Check(verdict.Level == TrustLevel.Verified, "trusted key verifies the file");
                item.Check(!string.IsNullOrEmpty(verdict.KeyId), "the verdict carries the signing key id");
                PluginTrust.ResetTemporaryKeys();
                break;
            }
            case "signature-policy":
            {
                item.Check(PluginTrust.Reject(PluginKind.Widget, true, TrustVerdict.Unsigned) != null,
                    "official widget without a signature is rejected");
                item.Check(PluginTrust.Reject(PluginKind.ZoneBar, true, TrustVerdict.Unsigned) != null,
                    "official zone bar without a signature is rejected");
                item.Check(PluginTrust.Reject(PluginKind.Language, true, TrustVerdict.Unsigned) == null,
                    "official declarative pack may stay unsigned");
                item.Check(PluginTrust.Reject(PluginKind.Widget, true,
                        new TrustVerdict { Level = TrustLevel.Verified, KeyId = "0011223344556677" }) == null,
                    "official widget with a valid signature passes");
                item.Check(PluginTrust.Reject(PluginKind.Widget, true,
                        new TrustVerdict { Level = TrustLevel.Invalid, Error = "test" }) != null,
                    "an invalid signature is rejected even for community packs");
                item.Check(PluginTrust.Reject(PluginKind.Widget, false, TrustVerdict.Unsigned) == null,
                    "community code plugins may stay unsigned");

                var package = FindSamplePackage(dir);
                if (package == null) { item.Check(false, "sample package exists"); break; }
                var signed = Path.Combine(dir, "signed-hello.dopkg");
                File.Copy(package, signed, overwrite: true);
                File.WriteAllText(signed + ".minisig", "untrusted comment: bogus\nAAAA\n");
                item.Check(PluginService.Instance.Installer!.PreviewPackage(signed, out var sidecarError) == null,
                    "a broken .minisig blocks local import");
                item.Check(!string.IsNullOrEmpty(sidecarError), "the rejection explains why");
                File.Delete(signed + ".minisig");
                item.Check(PluginService.Instance.Installer!.PreviewPackage(signed, out _) != null,
                    "the same package imports fine without the stray signature");
                File.Delete(signed);
                break;
            }
            case "update":
            {
                await EnsureEnabledAsync(dir);
                var record = PluginService.Instance.GetRecord(SamplePluginId);
                if (record == null) { item.Check(false, "sample plugin is installed"); break; }
                var entry = new PluginCatalogEntry
                {
                    Id = SamplePluginId,
                    Kind = "widget",
                    Name = new LocalizedText { Zh = "你好挂件", En = "Hello widget" },
                    Summary = new LocalizedText { Zh = "示例", En = "sample" },
                    Author = new PluginAuthor { Name = "Three-Freezen", Github = "Three-Freezen" },
                    Repo = "Three-Freezen/DeskOrder-Plugin-Hello",
                    Version = "1.1.0",
                    MinAppVersion = "1.0.0",
                    License = "Apache-2.0",
                    Permissions = new List<string> { "network", "notifications" },
                    Changelog = new LocalizedText
                    {
                        Zh = "新增天气概览，需要联网获取数据。",
                        En = "Adds a weather summary that needs network access."
                    },
                    DownloadUrl = "https://example.invalid/hello.dopkg"
                };
                item.Check(PluginDetailDialog.IsUpdateAvailable(record, entry), "1.1.0 is recognized as an update");
                var newer = new PluginCatalogEntry
                {
                    Id = SamplePluginId, Version = "1.1.0", MinAppVersion = "9.9.9"
                };
                item.Check(Version.TryParse(newer.MinAppVersion, out var required)
                            && required > PluginInstaller.HostVersion,
                    "a newer minAppVersion is detectable before downloading");

                var dialog = new PluginUpdateDialog(entry, record);
                dialog.Show();
                await Task.Delay(400);
                item.Check(dialog.ConsentCheck.Visibility == Visibility.Visible,
                    "adding the network permission shows the consent checkbox");
                item.Check(!dialog.UpdateButton.IsEnabled, "the update button stays disabled until consent");
                dialog.ConsentCheck.IsChecked = true;
                item.Check(dialog.UpdateButton.IsEnabled, "consent enables the update button");
                SaveScreenshot(dialog, Path.Combine(dir, "shot-update-dialog.png"));
                item.Check(File.Exists(Path.Combine(dir, "shot-update-dialog.png")),
                    "update dialog screenshot written");
                dialog.Close();
                break;
            }
            case "blocked":
            {
                await EnsureEnabledAsync(dir);
                var service2 = PluginService.Instance;
                var package = FindSamplePackage(dir);
                var blockedEntry = new BlockedPlugin
                {
                    Id = SamplePluginId,
                    Version = "1.0.0",
                    Reason = new LocalizedText
                    {
                        Zh = "自测：该版本会上传剪贴板内容",
                        En = "self-test: this version uploads the clipboard"
                    },
                    Since = DateTime.UtcNow
                };
                // 单调合并保护：应用启动后的后台拉取成功后，会把真实 registry 的
                // updatedAt（2026-11-14T09:48:02Z）写进本地名单的「来源时间」。之后再
                // 不带时间戳地写空名单，会被判成「比本地旧」而只取并集 —— 封禁就清不掉。
                // 这里模拟的是「来自 registry 的一次更新」，显式给一个严格递增的 updatedAt。
                PluginBlocklist.SaveFromRegistry(new List<BlockedPlugin> { blockedEntry }, NextLabRegistryStamp());
                PluginBlocklist.Reload();
                service2.ApplyBlocklist();

                item.Check(PluginBlocklist.IsBlocked(SamplePluginId, "1.0.0"), "the exact version is blocked");
                item.Check(!PluginBlocklist.IsBlocked(SamplePluginId, "1.1.0"), "other versions are unaffected");
                item.Check(service2.GetRecord(SamplePluginId)?.Enabled == false, "blocked plugin is force-disabled");
                item.Check(service2.GetLoaded(SamplePluginId) == null, "blocked plugin is unloaded");
                item.Check(service2.SetEnabled(SamplePluginId, true) == false, "a blocked plugin cannot be re-enabled");
                item.Check(service2.LoadErrors.TryGetValue(SamplePluginId, out var reason) && reason.Contains("自测"),
                    "the block reason shows up in the installed list");
                if (package != null)
                {
                    var reinstall = await service2.Installer!.InstallFromFileAsync(package);
                    item.Check(!reinstall.Success && (reinstall.Error ?? "").Contains("blocked"),
                        "installing a blocked version is refused");
                }

                EnsureManagementWindow();
                var window = ManagementWindow;
                window?.Show();
                window?.ShowSection("plugins");
                await Task.Delay(900);
                if (window?.MainContent.Content is PluginsPage blockedPage) blockedPage.SelectTab(1);
                await Task.Delay(500);
                if (window != null) SaveScreenshot(window, Path.Combine(dir, "shot-blocked.png"));
                item.Check(File.Exists(Path.Combine(dir, "shot-blocked.png")), "blocked screenshot written");

                // 还原：用更晚的 updatedAt 表示「registry 撤掉了这条封禁」。
                PluginBlocklist.SaveFromRegistry(new List<BlockedPlugin>(), NextLabRegistryStamp());
                PluginBlocklist.Reload();
                service2.SetEnabled(SamplePluginId, true);
                service2.ReloadForLab();
                item.Check(service2.GetRecord(SamplePluginId)?.Enabled == true,
                    "clearing the blocklist restores the plugin");
                item.Check(!service2.LoadErrors.ContainsKey(SamplePluginId),
                    "the block message is cleared once the version is allowed again");
                break;
            }
            case "dialog":
            {
                await EnsureEnabledAsync(dir);
                var registry = Path.Combine(dir, "registry.json");
                if (!File.Exists(registry)) { item.Check(false, "registry.json exists"); break; }
                var catalog = JsonSerializer.Deserialize<PluginCatalogFile>(
                    File.ReadAllText(registry),
                    new JsonSerializerOptions { PropertyNameCaseInsensitive = true, PropertyNamingPolicy = JsonNamingPolicy.CamelCase });
                var entry = catalog?.Plugins.FirstOrDefault();
                if (entry == null) { item.Check(false, "registry has an entry"); break; }

                var prompt = new PluginInstallPrompt
                {
                    Name = entry.Name.Get("zh"),
                    KindLine = "桌面组件",
                    Author = "@" + entry.Author.Github,
                    Version = entry.Version,
                    Size = "12 KB",
                    Sha256 = entry.Sha256,
                    Permissions = "不申请任何权限"
                };
                var install = new PluginInstallDialog(prompt);
                install.Show();
                await Task.Delay(300);
                SaveScreenshot(install, Path.Combine(dir, "shot-install-dialog.png"));
                item.Check(File.Exists(Path.Combine(dir, "shot-install-dialog.png")), "install dialog screenshot");
                install.Close();

                var detail = new PluginDetailDialog(entry, PluginService.Instance.GetRecord(SamplePluginId));
                detail.Show();
                await Task.Delay(500);
                SaveScreenshot(detail, Path.Combine(dir, "shot-detail.png"));
                item.Check(File.Exists(Path.Combine(dir, "shot-detail.png")), "detail dialog screenshot");
                detail.Close();

                // 语言包：安装确认里的「包含内容」+ 详情里的覆盖率/回退语言。
                var languageEntry = catalog?.Plugins.FirstOrDefault(p => p.KindValue == PluginKind.Language);
                if (languageEntry?.Language != null)
                {
                    var languagePrompt = new PluginInstallPrompt
                    {
                        Name = languageEntry.Name.Get("zh"),
                        KindLine = "语言包",
                        Author = "@" + languageEntry.Author.Github,
                        Version = languageEntry.Version,
                        Size = "9 KB",
                        Sha256 = languageEntry.Sha256,
                        Contents = LocalizationService.Instance.Get("Plugin.Contents.Language",
                            languageEntry.Language.NativeName,
                            (int)Math.Round(languageEntry.Language.Coverage * 100), languageEntry.Language.Base),
                        Declarative = true
                    };
                    var languageInstall = new PluginInstallDialog(languagePrompt);
                    languageInstall.Show();
                    await Task.Delay(300);
                    SaveScreenshot(languageInstall, Path.Combine(dir, "shot-install-language.png"));
                    item.Check(File.Exists(Path.Combine(dir, "shot-install-language.png")),
                        "language install dialog screenshot");
                    languageInstall.Close();

                    var languageDetail = new PluginDetailDialog(languageEntry, null);
                    languageDetail.Show();
                    await Task.Delay(400);
                    SaveScreenshot(languageDetail, Path.Combine(dir, "shot-detail-language.png"));
                    item.Check(File.Exists(Path.Combine(dir, "shot-detail-language.png")),
                        "language detail screenshot");
                    languageDetail.Close();
                }
                else
                {
                    item.Check(false, "registry has a language entry");
                }

                var appearanceEntry = catalog?.Plugins.FirstOrDefault(p => p.KindValue == PluginKind.Appearance);
                if (appearanceEntry != null)
                {
                    var appearanceDetail = new PluginDetailDialog(appearanceEntry, null);
                    appearanceDetail.Show();
                    await Task.Delay(400);
                    SaveScreenshot(appearanceDetail, Path.Combine(dir, "shot-detail-appearance.png"));
                    item.Check(File.Exists(Path.Combine(dir, "shot-detail-appearance.png")),
                        "appearance detail screenshot");
                    appearanceDetail.Close();
                }
                break;
            }
            case "ui":
            {
                // 用本地 registry 缓存让市场页有卡片，再截管理窗口的「插件市场」页。
                await EnsureEnabledAsync(dir);
                await EnsureLanguagePackAsync(dir);
                await EnsureAppearancePackAsync(dir);
                EnsureWidget(PluginService.Instance);
                var registry = Path.Combine(dir, "registry.json");
                if (File.Exists(registry))
                {
                    PluginPaths.EnsureCreated();
                    File.Copy(registry, PluginPaths.RegistryCachePath, overwrite: true);
                    File.WriteAllText(PluginPaths.RegistryMetaPath,
                        "{\"Etag\":null,\"FetchedAtUtc\":\"" + DateTime.UtcNow.ToString("O") + "\"}");
                }
                EnsureManagementWindow();
                var window = ManagementWindow;
                if (window == null) { item.Check(false, "management window exists"); break; }
                window.Show();
                window.ShowSection("plugins");
                await Task.Delay(1200);
                SaveScreenshot(window, Path.Combine(dir, "shot-market.png"));
                item.Check(File.Exists(Path.Combine(dir, "shot-market.png")), "market screenshot written");
                if (window.MainContent.Content is PluginsPage page)
                {
                    page.SelectTab(1);
                    await Task.Delay(600);
                    SaveScreenshot(window, Path.Combine(dir, "shot-installed.png"));
                    item.Check(File.Exists(Path.Combine(dir, "shot-installed.png")), "installed screenshot written");
                }
                LocalizationService.Instance.SetLanguage("en");
                window.ShowSection("plugins");
                await Task.Delay(700);
                SaveScreenshot(window, Path.Combine(dir, "shot-market-en.png"));
                item.Check(File.Exists(Path.Combine(dir, "shot-market-en.png")), "english screenshot written");
                LocalizationService.Instance.SetLanguage("zh");
                window.ShowSection("settings");
                await Task.Delay(500);
                SaveScreenshot(window, Path.Combine(dir, "shot-settings.png"));
                item.Check(File.Exists(Path.Combine(dir, "shot-settings.png")), "settings screenshot written");
                break;
            }
            case "ja-gallery":
            {
                // 官方日语全量语言包：装包 → 切到日语 → 抓主界面几张图当插件展示图。
                // 需要 lab 目录里有 lang-ja-full-<ver>.dopkg（plugin-community\tools\build-ja-pack.ps1 生成）。
                const string JapanesePackId = "three-freezen.ja";
                var loc = LocalizationService.Instance;
                var package = Path.Combine(dir, "lang-ja-full-1.0.0.dopkg");
                item.Check(File.Exists(package), "japanese package is staged");
                if (File.Exists(package) && service.GetRecord(JapanesePackId) == null)
                {
                    var install = await service.Installer!.InstallFromFileAsync(package);
                    item.Check(install.Success,
                        "japanese package installs" + (install.Success ? "" : ": " + install.Error));
                    service.RefreshRecords();
                }
                item.Check(service.GetRecord(JapanesePackId) != null, "japanese record exists");
                if (service.GetRecord(JapanesePackId)?.Enabled != true) service.SetEnabled(JapanesePackId, true);
                loc.ReloadLanguagePacks();

                item.Check(loc.TrySetLanguage("ja"), "switched to japanese");
                var hostSource = LocalizationService.ReadHostSource();
                var jaTable = LanguagePackService.Instance.GetTable("ja");
                item.Check(jaTable != null && jaTable.Count == hostSource.Count,
                    $"full japanese coverage ({jaTable?.Count ?? 0}/{hostSource.Count})");
                item.Check(loc["Manage.Zones"] == "ゾーン", "lookup comes from the full pack");

                // 市场页要有卡片：把 lab 目录里的 registry 当成缓存。
                var registry = Path.Combine(dir, "registry.json");
                if (File.Exists(registry))
                {
                    PluginPaths.EnsureCreated();
                    File.Copy(registry, PluginPaths.RegistryCachePath, overwrite: true);
                    File.WriteAllText(PluginPaths.RegistryMetaPath,
                        "{\"Etag\":null,\"FetchedAtUtc\":\"" + DateTime.UtcNow.ToString("O") + "\"}");
                }

                EnsureManagementWindow();
                var window = ManagementWindow;
                if (window == null) { item.Check(false, "management window exists"); break; }
                var shotRoot = Path.Combine(dir, "ja-shots");
                Directory.CreateDirectory(shotRoot);
                window.Show();
                var shots = new (string Section, string File, int Delay)[]
                {
                    ("zones", "ja-main.png", 900),
                    ("plugins", "ja-plugins.png", 1500),
                    ("settings", "ja-settings.png", 800),
                    ("panel", "ja-panel.png", 800),
                    ("calendar", "ja-widgets.png", 800),
                };
                foreach (var shot in shots)
                {
                    window.ShowSection(shot.Section);
                    await Task.Delay(shot.Delay);
                    SaveScreenshot(window, Path.Combine(shotRoot, shot.File));
                    item.Check(File.Exists(Path.Combine(shotRoot, shot.File)), shot.File + " written");
                }

                // 分区属性面板（日语字段树，语言包最密集的一屏）
                var zone = _zoneManager?.Zones?.FirstOrDefault(z => !z.DockMode);
                if (zone != null)
                {
                    DesktopZones.Helpers.PropertyWindowService.OpenOrFocus(zone);
                    await Task.Delay(900);
                    var panel = DesktopZones.Views.Components.PropertyWindowManager.Instance.GetFloating(zone);
                    item.Check(panel != null, "the property panel opens for a zone");
                    if (panel != null)
                    {
                        SaveScreenshot(panel, Path.Combine(shotRoot, "ja-property.png"));
                        item.Check(File.Exists(Path.Combine(shotRoot, "ja-property.png")),
                            "ja-property.png written");
                        panel.Close();
                    }
                }
                loc.SetLanguage("zh");
                break;
            }
            case "dev":
            {
                // 开发者模式：直接把源码输出目录当已启用插件加载，不落配置。
                var service3 = PluginService.Instance;
                // 开发者模式不该受禁用名单影响：blocked 用例模拟过封禁，若它没清理干净
                // （或本地名单里本来就封了这个版本），ApplyBlocklist 会把刚扫到的 dev
                // 记录强制停用 —— 表现就是「发现了却没加载」。这里按「registry 撤回封禁」
                // 显式解禁，让本用例不依赖前一个用例的清理结果。
                var keepBlocked = PluginBlocklist.Entries
                    .Where(e => !string.Equals(e.Id, SamplePluginId, StringComparison.OrdinalIgnoreCase))
                    .ToList();
                PluginBlocklist.SaveFromRegistry(keepBlocked, NextLabRegistryStamp());
                var package = FindSamplePackage(dir);
                if (package == null) { item.Check(false, "sample package exists"); break; }
                if (service3.GetRecord(SamplePluginId) != null) service3.Uninstall(SamplePluginId, deleteData: false, out _);
                service3.ReloadForLab();

                var devRoot = Path.Combine(dir, "dev-plugins");
                var target = Path.Combine(devRoot, "hello");
                if (Directory.Exists(target)) Directory.Delete(target, recursive: true);
                Directory.CreateDirectory(devRoot);
                ZipFile.ExtractToDirectory(package, target);

                service3.DevDirectory = devRoot;
                service3.ReloadForLab();
                var devRecord = service3.GetRecord(SamplePluginId);
                item.Check(devRecord != null && devRecord.Source == "dev",
                    "the dev directory shows up as an installed plugin");
                item.Check(service3.GetLoaded(SamplePluginId) != null, "the dev plugin is loaded");
                item.Check(service3.GetLoaded(SamplePluginId)?.Directory == target,
                    "the dev plugin loads from the source directory");
                item.Check(PluginPackages.ReadInstallInfo(PluginPaths.PluginDir(SamplePluginId)) == null,
                    "a dev plugin never goes through the installer (no install.json)");
                item.Check(devRecord?.Official == false && devRecord?.Signature == "unsigned",
                    "dev plugins are never treated as official");

                // 关掉开发者模式后必须彻底消失 —— 说明它没被写进 plugins.json。
                service3.DevDirectory = null;
                service3.ReloadForLab();
                item.Check(service3.GetRecord(SamplePluginId) == null, "the dev record is never persisted");
                break;
            }
            case "widget-panel":
            {
                // 插件组件接入了宿主的属性面板：字段树、状态区、预览、取消回滚。
                await EnsureEnabledAsync(dir);
                var service4 = PluginService.Instance;
                var widget = EnsureWidget(service4);
                if (widget == null) { item.Check(false, "widget instance exists"); break; }

                EnsureManagementWindow();
                DesktopZones.Helpers.PropertyWindowService.OpenOrFocus(widget);
                await Task.Delay(700);

                var floating = DesktopZones.Views.Components.PropertyWindowManager.Instance.GetFloating(widget);
                item.Check(floating != null, "the property panel opens for a plugin widget");
                if (floating == null) break;
                item.Check(ReferenceEquals(floating.Body.Target, widget), "the panel targets the plugin widget");
                item.Check(floating.Title.Length > 0, "the floating window has a title");
                SaveScreenshot(floating, Path.Combine(dir, "shot-widget-panel.png"));
                item.Check(File.Exists(Path.Combine(dir, "shot-widget-panel.png")),
                    "widget property panel screenshot written");

                // 快照 / 取消回滚：外观字段要能整体还原，插件自己的 SettingsJson 不能被动。
                var snapshot = widget.Clone();
                var settingsBefore = widget.SettingsJson;
                widget.CornerRadius = 30;
                widget.Opacity = 0.45;
                widget.FillColor = "#33FF0000";
                widget.CopyFrom(snapshot);
                item.Check(widget.CornerRadius == snapshot.CornerRadius, "cancel restores the corner radius");
                item.Check(Math.Abs(widget.Opacity - snapshot.Opacity) < 0.001, "cancel restores the opacity");
                item.Check(widget.FillColor == snapshot.FillColor, "cancel restores the fill colour");
                item.Check(widget.SettingsJson == settingsBefore, "cancel never touches the plugin's own settings");

                // 预览：改模型 → RefreshAppearance → 窗口跟上（不写盘）。
                // 前面的用例可能把组件窗口关掉了（禁用/卸载会关窗），先确保窗口存在。
                if (service4.GetWindow(widget.Id) == null) service4.ShowWidget(widget);
                await Task.Delay(400);
                var before = widget.CornerRadius;
                widget.CornerRadius = before == 20 ? 24 : 20;
                widget.Opacity = 0.6;
                var liveBefore = service4.GetWindow(widget.Id);
                liveBefore?.RefreshAppearance();
                var window = service4.GetWindow(widget.Id);
                item.Check(window != null, "the widget window exists before preview");
                item.Check(window != null && Math.Abs(window.Opacity - 0.6) < 0.01,
                    $"preview applies the new opacity to the live window (actual {window?.Opacity:0.00})");

                // 还原
                widget.CornerRadius = before;
                widget.Opacity = 1.0;
                widget.FillColor = snapshot.FillColor;
                service4.GetWindow(widget.Id)?.RefreshAppearance();
                service4.SaveWidgetsNow();

                DesktopZones.Views.Components.PropertyWindowManager.Instance.CloseWindow(widget);
                await Task.Delay(300);
                item.Check(DesktopZones.Views.Components.PropertyWindowManager.Instance.GetFloating(widget) == null,
                    "the panel closes cleanly");
                break;
            }
            case "widgets-page":
            {
                // 「插件组件」页：新建入口 → 实例出现 → 截图 → 删除回退；
                // 顺便验证总开关关闭时 RestoreWidgets / ShowAllWidgets / CreateWidget 都不产生占位窗。
                await EnsureEnabledAsync(dir);
                var service5 = PluginService.Instance;
                EnsureManagementWindow();
                var window = ManagementWindow;
                if (window == null) { item.Check(false, "management window exists"); break; }
                window.Show();
                window.ShowSection("pluginwidgets");
                await Task.Delay(900);
                item.Check(window.MainContent.Content is PluginWidgetsPage,
                    "the plugin widgets page is shown");
                if (window.MainContent.Content is not PluginWidgetsPage page) break;
                item.Check(page.AvailableDescriptorCount() >= 1,
                    "the page sees at least one widget descriptor");
                item.Check(page.NewButtonEnabled,
                    "the New widget button is enabled when a descriptor is available");
                var createMenu = DesktopZones.Views.Components.PluginWidgetCreateMenu.Build(window, null);
                item.Check(createMenu.Items.Count >= 1,
                    "the shared create menu lists the available widget types");

                var before = service5.Widgets.Count;
                var beforeRows = page.RowCount;
                var created = page.CreateFirstWidgetForTest();
                item.Check(created != null, "creating a widget from the page succeeds");
                if (created == null) break;
                item.Check(service5.Widgets.Count == before + 1, "widget instance count grows by one");
                item.Check(service5.GetWindow(created.Id)?.IsVisible == true,
                    "the new widget window is visible");
                await Task.Delay(600);
                item.Check(page.RowCount == beforeRows + 1, "the list shows the new row");
                SaveScreenshot(window, Path.Combine(dir, "shot-plugin-widgets.png"));
                item.Check(File.Exists(Path.Combine(dir, "shot-plugin-widgets.png")),
                    "plugin widgets screenshot written");

                // 详情弹窗里的文字版入口：已安装行把按钮压成「＋」图标后，这里必须还能新建。
                var sampleRecord = service5.GetRecord(SamplePluginId);
                var sampleEntry = new PluginCatalogEntry
                {
                    Id = SamplePluginId,
                    Kind = "widget",
                    Name = new LocalizedText { Zh = "Hello", En = "Hello" },
                    Version = sampleRecord?.Version ?? "1.0.0",
                    ApiVersion = 1
                };
                var detail = new PluginDetailDialog(sampleEntry, sampleRecord) { Owner = window };
                detail.Show();
                await Task.Delay(400);
                item.Check(detail.NewWidgetButton.Visibility == Visibility.Visible,
                    "the detail dialog shows the widget creation entry for a widget plugin");
                item.Check(detail.NewWidgetButton.IsEnabled,
                    "the detail dialog entry is enabled while descriptors are loaded");
                SaveScreenshot(detail, Path.Combine(dir, "shot-detail-widget-new.png"));
                item.Check(File.Exists(Path.Combine(dir, "shot-detail-widget-new.png")),
                    "detail dialog screenshot with the widget entry written");
                detail.Close();
                await Task.Delay(200);

                // 总开关守卫：先把窗口关掉、模型标成可见（模拟上次退出时是可见的），
                // 关闭总开关后 RestoreWidgets 必须什么都不开 —— 否则会开出「插件不可用」占位窗。
                service5.CloseWindow(created.Id);
                created.IsVisible = true;
                item.Check(service5.GetWindow(created.Id) == null,
                    "the widget window is closed before the guard checks");
                service5.SetPluginsEnabledForLab(false);
                try
                {
                    service5.RestoreWidgets();
                    service5.ShowAllWidgets();
                    item.Check(service5.GetWindow(created.Id) == null,
                        "no placeholder window is restored while the plugin system is off");
                    item.Check(service5.CreateWidget(SamplePluginId, "hello") == null,
                        "CreateWidget is a no-op while the plugin system is off");
                    item.Check(service5.Widgets.Count == before + 1,
                        "no instance is added while the plugin system is off");
                }
                finally
                {
                    service5.SetPluginsEnabledForLab(true);
                }

                service5.DeleteWidget(created);
                page.RefreshList();
                await Task.Delay(300);
                item.Check(service5.Widgets.Count == before,
                    "deleting the widget restores the instance count");
                item.Check(page.RowCount == beforeRows, "the list drops the deleted row");
                item.Check(service5.GetWindow(created.Id) == null,
                    "no window is left behind after deleting the widget");
                break;
            }
            case "security":
                // 安装/索引安全加固用例，实现见 App.PluginLab.Security.cs（并行开发时只改那个文件）。
                await RunSecurityLabCaseAsync(item, name, dir);
                break;
            case "tool":
            {
                // 工具插件内核（API v3）：命令全部合成，不依赖真实 dopkg / 真实插件程序集。
                var tools = PluginToolService.Instance;
                tools.Clear();

                item.Check(PluginApiVersion.Current == 3, "plugin API version is 3");

                PluginManifest MakeToolManifest(int apiVersion) => new()
                {
                    SchemaVersion = 1,
                    Id = "lab.tool",
                    Name = new LocalizedText { Zh = "自测工具", En = "Lab tool" },
                    Summary = new LocalizedText { Zh = "自测工具", En = "lab tool" },
                    Version = "1.0.0",
                    Author = new PluginAuthor { Name = "Three-Freezen", Github = "Three-Freezen" },
                    Repo = "Three-Freezen/DeskOrder-Plugin-Lab",
                    License = "Apache-2.0",
                    Kind = "tool",
                    ApiVersion = apiVersion,
                    MinAppVersion = "1.0.0",
                    Entry = new PluginEntry { Assembly = "lib/Lab.Tool.dll", Type = "Lab.Tool.Plugin" },
                    Icon = "icon.png"
                };
                var manifest = MakeToolManifest(3);
                item.Check(manifest.Validate(PluginInstaller.HostVersion, forLocalImport: true).Count == 0,
                    "manifest validation accepts a tool plugin with apiVersion 3");
                item.Check(MakeToolManifest(4).Validate(PluginInstaller.HostVersion, forLocalImport: true)
                        .Any(e => e.Contains("not supported", StringComparison.Ordinal)),
                    "apiVersion 4 is rejected with a clear reason");

                // 注册表装配期校验：非法 id / 重复 id / 超过 16 条都抛异常。
                bool Throws(Action action)
                {
                    try { action(); return false; } catch { return true; }
                }
                var registry = new PluginToolRegistry();
                item.Check(Throws(() => registry.Register(new PluginToolCommand
                    { Id = "BAD ID", DisplayName = "bad", Execute = _ => Task.CompletedTask })),
                    "an invalid tool command id is rejected");
                registry.Register(new PluginToolCommand
                    { Id = "dup", DisplayName = "dup", Execute = _ => Task.CompletedTask });
                item.Check(Throws(() => registry.Register(new PluginToolCommand
                    { Id = "dup", DisplayName = "dup again", Execute = _ => Task.CompletedTask })),
                    "a duplicate tool command id is rejected");
                for (int i = 0; i < PluginToolRegistry.MaxCommands - 1; i++)
                    registry.Register(new PluginToolCommand
                        { Id = $"cap{i}", DisplayName = "cap", Execute = _ => Task.CompletedTask });
                item.Check(Throws(() => registry.Register(new PluginToolCommand
                    { Id = "overflow", DisplayName = "overflow", Execute = _ => Task.CompletedTask })),
                    "registering more than 16 commands is rejected");

                var record = new PluginRecord
                {
                    Id = "lab.tool",
                    Version = "1.0.0",
                    Kind = "tool",
                    Enabled = true,
                    Source = "lab",
                    Signature = "unsigned"
                };
                int commandRuns = 0;
                var runCommand = new PluginToolCommand
                {
                    Id = "lab.run",
                    DisplayName = "Lab run",
                    Description = "lab command",
                    Menus = PluginToolSurface.Tray | PluginToolSurface.Panel | PluginToolSurface.Zone,
                    Execute = ctx =>
                    {
                        commandRuns++;
                        ctx.Settings.Set("lab.key", "lab.value");
                        return Task.CompletedTask;
                    }
                };
                tools.Publish(record, manifest, new[] { runCommand });
                item.Check(tools.Commands.Count == 1, "publishing one command lists exactly one command");
                var entry = tools.Find("lab.tool", "lab.run");
                item.Check(entry != null, "the published command is found by plugin id + command id");
                item.Check(tools.Find("lab.tool", "missing") == null, "an unknown command id is not found");
                item.Check(tools.HasCommands(PluginToolSurface.Tray)
                           && tools.HasCommands(PluginToolSurface.Panel)
                           && tools.HasCommands(PluginToolSurface.Zone),
                    "all three menu surfaces report the command");

                var trayMenu = tools.BuildMenu(PluginToolSurface.Tray);
                var panelMenu = tools.BuildMenu(PluginToolSurface.Panel);
                var zoneMenu = tools.BuildMenu(PluginToolSurface.Zone);
                item.Check(trayMenu != null && trayMenu.Items.Count == 1, "tray menu carries exactly one command");
                item.Check(panelMenu != null && panelMenu.Items.Count == 1, "panel menu carries exactly one command");
                item.Check(zoneMenu != null && zoneMenu.Items.Count == 1, "zone menu carries exactly one command");
                item.Check((trayMenu?.Tag as string) == PluginToolService.MenuTag
                           && (trayMenu?.Header as string) == LocalizationService.Instance["PluginTool.Menu"],
                    "the submenu is tagged and uses the localized title");

                var hostMenu = new ContextMenu();
                tools.Attach(hostMenu, PluginToolSurface.Tray);
                var itemsAfterFirstAttach = hostMenu.Items.Count;
                tools.Attach(hostMenu, PluginToolSurface.Tray);
                item.Check(itemsAfterFirstAttach == 2 && hostMenu.Items.Count == 2,
                    "attaching adds one separator and one submenu");
                item.Check(hostMenu.Items.OfType<MenuItem>()
                        .Count(m => (m.Tag as string) == PluginToolService.MenuTag) == 1,
                    "attaching twice does not stack a second plugin tool submenu");

                // 插入位置：分区菜单里插件工具要排在「删除」之前（insertIndex 是调用方看到的当前下标）。
                var orderedMenu = new ContextMenu();
                var anchorA = new MenuItem { Header = "a" };
                var anchorB = new MenuItem { Header = "b" };
                var anchorDelete = new MenuItem { Header = "delete" };
                orderedMenu.Items.Add(anchorA);
                orderedMenu.Items.Add(anchorB);
                orderedMenu.Items.Add(anchorDelete);
                var deleteIndex = orderedMenu.Items.IndexOf(anchorDelete);
                tools.Attach(orderedMenu, PluginToolSurface.Zone, deleteIndex);
                // 第二次按「重新打开菜单」的真实路径：下标在当前（已含插件工具项的）菜单上重新取。
                tools.Attach(orderedMenu, PluginToolSurface.Zone, orderedMenu.Items.IndexOf(anchorDelete));
                item.Check(orderedMenu.Items.Count == 5
                           && orderedMenu.Items[2] is Separator
                           && ((orderedMenu.Items[3] as MenuItem)?.Tag as string) == PluginToolService.MenuTag
                           && ReferenceEquals(orderedMenu.Items[4], anchorDelete),
                    "the submenu lands right before the delete item and survives re-opening");

                var invoked = await tools.InvokeAsync(entry!, PluginToolSurface.Tray);
                item.Check(invoked, "invoking the command succeeds");
                item.Check(commandRuns == 1, "the command body ran once");
                var storage = new JsonPluginStorage(
                    Path.Combine(PluginPaths.PluginDataDir("lab.tool"), "settings.json"), null, null);
                item.Check(storage.Get("lab.key") == "lab.value",
                    "settings written inside the command are flushed to the plugin data dir");

                var failing = new PluginToolCommand
                {
                    Id = "lab.fail",
                    DisplayName = "Lab fail",
                    Menus = PluginToolSurface.Hotkey,
                    Execute = _ => throw new InvalidOperationException("lab tool failure")
                };
                tools.Publish(record, manifest, new[] { runCommand, failing });
                item.Check(tools.Commands.Count == 2, "republishing replaces the plugin's earlier commands");
                item.Check(tools.HasCommands(PluginToolSurface.Hotkey),
                    "the failing command opts into the hotkey surface");
                var failingEntry = tools.Find("lab.tool", "lab.fail");
                var failed = failingEntry != null && !await tools.InvokeAsync(failingEntry, PluginToolSurface.Hotkey);
                item.Check(failed, "a throwing command returns false instead of throwing");
                item.Check(commandRuns == 1, "the failing command never ran the successful body");

                // 全局热键：配置 → 注册 → WM_HOTKEY 分发 → 清配置注销。
                var config = _configService!.Load();
                var hotkeyBackup = config.PluginToolHotkeys;
                config.PluginToolHotkeys = new List<PluginToolHotkey>
                {
                    // Ctrl+Alt+Shift+F9（MOD_ALT|MOD_CONTROL|MOD_SHIFT = 0x0007，VK_F9 = 0x78）。
                    new() { PluginId = "lab.tool", CommandId = "lab.run", Modifiers = 0x0007, Key = 0x78, Enabled = true }
                };
                _configService.Save(config);
                RefreshPluginToolHotkeys();
                item.Check(PluginToolHotkeyMapForLab.Count >= 1, "a configured plugin tool hotkey is registered");
                var hotkeyId = PluginToolHotkeyMapForLab.Keys.FirstOrDefault();
                var runsBeforeHotkey = commandRuns;
                item.Check(hotkeyId >= PLUGIN_TOOL_HOTKEY_BASE && TryHandlePluginToolHotkey(hotkeyId),
                    "the hotkey id is dispatched to the command");
                await Task.Delay(150);
                item.Check(commandRuns == runsBeforeHotkey + 1, "the command ran through the hotkey path");

                config = _configService.Load();
                config.PluginToolHotkeys = new List<PluginToolHotkey>();
                _configService.Save(config);
                RefreshPluginToolHotkeys();
                item.Check(PluginToolHotkeyMapForLab.Count == 0, "clearing the list unregisters the hotkey");

                tools.RemovePlugin("lab.tool");
                item.Check(tools.Commands.Count == 0, "removing the plugin drops its commands");
                item.Check(tools.BuildMenu(PluginToolSurface.Tray) == null,
                    "no plugin tool menu is built after removal");
                item.Check(!tools.HasCommands(PluginToolSurface.Tray)
                           && !tools.HasCommands(PluginToolSurface.Panel)
                           && !tools.HasCommands(PluginToolSurface.Zone),
                    "no surface reports the removed commands");
                tools.Attach(hostMenu, PluginToolSurface.Tray);
                item.Check(hostMenu.Items.Count == 0,
                    "a menu that already carried the submenu drops it once the commands are gone");

                // 先备份后还原：别把自测热键留在（隔离的）用户配置里。
                config = _configService.Load();
                config.PluginToolHotkeys = hotkeyBackup;
                _configService.Save(config);
                RefreshPluginToolHotkeys();
                break;
            }
            case "tool-ui":
            {
                // 「插件工具」页（UI 批次）：两条合成命令 → 侧栏入口进页 / 列表两行 / 截图 →
                // 直接写配置分配热键（录制走 WH_KEYBOARD_LL，自测台里没法可靠模拟按键）→
                // 清理配置与命令后空态必须出现。配置与合成命令无论断言成败都要还原。
                var tools = PluginToolService.Instance;
                tools.Clear();

                var record = new PluginRecord
                {
                    Id = "lab.toolui",
                    Version = "1.0.0",
                    Kind = "tool",
                    Enabled = true,
                    Source = "lab",
                    Signature = "unsigned"
                };
                var manifest = new PluginManifest
                {
                    SchemaVersion = 1,
                    Id = "lab.toolui",
                    Name = new LocalizedText { Zh = "自测工具界面", En = "Lab tool UI" },
                    Summary = new LocalizedText { Zh = "自测工具界面", En = "lab tool ui" },
                    Version = "1.0.0",
                    Author = new PluginAuthor { Name = "Three-Freezen", Github = "Three-Freezen" },
                    Repo = "Three-Freezen/DeskOrder-Plugin-Lab",
                    License = "Apache-2.0",
                    Kind = "tool",
                    ApiVersion = 3,
                    MinAppVersion = "1.0.0",
                    Entry = new PluginEntry { Assembly = "lib/Lab.ToolUi.dll", Type = "Lab.ToolUi.Plugin" },
                    Icon = "icon.png"
                };
                int commandRuns = 0;
                PluginToolCommand MakeCommand(string id, string display) => new()
                {
                    Id = id,
                    DisplayName = display,
                    // 三条菜单入口全开：副标题要能拼出「托盘 / 面板 / 分区」。
                    Menus = PluginToolSurface.Tray | PluginToolSurface.Panel | PluginToolSurface.Zone,
                    Execute = _ => { commandRuns++; return Task.CompletedTask; }
                };
                tools.Publish(record, manifest, new[]
                {
                    MakeCommand("one", "Lab command one"),
                    MakeCommand("two", "Lab command two")
                });
                item.Check(tools.Commands.Count == 2, "two synthetic tool commands are published");
                item.Check(commandRuns == 0, "publishing a tool command does not run it");

                var hotkeyBackup = _configService!.Load().PluginToolHotkeys;
                PluginToolsPage? page = null;
                ManagementWindow? window = null;
                try
                {
                    EnsureManagementWindow();
                    window = ManagementWindow;
                    item.Check(window != null, "management window exists");
                    if (window != null)
                    {
                        window.Show();
                        window.ShowSection("plugintools");
                        await Task.Delay(900);
                        item.Check(window.MainContent.Content is PluginToolsPage,
                            "the plugin tools page is shown");
                        page = window.MainContent.Content as PluginToolsPage;
                    }
                    if (page != null)
                    {
                        item.Check(page.RowCount == 2, "the page lists both synthetic commands");
                        SaveScreenshot(window!, Path.Combine(dir, "shot-plugin-tools.png"));
                        item.Check(File.Exists(Path.Combine(dir, "shot-plugin-tools.png")),
                            "plugin tools screenshot written");

                        // Ctrl+Alt+Shift+F10（MOD_ALT|MOD_CONTROL|MOD_SHIFT = 0x0007，VK_F10 = 0x79），
                        // 与 tool 用例的 F9 错开：两条用例可能会被连着跑。
                        var config = _configService.Load();
                        config.PluginToolHotkeys = new List<PluginToolHotkey>
                        {
                            new() { PluginId = "lab.toolui", CommandId = "one", Modifiers = 0x0007, Key = 0x79, Enabled = true }
                        };
                        _configService.Save(config);
                        RefreshPluginToolHotkeys();
                        page.RefreshList();
                        item.Check(PluginToolHotkeyMapForLab.Count >= 1,
                            "the assigned plugin tool hotkey is registered");
                        SaveScreenshot(window!, Path.Combine(dir, "shot-plugin-tools-hotkey.png"));
                        item.Check(File.Exists(Path.Combine(dir, "shot-plugin-tools-hotkey.png")),
                            "assigned hotkey screenshot written");
                    }
                    else
                    {
                        item.Check(false, "the plugin tools page is available for the list checks");
                    }
                }
                finally
                {
                    // 清理：还原配置 → 重新注册 → 撤销合成命令。断言失败/提前抛出也要走到这里，
                    // 隔离 data-root 里同样不能留下自测热键。
                    var config = _configService.Load();
                    config.PluginToolHotkeys = hotkeyBackup;
                    _configService.Save(config);
                    RefreshPluginToolHotkeys();
                    tools.RemovePlugin("lab.toolui");
                }

                if (page != null && window != null)
                {
                    page.RefreshList();
                    await Task.Delay(300);
                    item.Check(page.RowCount == 0, "the list is empty after removing the tool plugin");
                    item.Check(page.EmptyStateVisibility == Visibility.Visible, "the empty state is visible");
                    item.Check(PluginToolHotkeyMapForLab.Count == 0,
                        "clearing the assignment drops the registered hotkey");
                    item.Check(page.RestartHintVisibility == Visibility.Collapsed,
                        "the restart hint stays hidden while no tool plugin is enabled");

                    // 「已安装且启用的 tool 插件，但一条命令都没注册」= 装了/启用了还没重启：
                    // 空态里要追加「重启后生效」。直接塞一条合成记录（隔离 data-root，且
                    // PluginService.Records 没有任何 CollectionChanged 订阅者）。
                    var pendingTool = new PluginRecord
                    {
                        Id = "lab.toolui.pending",
                        Version = "1.0.0",
                        Kind = "tool",
                        Enabled = true,
                        Source = "lab",
                        Signature = "unsigned"
                    };
                    PluginService.Instance.Records.Add(pendingTool);
                    try
                    {
                        page.RefreshList();
                        item.Check(page.RestartHintVisibility == Visibility.Visible,
                            "the restart hint appears when an enabled tool plugin has no commands");
                    }
                    finally
                    {
                        PluginService.Instance.Records.Remove(pendingTool);
                        page.RefreshList();
                        item.Check(page.RestartHintVisibility == Visibility.Collapsed,
                            "the restart hint clears once the pending tool plugin is removed");
                    }
                }
                else
                {
                    item.Check(false, "the plugin tools page survives cleanup for the empty state check");
                }
                break;
            }
            default:
                item.Check(false, "unknown case: " + name);
                break;
        }
    }

    int _labRegistryStamp;

    /// <summary>
    /// 自测台专用的「registry updatedAt」，严格递增。
    /// <see cref="PluginBlocklist.SaveFromRegistry"/> 只允许「来源确实比本地新」的名单整体替换，
    /// 否则按单调保护取并集；自测台每次写入都必须比上一次新，否则清空/解禁会静默失效。
    /// </summary>
    DateTime NextLabRegistryStamp() => DateTime.UtcNow.AddSeconds(++_labRegistryStamp);

    async Task<PluginInstallResult?> InstallSampleAsync(string dir)
    {
        var package = FindSamplePackage(dir);
        if (package == null) return null;
        var install = await PluginService.Instance.Installer!.InstallFromFileAsync(package);
        PluginService.Instance.RefreshRecords();
        return install;
    }

    static string? FindSamplePackage(string dir)
    {
        foreach (var candidate in new[]
                 {
                     Path.Combine(dir, "hello-widget-1.0.0.dopkg"),
                     Path.Combine(dir, "packages", "hello-widget-1.0.0.dopkg")
                 })
        {
            if (File.Exists(candidate)) return candidate;
        }
        return null;
    }

    async Task EnsureEnabledAsync(string dir)
    {
        var service = PluginService.Instance;
        if (service.GetRecord(SamplePluginId) == null) await InstallSampleAsync(dir);
        service.SetEnabled(SamplePluginId, true);
        service.ReloadForLab();
    }

    async Task EnsureLanguagePackAsync(string dir)
    {
        var service = PluginService.Instance;
        if (service.GetRecord(LanguagePackId) == null)
        {
            var package = Path.Combine(dir, "lang-ja-1.0.0.dopkg");
            if (!File.Exists(package))
                throw new FileNotFoundException("lang-ja package is missing; run tools\\build-labs.ps1", package);
            var result = await service.Installer!.InstallFromFileAsync(package);
            if (!result.Success)
            {
                // 诊断用：宿主词条表在失败瞬间到底有多少条（i18n 文件被并发改动过）。
                var hostKeys = LocalizationService.ReadHostSource().Count;
                throw new InvalidOperationException(
                    $"cannot install the language pack (host keys={hostKeys}): " + result.Error);
            }
            service.RefreshRecords();
        }
        if (service.GetRecord(LanguagePackId)?.Enabled != true)
            service.SetEnabled(LanguagePackId, true);
    }

    async Task EnsureAppearancePackAsync(string dir)
    {
        var service = PluginService.Instance;
        if (service.GetRecord(AppearancePackId) == null)
        {
            var package = Path.Combine(dir, "appearance-aurora-1.0.0.dopkg");
            if (!File.Exists(package))
                throw new FileNotFoundException("appearance pack is missing; run tools\\build-labs.ps1", package);
            var result = await service.Installer!.InstallFromFileAsync(package);
            if (!result.Success)
                throw new InvalidOperationException("cannot install the appearance pack: " + result.Error);
            service.RefreshRecords();
        }
        if (service.GetRecord(AppearancePackId)?.Enabled != true)
            service.SetEnabled(AppearancePackId, true);
    }

    static PluginWidget? EnsureWidget(PluginService service)
    {
        var widget = service.Widgets.FirstOrDefault(w => w.PluginId == SamplePluginId);
        if (widget != null) return widget;
        return service.CreateWidget(SamplePluginId, "hello");
    }

    static void CreateZipSlipPackage(string path)
    {
        if (File.Exists(path)) File.Delete(path);
        using var zip = ZipFile.Open(path, ZipArchiveMode.Create);
        var entry = zip.CreateEntry("../evil.txt");
        using (var writer = new StreamWriter(entry.Open()))
        {
            writer.Write("escaped");
        }
        zip.CreateEntry("plugin.json");
    }

    static void SaveScreenshot(Window window, string path)
    {
        if (window.Content is not FrameworkElement root) return;
        root.UpdateLayout();
        var width = (int)Math.Max(1, root.ActualWidth);
        var height = (int)Math.Max(1, root.ActualHeight);
        var bitmap = new RenderTargetBitmap(width, height, 96, 96, PixelFormats.Pbgra32);
        // 透明窗口（坞 / 玻璃分区）的背板由 DWM 画，RTB 抓不到；先铺一张深色底再叠
        // 视觉树，截图里至少能看清白色图标和描边，不会得到一张全透明 PNG。
        var visual = new DrawingVisual();
        using (var dc = visual.RenderOpen())
        {
            dc.DrawRectangle(new SolidColorBrush(Color.FromRgb(0x20, 0x24, 0x2C)), null,
                new Rect(0, 0, width, height));
            // ponytail: VisualBrush 默认按「后代包围盒」取 Viewbox —— 坞这种细长窗口里只要
            // 有一个元素把包围盒撑宽（折叠空提示等），整张图就会被横向挤压（纵向坞截图里
            // 图标被压成 2px 就是这么来的）。显式用绝对单位的 (0,0,w,h) 做 Viewbox。
            dc.DrawRectangle(new VisualBrush(root)
            {
                Stretch = Stretch.Fill,
                AlignmentX = AlignmentX.Left,
                AlignmentY = AlignmentY.Top,
                ViewboxUnits = BrushMappingMode.Absolute,
                Viewbox = new Rect(0, 0, width, height),
            }, null, new Rect(0, 0, width, height));
        }
        bitmap.Render(visual);
        var encoder = new PngBitmapEncoder();
        encoder.Frames.Add(BitmapFrame.Create(bitmap));
        using var stream = File.Create(path);
        encoder.Save(stream);
    }
}
#endif
