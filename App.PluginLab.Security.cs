#if DEBUG
using System;
using System.Collections.Generic;
using System.IO;
using System.IO.Compression;
using System.Linq;
using System.Text;
using System.Text.Json;
using System.Threading.Tasks;
using System.Windows;
using System.Windows.Media;
using System.Windows.Media.Imaging;
using DesktopZones.Helpers;
using DesktopZones.Models;
using DesktopZones.Services;

namespace DesktopZones;

/// <summary>
/// 安装/索引安全加固的自测用例（v1.0.29 批次）。与 <c>App.PluginLab.cs</c> 同属一个 partial
/// 类：主文件只负责分派，用例实现放在这里，避免两个并行开发流同时改同一个文件。
///
/// 覆盖：registry 条目严格校验、sha256 必填、kind 一致性、权限越界、lib 程序集引用审计、
/// 图标/预览校验、禁用名单来源时间持久化。全部用临时目录现造包 / registry / blocked.json，
/// 不联网（InstallFromCatalogAsync 的用例在下载之前就返回）。
/// </summary>
public partial class App
{
    const string LabValidSha = "aaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaa";

    async Task RunSecurityLabCaseAsync(LabCase item, string name, string dir)
    {
        var service = PluginService.Instance;
        var installer = service.Installer;
        if (installer == null) { item.Check(false, "the plugin installer is initialized"); return; }

        // dist 里的 hello 包提供一份「干净入口 DLL」：现造一个能通过入口类型检查的程序集太重，
        // 直接复用它，把用例重心放在被审计的那一层。
        var helloPackage = Path.Combine(dir, "hello-widget-1.0.0.dopkg");
        item.Check(File.Exists(helloPackage), "the hello fixture package is available");
        if (!File.Exists(helloPackage)) return;
        var entryDll = Path.Combine(dir, "sec-HelloWidget.dll");
        if (!ExtractPackageEntry(helloPackage, "lib/HelloWidget.dll", entryDll))
        {
            item.Check(false, "the hello entry assembly is extracted");
            return;
        }

        // ── 1/4. registry 条目严格校验：不合规条目丢弃，合法条目保留 ──
        var catalog = new PluginCatalogService();
        item.Check(catalog.ParseRegistry(BuildLabRegistry(BuildLabRegistryEntry("lab.sec-parse-ok")))
                       .Entries.Count == 1,
            "a well-formed registry entry is kept");
        item.Check(catalog.ParseRegistry(BuildLabRegistry(BuildLabRegistryEntry("lab.sec-parse-ok", sha256: "")))
                       .Entries.Count == 0,
            "an entry without sha256 is dropped");
        item.Check(catalog.ParseRegistry(BuildLabRegistry(BuildLabRegistryEntry("lab.sec-parse-ok", sha256: "deadbeef")))
                       .Entries.Count == 0,
            "an entry with a malformed sha256 is dropped");
        item.Check(catalog.ParseRegistry(BuildLabRegistry(BuildLabRegistryEntry("lab.sec-parse-ok", size: 0)))
                       .Entries.Count == 0,
            "an entry with size 0 is dropped");
        item.Check(catalog.ParseRegistry(
                       BuildLabRegistry(BuildLabRegistryEntry("lab.sec-parse-ok",
                           size: PluginPackages.MaxPackageBytes + 1)))
                       .Entries.Count == 0,
            "an entry larger than 50 MB is dropped");
        item.Check(catalog.ParseRegistry(BuildLabRegistry(BuildLabRegistryEntry("LAB.SEC.BAD")))
                       .Entries.Count == 0,
            "an entry with an uppercase plugin id is dropped");
        item.Check(catalog.ParseRegistry(BuildLabRegistry(BuildLabRegistryEntry("..")))
                       .Entries.Count == 0,
            "an entry with a traversal plugin id is dropped");
        item.Check(catalog.ParseRegistry(BuildLabRegistry(BuildLabRegistryEntry("lab.sec-parse-ok", version: "1.0")))
                       .Entries.Count == 0,
            "an entry with a non-SemVer version is dropped");
        item.Check(catalog.ParseRegistry(BuildLabRegistry(BuildLabRegistryEntry("lab.sec-parse-ok", kind: "bogus")))
                       .Entries.Count == 0,
            "an entry with an unknown kind is dropped");
        item.Check(catalog.ParseRegistry(
                       BuildLabRegistry(BuildLabRegistryEntry("lab.sec-parse-ok", minAppVersion: "banana")))
                       .Entries.Count == 0,
            "an entry with a non-SemVer minAppVersion is dropped");
        var mixedRegistry = catalog.ParseRegistry(BuildLabRegistry(
            BuildLabRegistryEntry("lab.sec-parse-good"),
            BuildLabRegistryEntry("lab.sec-parse-nosha", sha256: ""),
            BuildLabRegistryEntry("lab.sec-parse-bigid", size: long.MaxValue),
            BuildLabRegistryEntry("lab.sec-parse-badver", version: "1.0")));
        item.Check(mixedRegistry.Error == null && mixedRegistry.Entries.Count == 1
                   && mixedRegistry.Entries[0].Id == "lab.sec-parse-good",
            "one malformed entry does not empty the whole registry");

        // 样例 registry 必须继续全部通过：条目过期/过期严格化不能把正版市场清空。
        var exampleRegistry = Path.Combine(dir, "registry.json");
        if (File.Exists(exampleRegistry))
        {
            var parsedExample = catalog.ParseRegistry(File.ReadAllBytes(exampleRegistry));
            // 期望条目数从夹具文件算：加示例包（v1.1 的 tool-demo）时不用改断言。
            var expectedCount = System.Text.Json.JsonDocument.Parse(File.ReadAllText(exampleRegistry))
                .RootElement.GetProperty("plugins").GetArrayLength();
            item.Check(parsedExample.Error == null && parsedExample.Entries.Count == expectedCount,
                $"the shipped example registry keeps all {expectedCount} entries (kept {parsedExample.Entries.Count})");
        }
        else
        {
            item.Check(false, "registry.json is staged in the lab directory");
        }

        // ── 1. 安装器对缺 sha256 的市场条目直接拒装（不发起下载） ──
        var noShaEntry = BuildLabCatalogEntry("lab.sec-nosha", "");
        var noShaResult = await installer.InstallFromCatalogAsync(noShaEntry);
        item.Check(!noShaResult.Success, "a catalog entry without sha256 is refused");
        item.Check((noShaResult.Error ?? "").Contains("sha256", StringComparison.OrdinalIgnoreCase),
            "the refusal explains that sha256 is missing");
        var badShaEntry = BuildLabCatalogEntry("lab.sec-badsha", "abc123");
        var badShaResult = await installer.InstallFromCatalogAsync(badShaEntry);
        item.Check(!badShaResult.Success
                   && (badShaResult.Error ?? "").Contains("sha256", StringComparison.OrdinalIgnoreCase),
            "a catalog entry with a malformed sha256 is refused");

        // ── 2. kind 一致性：确认弹窗按 registry 渲染，包内类型不同必须拒装 ──
        var widgetPackage = BuildLabPackage(dir, "sec-widget.dopkg", new LabPackageOptions
        {
            Id = "lab.sec-widget"
        }, entryDll);
        var kindMismatch = await InstallLabPackageAsync(installer, widgetPackage,
            id: "lab.sec-widget", declaredPermissions: Array.Empty<string>(),
            expectedKind: PluginKind.Language);
        item.Check(!kindMismatch.Success, "a package whose kind differs from the registry entry is refused");
        item.Check((kindMismatch.Error ?? "").Contains("kind", StringComparison.OrdinalIgnoreCase),
            "the refusal explains the kind mismatch");

        // ── 3. 权限越界：registry 没声明 permissions 时按空集处理 ──
        var networkPackage = BuildLabPackage(dir, "sec-permission.dopkg", new LabPackageOptions
        {
            Id = "lab.sec-permission",
            Permissions = new List<string> { "network" }
        }, entryDll);
        var undeclared = await InstallLabPackageAsync(installer, networkPackage,
            id: "lab.sec-permission", declaredPermissions: null, expectedKind: PluginKind.Widget);
        item.Check(!undeclared.Success,
            "a package that requests permissions the listing does not declare is refused");
        item.Check((undeclared.Error ?? "").Contains("network", StringComparison.OrdinalIgnoreCase),
            "the refusal names the undeclared permission");

        // ── 4. 权限不越界时正常安装（registry 声明空集 + 包内无权限） ──
        var cleanPackage = BuildLabPackage(dir, "sec-clean.dopkg", new LabPackageOptions
        {
            Id = "lab.sec-clean"
        }, entryDll);
        var cleanInstall = await InstallLabPackageAsync(installer, cleanPackage,
            id: "lab.sec-clean", declaredPermissions: Array.Empty<string>(),
            expectedKind: PluginKind.Widget);
        item.Check(cleanInstall.Success, "a package without extra permissions installs fine"
                                         + (cleanInstall.Success ? "" : ": " + cleanInstall.Error));
        CleanupLabPlugin(service, "lab.sec-clean");

        // ── 5. 程序集审计扩展到包内所有 DLL ──
        // 5a. lib\ 里第二个 DLL 引用 DeskOrder：入口 DLL 干净也要拒。
        var badRefDll = Path.Combine(dir, "sec-BadRefWidget.dll");
        var badRefPackage = Path.Combine(dir, "bad-ref-1.0.0.dopkg");
        if (File.Exists(badRefPackage) && ExtractPackageEntry(badRefPackage, "lib/BadRefWidget.dll", badRefDll))
        {
            var libBadRef = BuildLabPackage(dir, "sec-lib-badref.dopkg", new LabPackageOptions
            {
                Id = "lab.sec-lib-badref",
                CustomizeStage = stage => File.Copy(badRefDll,
                    Path.Combine(stage, "lib", "BadRefWidget.dll"), overwrite: true)
            }, entryDll);
            var libBadRefResult = await InstallLabPackageAsync(installer, libBadRef,
                id: "lab.sec-lib-badref", declaredPermissions: Array.Empty<string>(),
                expectedKind: PluginKind.Widget);
            item.Check(!libBadRefResult.Success, "a lib\\ helper DLL that references DeskOrder is refused");
            item.Check((libBadRefResult.Error ?? "").Contains("must not reference DeskOrder"),
                "the audit explains the rejected host reference");
        }
        else
        {
            item.Check(false, "the bad-ref fixture package is staged");
        }

        // 5b. 假 DLL（纯文本）：拒为「不是托管程序集」。
        var fakeDll = BuildLabPackage(dir, "sec-lib-raw.dopkg", new LabPackageOptions
        {
            Id = "lab.sec-lib-raw",
            CustomizeStage = stage => File.WriteAllText(
                Path.Combine(stage, "lib", "notmanaged.dll"), "this is not a .NET assembly")
        }, entryDll);
        var fakeDllResult = await InstallLabPackageAsync(installer, fakeDll,
            id: "lab.sec-lib-raw", declaredPermissions: Array.Empty<string>(),
            expectedKind: PluginKind.Widget);
        item.Check(!fakeDllResult.Success, "a file that pretends to be a DLL is refused");
        item.Check((fakeDllResult.Error ?? "").Contains("not a managed assembly"),
            "the refusal says the file is not a managed assembly");

        // 5c. 原生 DLL：不加载、不拒装，只记日志。
        var nativeDll = FindNativeLabDll();
        if (nativeDll != null)
        {
            var nativePackage = BuildLabPackage(dir, "sec-lib-native.dopkg", new LabPackageOptions
            {
                Id = "lab.sec-lib-native",
                CustomizeStage = stage => File.Copy(nativeDll,
                    Path.Combine(stage, "lib", "native.dll"), overwrite: true)
            }, entryDll);
            var nativeResult = await InstallLabPackageAsync(installer, nativePackage,
                id: "lab.sec-lib-native", declaredPermissions: Array.Empty<string>(),
                expectedKind: PluginKind.Widget);
            item.Check(nativeResult.Success, "a native DLL in the package is allowed but not inspected"
                                              + (nativeResult.Success ? "" : ": " + nativeResult.Error));
            CleanupLabPlugin(service, "lab.sec-lib-native");
        }
        else
        {
            item.Check(false, "a native DLL is available for the audit case");
        }

        // ── 6. 图标 / 预览校验 ──
        var iconMissing = BuildLabPackage(dir, "sec-icon-missing.dopkg", new LabPackageOptions
        {
            Id = "lab.sec-icon-missing",
            IncludeIcon = false
        }, entryDll);
        var iconMissingResult = await InstallLabPackageAsync(installer, iconMissing,
            id: "lab.sec-icon-missing", declaredPermissions: Array.Empty<string>(),
            expectedKind: PluginKind.Widget);
        item.Check(!iconMissingResult.Success
                   && (iconMissingResult.Error ?? "").Contains("icon", StringComparison.OrdinalIgnoreCase),
            "a missing icon is refused with an icon error");

        var bigIcon = CreatePng(16, 16).Concat(new byte[300 * 1024]).ToArray();
        var iconTooBig = BuildLabPackage(dir, "sec-icon-big.dopkg", new LabPackageOptions
        {
            Id = "lab.sec-icon-big",
            IconBytes = bigIcon
        }, entryDll);
        var iconTooBigResult = await InstallLabPackageAsync(installer, iconTooBig,
            id: "lab.sec-icon-big", declaredPermissions: Array.Empty<string>(),
            expectedKind: PluginKind.Widget);
        item.Check(!iconTooBigResult.Success
                   && (iconTooBigResult.Error ?? "").Contains("icon", StringComparison.OrdinalIgnoreCase),
            "an icon larger than 256 KB is refused");

        var iconNotAnImage = BuildLabPackage(dir, "sec-icon-bad.dopkg", new LabPackageOptions
        {
            Id = "lab.sec-icon-bad",
            IconBytes = Encoding.UTF8.GetBytes("this is not an image")
        }, entryDll);
        var iconNotAnImageResult = await InstallLabPackageAsync(installer, iconNotAnImage,
            id: "lab.sec-icon-bad", declaredPermissions: Array.Empty<string>(),
            expectedKind: PluginKind.Widget);
        item.Check(!iconNotAnImageResult.Success
                   && (iconNotAnImageResult.Error ?? "").Contains("icon", StringComparison.OrdinalIgnoreCase),
            "an icon that cannot be decoded is refused");

        var fivePreviews = BuildLabPackage(dir, "sec-preview-five.dopkg", new LabPackageOptions
        {
            Id = "lab.sec-preview-five",
            Previews = Enumerable.Range(1, 5).Select(index => $"previews/{index}.png").ToList(),
            CustomizeStage = stage => WriteLabPreviews(stage, 5)
        }, entryDll);
        var fivePreviewsResult = await InstallLabPackageAsync(installer, fivePreviews,
            id: "lab.sec-preview-five", declaredPermissions: Array.Empty<string>(),
            expectedKind: PluginKind.Widget);
        item.Check(!fivePreviewsResult.Success
                   && (fivePreviewsResult.Error ?? "").Contains("preview", StringComparison.OrdinalIgnoreCase),
            "more than 4 preview images are refused");

        var missingPreview = BuildLabPackage(dir, "sec-preview-missing.dopkg", new LabPackageOptions
        {
            Id = "lab.sec-preview-missing",
            Previews = new List<string> { "previews/1.png", "previews/2.png" },
            CustomizeStage = stage => WriteLabPreviews(stage, 1)
        }, entryDll);
        var missingPreviewResult = await InstallLabPackageAsync(installer, missingPreview,
            id: "lab.sec-preview-missing", declaredPermissions: Array.Empty<string>(),
            expectedKind: PluginKind.Widget);
        item.Check(!missingPreviewResult.Success
                   && (missingPreviewResult.Error ?? "").Contains("preview image #2"),
            "a missing preview is refused and the index is named");

        var badPreview = BuildLabPackage(dir, "sec-preview-bad.dopkg", new LabPackageOptions
        {
            Id = "lab.sec-preview-bad",
            Previews = new List<string> { "previews/1.png" },
            CustomizeStage = stage =>
            {
                Directory.CreateDirectory(Path.Combine(stage, "previews"));
                File.WriteAllText(Path.Combine(stage, "previews", "1.png"), "broken preview");
            }
        }, entryDll);
        var badPreviewResult = await InstallLabPackageAsync(installer, badPreview,
            id: "lab.sec-preview-bad", declaredPermissions: Array.Empty<string>(),
            expectedKind: PluginKind.Widget);
        item.Check(!badPreviewResult.Success
                   && (badPreviewResult.Error ?? "").Contains("preview image #1"),
            "a preview that cannot be decoded is refused and the index is named");

        var fourPreviews = BuildLabPackage(dir, "sec-preview-four.dopkg", new LabPackageOptions
        {
            Id = "lab.sec-preview-four",
            Previews = Enumerable.Range(1, 4).Select(index => $"previews/{index}.png").ToList(),
            CustomizeStage = stage => WriteLabPreviews(stage, 4)
        }, entryDll);
        var fourPreviewsResult = await InstallLabPackageAsync(installer, fourPreviews,
            id: "lab.sec-preview-four", declaredPermissions: Array.Empty<string>(),
            expectedKind: PluginKind.Widget);
        item.Check(fourPreviewsResult.Success, "four valid preview images are accepted"
                                               + (fourPreviewsResult.Success ? "" : ": " + fourPreviewsResult.Error));
        CleanupLabPlugin(service, "lab.sec-preview-four");

        // ── 7. 禁用名单：来源时间落盘、重启后单调性保护仍生效 ──
        var localSource = DateTime.UtcNow.AddDays(-1);
        PluginPaths.EnsureCreated();
        File.WriteAllText(PluginBlocklist.FilePath, JsonSerializer.Serialize(new Dictionary<string, object?>
        {
            ["updatedAtUtc"] = DateTime.UtcNow.AddHours(-1),
            ["sourceUpdatedAtUtc"] = localSource,
            ["entries"] = new object[]
            {
                new Dictionary<string, object?>
                {
                    ["id"] = "lab.sec-keeplocal",
                    ["version"] = "1.0.0",
                    ["reason"] = new Dictionary<string, string>
                    {
                        ["zh"] = "自测：本地缓存的封禁",
                        ["en"] = "lab: locally cached block"
                    }
                }
            }
        }));
        PluginBlocklist.Reload();
        item.Check(PluginBlocklist.IsBlocked("lab.sec-keeplocal", "1.0.0"),
            "the locally cached blocklist entry is loaded from disk");
        item.Check((PluginBlocklist.SourceUpdatedAtUtc - localSource).Duration() < TimeSpan.FromSeconds(1),
            "Load() restores SourceUpdatedAtUtc");
        // 旧 registry（比本地来源时间更早）不能清掉本地封禁 —— 以前重启后 localSourceKnown
        // 永远为 false，这里会直接整体替换成空名单。
        PluginBlocklist.SaveFromRegistry(new List<BlockedPlugin>(), localSource.AddDays(-1));
        item.Check(PluginBlocklist.IsBlocked("lab.sec-keeplocal", "1.0.0"),
            "an older registry cannot clear a locally cached block after restart");
        // 更新 registry 能正常撤回封禁（单调保护不能把名单变成永久封禁）。
        PluginBlocklist.SaveFromRegistry(new List<BlockedPlugin>(), DateTime.UtcNow.AddDays(1));
        item.Check(!PluginBlocklist.IsBlocked("lab.sec-keeplocal", "1.0.0"),
            "a newer registry can still retire a block");
    }

    /// <summary>检查安装是否落盘成功后清理记录/目录，避免污染同一 lab 目录的后续用例。</summary>
    static void CleanupLabPlugin(PluginService service, string pluginId)
    {
        try { service.Uninstall(pluginId, deleteData: true, out _); } catch { }
    }

    // ── 现造 .dopkg / registry / blocked.json 的辅助 ──

    sealed class LabPackageOptions
    {
        public string Id { get; init; } = "lab.sec-widget";
        public string Kind { get; init; } = "widget";
        public List<string>? Permissions { get; init; }
        public List<string>? Previews { get; init; }
        public bool IncludeIcon { get; init; } = true;
        public byte[]? IconBytes { get; init; }
        public Action<string>? CustomizeStage { get; init; }
    }

    static string BuildLabPackage(string dir, string fileName, LabPackageOptions options, string entryDll)
    {
        var stage = Path.Combine(dir, "sec-stage-" + Guid.NewGuid().ToString("N"));
        Directory.CreateDirectory(Path.Combine(stage, "lib"));
        File.Copy(entryDll, Path.Combine(stage, "lib", "HelloWidget.dll"), overwrite: true);
        var manifest = new Dictionary<string, object?>
        {
            ["schemaVersion"] = 1,
            ["id"] = options.Id,
            ["name"] = new Dictionary<string, string> { ["zh"] = "安全自测", ["en"] = "Security lab" },
            ["summary"] = new Dictionary<string, string> { ["zh"] = "安全自测用例。", ["en"] = "Security lab case." },
            ["version"] = "1.0.0",
            ["author"] = new Dictionary<string, string> { ["name"] = "lab", ["github"] = "lab" },
            ["repo"] = "lab/DeskOrder-SecurityLab",
            ["license"] = "MIT",
            ["kind"] = options.Kind,
            ["apiVersion"] = 1,
            ["minAppVersion"] = "1.0.0",
            ["entry"] = new Dictionary<string, string>
            {
                ["assembly"] = "lib/HelloWidget.dll",
                ["type"] = "HelloWidget.HelloPlugin"
            },
            ["widgets"] = new object[]
            {
                new Dictionary<string, object?>
                {
                    ["id"] = "hello",
                    ["name"] = new Dictionary<string, string> { ["zh"] = "安全挂件", ["en"] = "Security widget" }
                }
            },
            ["icon"] = "icon.png"
        };
        if (options.Permissions != null) manifest["permissions"] = options.Permissions;
        if (options.Previews != null) manifest["previews"] = options.Previews;
        File.WriteAllText(Path.Combine(stage, "plugin.json"), JsonSerializer.Serialize(manifest));
        if (options.IncludeIcon)
            File.WriteAllBytes(Path.Combine(stage, "icon.png"), options.IconBytes ?? CreatePng(16, 16));
        options.CustomizeStage?.Invoke(stage);
        var package = Path.Combine(dir, fileName);
        PluginPackages.CreatePackage(stage, package);
        try { Directory.Delete(stage, recursive: true); } catch { }
        return package;
    }

    static void WriteLabPreviews(string stage, int count)
    {
        Directory.CreateDirectory(Path.Combine(stage, "previews"));
        for (var index = 1; index <= count; index++)
            File.WriteAllBytes(Path.Combine(stage, "previews", index + ".png"), CreatePng(16, 16));
    }

    /// <summary>用 WPF 编码器现造一张小 PNG（不依赖磁盘上的素材）。</summary>
    static byte[] CreatePng(int width, int height)
    {
        var bitmap = new RenderTargetBitmap(width, height, 96, 96, PixelFormats.Pbgra32);
        var visual = new DrawingVisual();
        using (var dc = visual.RenderOpen())
            dc.DrawRectangle(new SolidColorBrush(Color.FromRgb(0x2E, 0x86, 0xDE)), null,
                new Rect(0, 0, width, height));
        bitmap.Render(visual);
        var encoder = new PngBitmapEncoder();
        encoder.Frames.Add(BitmapFrame.Create(bitmap));
        using var stream = new MemoryStream();
        encoder.Save(stream);
        return stream.ToArray();
    }

    static bool ExtractPackageEntry(string packagePath, string entryName, string destination)
    {
        try
        {
            using var archive = ZipFile.OpenRead(packagePath);
            var entry = archive.GetEntry(entryName)
                        ?? archive.Entries.FirstOrDefault(e =>
                            string.Equals(e.FullName.Replace('\\', '/'), entryName, StringComparison.OrdinalIgnoreCase));
            if (entry == null) return false;
            Directory.CreateDirectory(Path.GetDirectoryName(destination)!);
            entry.ExtractToFile(destination, overwrite: true);
            return true;
        }
        catch
        {
            return false;
        }
    }

    /// <summary>挑一个系统自带的原生 DLL 当审计夹具（合法 PE、没有 CorHeader）。</summary>
    static string? FindNativeLabDll()
    {
        foreach (var candidate in new[]
                 {
                     Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.System), "version.dll"),
                     Path.Combine(Path.GetDirectoryName(typeof(object).Assembly.Location) ?? "", "clrjit.dll")
                 })
        {
            try { if (File.Exists(candidate) && new FileInfo(candidate).Length > 0) return candidate; }
            catch { }
        }
        return null;
    }

    static PluginCatalogEntry BuildLabCatalogEntry(string id, string sha256)
        => new()
        {
            Id = id,
            Kind = "widget",
            Name = new LocalizedText { Zh = "安全条目", En = "Security entry" },
            Summary = new LocalizedText { Zh = "自测条目。", En = "Lab entry." },
            Author = new PluginAuthor { Name = "lab", Github = "lab" },
            Repo = "lab/DeskOrder-SecurityLab",
            Version = "1.0.0",
            MinAppVersion = "1.0.0",
            License = "MIT",
            DownloadUrl = "https://example.invalid/lab.dopkg",
            Permissions = new List<string>(),
            Size = 1024,
            Sha256 = sha256
        };

    static byte[] BuildLabRegistry(params Dictionary<string, object?>[] entries)
        => Encoding.UTF8.GetBytes(JsonSerializer.Serialize(new Dictionary<string, object?>
        {
            ["schemaVersion"] = 1,
            ["updatedAt"] = "2026-11-01T00:00:00Z",
            ["blocked"] = Array.Empty<object>(),
            ["plugins"] = entries
        }));

    static Dictionary<string, object?> BuildLabRegistryEntry(string id, string version = "1.0.0",
        string sha256 = LabValidSha, long size = 1024, string kind = "widget",
        string minAppVersion = "1.0.0")
        => new()
        {
            ["id"] = id,
            ["kind"] = kind,
            ["name"] = new Dictionary<string, string> { ["zh"] = "安全条目", ["en"] = "Security entry" },
            ["summary"] = new Dictionary<string, string> { ["zh"] = "自测条目。", ["en"] = "Lab entry." },
            ["author"] = new Dictionary<string, string> { ["name"] = "lab", ["github"] = "lab" },
            ["repo"] = "lab/DeskOrder-SecurityLab",
            ["version"] = version,
            ["license"] = "MIT",
            ["minAppVersion"] = minAppVersion,
            ["downloadUrl"] = "https://example.invalid/lab.dopkg",
            ["permissions"] = Array.Empty<string>(),
            ["size"] = size,
            ["sha256"] = sha256
        };

    /// <summary>走内部安装路径（与 InstallFromCatalogAsync 的下载后流程一致），在后台线程跑。</summary>
    static Task<PluginInstallResult> InstallLabPackageAsync(PluginInstaller installer, string package,
        string id, IReadOnlyList<string>? declaredPermissions, PluginKind expectedKind)
    {
        var sha = PluginPackages.Sha256OfFile(package);
        return Task.Run(() => installer.InstallPackageCore(package, "market", official: false,
            expectedSha: sha, expectedId: id, expectedVersion: "1.0.0",
            trust: TrustVerdict.Unsigned, declaredPermissions: declaredPermissions,
            enabledOverride: null, expectedKind: expectedKind));
    }
}
#endif
