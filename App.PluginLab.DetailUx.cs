#if DEBUG
using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Threading.Tasks;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Controls.Primitives;
using DeskOrder.PluginApi;
using DesktopZones.Helpers;
using DesktopZones.Models;
using DesktopZones.Services;
using DesktopZones.Views;
using DesktopZones.Views.Pages;

namespace DesktopZones;

/// <summary>
/// v1.2.3「插件市场 UX 收尾」中由详情/设置批次负责的自测用例：
/// <list type="number">
/// <item>详情弹窗的更新日志区块（当前语言 / 回退 / 无内容不占位 / 有更新时标题点明版本）；</item>
/// <item>安装确认的多类型「包含内容」清单（单类型文案保持不变）；</item>
/// <item>拖拽导入的纯函数真值表 + 复用 ImportPackageAsync 的真实安装路径；</item>
/// <item>插件设置入口端到端：已启用 + 有 schema → 详情按钮 / 已安装行齿轮 / 设置窗口定位插件 /
///       「恢复默认」写回默认值并触发 SettingsChanged；</item>
/// <item>GitHub 直装插件的更新检查纯函数（版本比较 / 仓库解析，不触网）与入口可见性；</item>
/// <item>外观包详情的材质 / 预设名称列表。</item>
/// </list>
/// 与 <c>App.PluginLab.cs</c> 同属一个 partial 类：主文件只加一行用例名与一行 switch 分派。
/// </summary>
public partial class App
{
    const string DetailUxLabPluginId = "lab.detailux";
    const string DetailUxToolDemoId = "three-freezen.tool-demo";

    async Task RunDetailUxLabCaseAsync(LabCase item, string dir)
    {
        var service = PluginService.Instance;
        var loc = LocalizationService.Instance;
        var settingsService = PluginSettingsService.Instance;
        var originalLanguage = loc.CurrentLanguage;
        var dialogs = new List<Window>();
        var probeUpper = Path.Combine(dir, "detailux-drop-probe.DOPKG");
        var probeDir = Path.Combine(dir, "detailux-fake.dopkg");
        var probeText = Path.Combine(dir, "detailux-note.txt");
        var appearanceInstalledHere = false;
        try
        {
            loc.SetLanguage("zh");

            // ── 1/7. 详情弹窗「更新日志」 ──
            var changelogEntry = BuildDetailUxEntry("1.1.0", new LocalizedText
            {
                Zh = "修复设置保存后界面不刷新的问题。",
                En = "Fixes the UI not refreshing after settings are saved."
            });
            var oldRecord = new PluginRecord { Id = DetailUxLabPluginId, Version = "1.0.0", Kind = "widget" };
            var changelogDetail = new PluginDetailDialog(changelogEntry, oldRecord);
            dialogs.Add(changelogDetail);
            item.Check(changelogDetail.ChangelogVisibleForLab,
                "detail: an entry with a changelog shows the changelog block");
            item.Check(changelogDetail.ChangelogTextForLab == changelogEntry.Changelog!.Zh,
                "detail: the changelog uses the current language");
            item.Check(changelogDetail.ChangelogTitleForLab
                       == loc.Get("Plugin.Detail.Changelog.UpdateTitle", changelogEntry.Version),
                "detail: with a newer version the changelog title names the new version");

            var noChangelogEntry = BuildDetailUxEntry("1.0.0", null);
            var noChangelogDetail = new PluginDetailDialog(noChangelogEntry, null);
            dialogs.Add(noChangelogDetail);
            item.Check(!noChangelogDetail.ChangelogVisibleForLab,
                "detail: without a changelog the block is collapsed (no empty placeholder)");

            loc.SetLanguage("en");
            var englishDetail = new PluginDetailDialog(changelogEntry, oldRecord);
            dialogs.Add(englishDetail);
            item.Check(englishDetail.ChangelogTextForLab == changelogEntry.Changelog!.En,
                "detail: the changelog follows a language switch");

            var zhOnlyEntry = BuildDetailUxEntry("1.0.0", new LocalizedText { Zh = "只有中文的更新日志" });
            var fallbackDetail = new PluginDetailDialog(zhOnlyEntry, null);
            dialogs.Add(fallbackDetail);
            item.Check(fallbackDetail.ChangelogTextForLab == "只有中文的更新日志",
                "detail: a missing language falls back to the other language");
            loc.SetLanguage("zh");

            var sameVersionDetail = new PluginDetailDialog(changelogEntry, new PluginRecord
            {
                Id = DetailUxLabPluginId, Version = "1.1.0", Kind = "widget"
            });
            dialogs.Add(sameVersionDetail);
            item.Check(sameVersionDetail.ChangelogTitleForLab == loc["Plugin.Detail.Changelog"],
                "detail: without an update the changelog keeps the plain title");

            // ── 2/7. 多类型安装确认的「包含内容」 ──
            var multiPromptEntry = BuildDetailUxEntry("1.0.0", null);
            multiPromptEntry.Kinds = new List<string> { "widget", "tool" };
            multiPromptEntry.ApiVersion = 4;
            var expectedMulti = loc.Get("Plugin.Contents.CapabilityList",
                loc["Plugin.Type.Widget"] + " · " + loc["Plugin.Type.Tool"]);
            item.Check(PluginsPage.DescribePromptContents(multiPromptEntry) == expectedMulti,
                "prompt: a two-kind package lists every capability");
            var promptPage = new PluginsPage();
            item.Check(promptPage.BuildPrompt(multiPromptEntry).Contents == expectedMulti,
                "prompt: BuildPrompt uses the multi-kind capability list");
            item.Check(promptPage.BuildPrompt(multiPromptEntry).Declarative == false,
                "prompt: a multi-kind code package is still treated as code");

            var singleWidgetEntry = BuildDetailUxEntry("1.0.0", null);
            item.Check(PluginsPage.DescribePromptContents(singleWidgetEntry) == "",
                "prompt: a single widget package keeps the old empty contents");

            var languageEntry = BuildDetailUxEntry("1.0.0", null);
            languageEntry.Kind = "language";
            languageEntry.ApiVersion = 0;
            languageEntry.Language = new PluginLanguageDeclaration
            {
                Code = "ja", NativeName = "日本語", Base = "en", Coverage = 0.42
            };
            item.Check(PluginsPage.DescribePromptContents(languageEntry)
                       == loc.Get("Plugin.Contents.Language", "日本語", 42, "en"),
                "prompt: a language pack keeps its original contents line");

            var appearancePromptEntry = BuildDetailUxEntry("1.0.0", null);
            appearancePromptEntry.Kind = "appearance";
            appearancePromptEntry.ApiVersion = 0;
            item.Check(PluginsPage.DescribePromptContents(appearancePromptEntry)
                       == loc["Plugin.Appearance.Hint"],
                "prompt: an appearance pack keeps its original contents line");

            var threeKindEntry = BuildDetailUxEntry("1.0.0", null);
            threeKindEntry.Kinds = new List<string> { "widget", "tool", "integration" };
            threeKindEntry.ApiVersion = 4;
            var threeKindContents = PluginsPage.DescribePromptContents(threeKindEntry);
            item.Check(threeKindContents.Contains(loc["Plugin.Type.Widget"])
                       && threeKindContents.Contains(loc["Plugin.Type.Tool"])
                       && threeKindContents.Contains(loc["Plugin.Type.Integration"]),
                "prompt: a three-kind package lists all three capabilities");

            // ── 3/7. 拖拽导入纯函数真值表 ──
            var helloPackage = Path.Combine(dir, "hello-widget-1.0.0.dopkg");
            item.Check(PluginsPage.IsDroppablePackage(helloPackage),
                "drop: a real .dopkg in the lab dir is accepted");
            File.Copy(helloPackage, probeUpper, overwrite: true);
            item.Check(PluginsPage.IsDroppablePackage(probeUpper),
                "drop: the .dopkg extension check is case-insensitive");
            item.Check(!PluginsPage.IsDroppablePackage(Path.Combine(dir, "no-such-file.dopkg")),
                "drop: a missing package is rejected");
            Directory.CreateDirectory(probeDir);
            item.Check(!PluginsPage.IsDroppablePackage(probeDir),
                "drop: a directory named *.dopkg is rejected");
            File.WriteAllText(probeText, "not a package");
            item.Check(!PluginsPage.IsDroppablePackage(probeText),
                "drop: a non-package file is rejected");
            item.Check(!PluginsPage.IsDroppablePackage(null)
                       && !PluginsPage.IsDroppablePackage("")
                       && !PluginsPage.IsDroppablePackage("   "),
                "drop: null and blank paths are rejected");

            // ── 4/7. 导入：复用重构后的 ImportPackageAsync（跳过模态确认，其余路径不变）──
            var importPage = new PluginsPage();
            // tool 用例会留下一个已安装（已禁用）的 tool-demo：先清掉，才能断言导入新增了记录。
            if (service.GetRecord(DetailUxToolDemoId) != null)
            {
                service.Uninstall(DetailUxToolDemoId, deleteData: true, out _);
                service.RefreshRecords();
            }
            item.Check(service.GetRecord(DetailUxToolDemoId) == null,
                "import: the tool-demo record is absent before the import");
            var toolPackage = Path.Combine(dir, "tool-demo-1.0.0.dopkg");
            item.Check(PluginsPage.IsDroppablePackage(toolPackage),
                "import: the tool-demo package passes the drop filter");
            item.Check(await importPage.ImportPackageAsync(toolPackage, interactive: false),
                "import: ImportPackageAsync installs the dropped package");
            var importedRecord = service.GetRecord(DetailUxToolDemoId);
            item.Check(importedRecord != null
                       && string.Equals(importedRecord.Source, "local", StringComparison.OrdinalIgnoreCase)
                       && importedRecord.Version == "1.0.0",
                "import: the installed record carries the local source and the package version");
            item.Check(Directory.Exists(PluginPaths.VersionDir(DetailUxToolDemoId, "1.0.0")),
                "import: the package files land in the version directory");
            item.Check(!await importPage.ImportPackageAsync(
                    Path.Combine(dir, "bad-ref-1.0.0.dopkg"), interactive: false),
                "import: an invalid package is refused");
            item.Check(!await importPage.ImportPackageAsync(
                    Path.Combine(dir, "missing-package.dopkg"), interactive: false),
                "import: a missing path is refused");
            service.Uninstall(DetailUxToolDemoId, deleteData: true, out _);
            service.RefreshRecords();
            item.Check(service.GetRecord(DetailUxToolDemoId) == null,
                "import: cleanup removes the imported package again");

            // ── 5/7. 插件设置入口 + 「恢复默认」端到端 ──
            var demoPackage = Path.Combine(dir, "tool-settings-demo-1.0.0.dopkg");
            item.Check(File.Exists(demoPackage), "settings: the tool-settings-demo package is staged");
            var demoInstall = await service.Installer!.InstallFromFileAsync(demoPackage);
            item.Check(demoInstall.Success,
                "settings: installing the tool-settings-demo package succeeds"
                + (demoInstall.Error == null ? "" : " (" + demoInstall.Error + ")"));
            if (!demoInstall.Success) return;
            service.RefreshRecords();
            service.SetEnabled(ToolSettingsDemoId, true);
            service.ReloadForLab();
            var demoRecord = service.GetRecord(ToolSettingsDemoId);
            item.Check(demoRecord != null && demoRecord.Enabled,
                "settings: the tool-settings demo is installed and enabled");
            var demoEntry = settingsService.Find(ToolSettingsDemoId);
            item.Check(demoEntry != null && demoEntry.FieldCount >= 1,
                "settings: the real plugin DLL publishes a settings schema");

            var settingsCatalogEntry = BuildDetailUxEntry("1.1.0", new LocalizedText
            {
                Zh = "新的设置项与更稳的保存流程。",
                En = "New settings and a more reliable save flow."
            });
            settingsCatalogEntry.Id = ToolSettingsDemoId;
            settingsCatalogEntry.Kind = "tool";
            settingsCatalogEntry.Name = new LocalizedText
            {
                Zh = "时间戳演示", En = "Timestamp demo"
            };
            settingsCatalogEntry.Summary = new LocalizedText
            {
                Zh = "把时间戳插到剪贴板，并演示插件设置页。",
                En = "Inserts a timestamp into the clipboard and demos the plugin settings page."
            };
            settingsCatalogEntry.Author = new PluginAuthor { Name = "Three-Freezen", Github = "Three-Freezen" };
            settingsCatalogEntry.Repo = "Three-Freezen/DeskOrder-Plugin-Lab";
            settingsCatalogEntry.ApiVersion = 5;
            settingsCatalogEntry.MinAppVersion = "1.2.0";

            var settingsDetail = new PluginDetailDialog(settingsCatalogEntry, demoRecord);
            dialogs.Add(settingsDetail);
            item.Check(settingsDetail.SettingsVisibleForLab,
                "detail: an enabled plugin with a schema shows the settings button");
            settingsDetail.Show();
            await Task.Delay(500);
            var detailShot = Path.Combine(dir, "shot-ux-detail-changelog.png");
            SaveScreenshot(settingsDetail, detailShot);
            item.Check(File.Exists(detailShot),
                "screenshot written: shot-ux-detail-changelog.png");
            settingsDetail.Close();

            service.SetEnabled(ToolSettingsDemoId, false);
            service.ReloadForLab();
            var disabledDetail = new PluginDetailDialog(settingsCatalogEntry,
                service.GetRecord(ToolSettingsDemoId));
            dialogs.Add(disabledDetail);
            item.Check(!disabledDetail.SettingsVisibleForLab,
                "detail: a disabled plugin hides the settings button");
            item.Check(settingsService.Find(ToolSettingsDemoId) == null,
                "settings: disabling the plugin drops its schema");
            service.SetEnabled(ToolSettingsDemoId, true);
            service.ReloadForLab();

            var settingsDialog = new PluginSettingsDialog(ToolSettingsDemoId);
            dialogs.Add(settingsDialog);
            item.Check(settingsDialog.FocusedPluginIdForLab == ToolSettingsDemoId,
                "settings: opening with a plugin id remembers the target");
            item.Check(settingsDialog.FocusedSectionVisibleForLab,
                "settings: the target plugin section is highlighted");
            item.Check(settingsDialog.FocusedSectionHeaderForLab.Contains("时间戳设置"),
                "settings: the highlighted section is the target plugin's schema");
            item.Check(settingsDialog.ResetButtonCountForLab >= 1,
                "settings: every plugin section gets a reset-to-defaults button");
            settingsDialog.Show();
            await Task.Delay(500);
            var resetShot = Path.Combine(dir, "shot-ux-settings-reset.png");
            SaveScreenshot(settingsDialog, resetShot);
            item.Check(File.Exists(resetShot),
                "screenshot written: shot-ux-settings-reset.png");

            var liveEntry = settingsService.Find(ToolSettingsDemoId)!;
            var defaults = settingsService.ReadValues(liveEntry);
            var changed = new Dictionary<string, string>(StringComparer.Ordinal);
            foreach (var field in liveEntry.Schema.Fields)
                changed[field.Key] = NonDefaultValue(field, defaults[field.Key]);
            item.Check(settingsService.Save(liveEntry, changed, out var saveError),
                "reset: saving non-default values succeeds"
                + (saveError == null ? "" : " (" + saveError + ")"));
            var storagePath = PluginStorage.FilePath(ToolSettingsDemoId);
            var changedStorage = new JsonPluginStorage(storagePath, null, null);
            item.Check(liveEntry.Schema.Fields.Any(field =>
                    changedStorage.Get(PluginSettingsService.StorageKeyPrefix + field.Key) == changed[field.Key]),
                "reset: the non-default values are persisted under settings.*");

            var resetNotifications = new List<string>();
            Action<string> onSettingsChanged = id => resetNotifications.Add(id);
            settingsService.SettingsChanged += onSettingsChanged;
            try
            {
                var resetButton = settingsDialog.ResetButtonForLab(ToolSettingsDemoId);
                item.Check(resetButton != null, "reset: the reset button is wired to the dialog");
                resetButton?.RaiseEvent(new RoutedEventArgs(ButtonBase.ClickEvent));
            }
            finally
            {
                settingsService.SettingsChanged -= onSettingsChanged;
            }

            var resetStorage = new JsonPluginStorage(storagePath, null, null);
            item.Check(liveEntry.Schema.Fields.All(field =>
                    resetStorage.Get(PluginSettingsService.StorageKeyPrefix + field.Key)
                    == defaults[field.Key]),
                "reset: every field is written back to its schema default");
            item.Check(settingsDialog.StatusVisibilityForLab == Visibility.Visible
                       && settingsDialog.StatusTextForLab
                       == loc.Get("Plugin.Settings.ResetDone", liveEntry.DisplayPluginName),
                "reset: the status bar reports the reset");
            item.Check(resetNotifications.Count == 1 && resetNotifications[0] == ToolSettingsDemoId,
                "reset: the save pipeline raises SettingsChanged once for the plugin");
            settingsDialog.Close();

            // ── 6/7. 已安装行的齿轮按钮（真实页面视觉树）──
            // 页面 Loaded 会拉 registry：先把夹具缓存写进本地，避免用例被一次网络请求拖慢。
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
            item.Check(window != null, "row: the management window exists");
            if (window != null)
            {
                window.Show();
                window.ShowSection("plugins");
                window.Width = 1680;
                window.Height = 1120;
                window.Left = 40;
                window.Top = 20;
                await Task.Delay(900);
                if (window.MainContent.Content is PluginsPage installedPage)
                {
                    installedPage.SelectTab(1);
                    await Task.Delay(700);
                    var gearButtons = WalkVisualTree(installedPage.InstalledHost).OfType<Button>()
                        .Where(button => Equals(button.ToolTip, loc["Plugin.Settings.Title"]))
                        .ToList();
                    item.Check(gearButtons.Count == 1,
                        "row: the installed row of a plugin with a schema shows the gear button");
                    item.Check(gearButtons.Count == 1
                               && gearButtons[0].Width >= 28 && gearButtons[0].Width <= 32,
                        "row: the gear is a compact icon button");
                    var pageShot = Path.Combine(dir, "shot-ux-installed-gear.png");
                    SaveScreenshot(window, pageShot);
                    item.Check(File.Exists(pageShot), "screenshot written: shot-ux-installed-gear.png");
                }
                else
                {
                    item.Check(false, "row: the plugins page is reachable from the management window");
                }
            }

            service.Uninstall(ToolSettingsDemoId, deleteData: true, out _);
            service.RefreshRecords();
            item.Check(service.GetRecord(ToolSettingsDemoId) == null,
                "settings: cleanup removes the tool-settings demo again");

            // ── 7/8. GitHub 直装插件的更新检查（纯函数，不触网）──
            item.Check(PluginDetailDialog.TryParseGitHubRepo(
                    "Three-Freezen/DeskOrder-Plugins", out var ghOwner, out var ghRepo)
                       && ghOwner == "Three-Freezen" && ghRepo == "DeskOrder-Plugins",
                "github: an owner/repo record parses into owner and repository");
            item.Check(PluginDetailDialog.TryParseGitHubRepo(
                    "https://github.com/Three-Freezen/DeskOrder-Plugins", out _, out _),
                "github: a repository URL parses as well");
            item.Check(!PluginDetailDialog.TryParseGitHubRepo("not-a-repo", out _, out _)
                       && !PluginDetailDialog.TryParseGitHubRepo("", out _, out _),
                "github: malformed repositories are rejected");

            item.Check(PluginDetailDialog.NormalizeReleaseVersion("release/v1.2.3") == "v1.2.3",
                "github: a path-like release tag normalizes to its version");
            item.Check(PluginDetailDialog.NormalizeReleaseVersion("DeskOrder-Plugin-2.1.0") == "2.1.0",
                "github: a prefixed release tag normalizes to its version");
            item.Check(PluginDetailDialog.IsGitHubUpdateAvailable("1.0.0", "v1.0.1"),
                "github: a newer release tag is an available update");
            item.Check(!PluginDetailDialog.IsGitHubUpdateAvailable("1.2.0", "v1.1.9"),
                "github: an older release tag is not an update");
            item.Check(!PluginDetailDialog.IsGitHubUpdateAvailable("1.2.0", "v1.2.0"),
                "github: the same version is not an update");
            item.Check(PluginDetailDialog.IsGitHubUpdateAvailable("1.2.0-beta", "v1.2.0"),
                "github: a final release supersedes a prerelease");
            item.Check(!PluginDetailDialog.IsGitHubUpdateAvailable("1.2.0", "v1.2.0-rc.1"),
                "github: a prerelease does not supersede a final release");
            item.Check(!PluginDetailDialog.IsGitHubUpdateAvailable("1.2.0", "latest"),
                "github: a rolling tag is not mistaken for a version");

            var githubRecord = new PluginRecord
            {
                Id = "lab.detailux-github",
                Version = "1.0.0",
                Kind = "tool",
                Source = "github",
                Repo = "Three-Freezen/DeskOrder-Plugins",
                Enabled = true
            };
            var githubEntry = BuildDetailUxEntry("1.0.0", null);
            githubEntry.Id = githubRecord.Id;
            githubEntry.Kind = "tool";
            githubEntry.Repo = githubRecord.Repo;
            var githubDetail = new PluginDetailDialog(githubEntry, githubRecord);
            dialogs.Add(githubDetail);
            item.Check(githubDetail.CheckUpdateVisibleForLab,
                "github: an installed GitHub record shows the update-check button");
            var localDetail = new PluginDetailDialog(BuildDetailUxEntry("1.0.0", null),
                new PluginRecord { Id = DetailUxLabPluginId, Version = "1.0.0", Kind = "widget", Source = "local" });
            dialogs.Add(localDetail);
            item.Check(!localDetail.CheckUpdateVisibleForLab,
                "github: a locally installed record hides the update-check button");

            // ── 8/8. 外观包详情：列出材质 / 预设名称 ──
            appearanceInstalledHere = service.GetRecord(AppearancePackId) == null;
            await EnsureAppearancePackAsync(dir);
            var appearanceRecord = service.GetRecord(AppearancePackId);
            item.Check(appearanceRecord != null, "appearance: the aurora appearance pack is installed");
            if (appearanceRecord != null)
            {
                var appearanceEntry = BuildDetailUxEntry(appearanceRecord.Version, null);
                appearanceEntry.Id = AppearancePackId;
                appearanceEntry.Kind = "appearance";
                appearanceEntry.ApiVersion = 0;
                appearanceEntry.Name = new LocalizedText { Zh = "极光外观", En = "Aurora appearance" };
                var appearanceDetail = new PluginDetailDialog(appearanceEntry, appearanceRecord);
                dialogs.Add(appearanceDetail);
                item.Check(appearanceDetail.AppearanceNamesVisibleForLab,
                    "appearance: an installed appearance pack lists its contents by name");

                var packDir = PluginPaths.VersionDir(AppearancePackId, appearanceRecord.Version);
                var packManifest = PluginPackages.ReadManifest(packDir, out _);
                var packValidation = packManifest == null
                    ? null
                    : AppearancePackService.Instance.Validate(packDir, packManifest);
                var expectedMaterial = packValidation?.Materials.FirstOrDefault()?.ZhName;
                var expectedPreset = packValidation?.Presets
                    .Select(preset =>
                    {
                        try { return PresetService.For(preset.Kind).ValidateFile(preset.Path, out _)?.Name; }
                        catch { return null; }
                    })
                    .FirstOrDefault(name => !string.IsNullOrWhiteSpace(name));
                item.Check(expectedMaterial != null
                           && appearanceDetail.AppearanceNamesForLab.Contains(expectedMaterial),
                    "appearance: the material names are listed in the detail dialog");
                item.Check(expectedPreset != null
                           && appearanceDetail.AppearanceNamesForLab.Contains(expectedPreset),
                    "appearance: the preset names are listed in the detail dialog");
            }
        }
        catch (Exception ex)
        {
            item.Check(false, "detail-ux case crashed: " + ex);
        }
        finally
        {
            foreach (var dialog in dialogs)
            {
                try { dialog.Close(); } catch { }
            }
            try
            {
                if (service.GetRecord(ToolSettingsDemoId) != null)
                    service.Uninstall(ToolSettingsDemoId, deleteData: true, out _);
            }
            catch { }
            try
            {
                if (service.GetRecord(DetailUxToolDemoId) != null)
                    service.Uninstall(DetailUxToolDemoId, deleteData: true, out _);
            }
            catch { }
            try
            {
                if (appearanceInstalledHere && service.GetRecord(AppearancePackId) != null)
                    service.Uninstall(AppearancePackId, deleteData: true, out _);
            }
            catch { }
            try { service.RefreshRecords(); } catch { }
            try { File.Delete(probeUpper); } catch { }
            try { File.Delete(probeText); } catch { }
            try { if (Directory.Exists(probeDir)) Directory.Delete(probeDir, recursive: true); } catch { }
            loc.SetLanguage(originalLanguage);
        }
    }

    /// <summary>详情用例的合成条目骨架：名称 / 摘要 / 作者 / 仓库都合法，只按需覆盖 changelog 等字段。</summary>
    static PluginCatalogEntry BuildDetailUxEntry(string version, LocalizedText? changelog) => new()
    {
        Id = DetailUxLabPluginId,
        Kind = "widget",
        Name = new LocalizedText { Zh = "详情自测", En = "Detail lab" },
        Summary = new LocalizedText { Zh = "详情弹窗自测用的合成条目。", En = "A synthetic entry for the detail dialog case." },
        Author = new PluginAuthor { Name = "DeskOrder Lab", Github = "lab" },
        Repo = "lab/DeskOrder-DetailUx",
        Version = version,
        ApiVersion = 1,
        MinAppVersion = "1.0.0",
        License = "Apache-2.0",
        Changelog = changelog
    };

    /// <summary>给「恢复默认」用例造一个合法的非默认值：Text 加后缀、Toggle 取反、Int 加一、Choice 换一项。</summary>
    static string NonDefaultValue(PluginSettingField field, string current)
    {
        switch (field.Kind)
        {
            case PluginSettingKind.Toggle:
                return string.Equals(current, "true", StringComparison.OrdinalIgnoreCase) ? "false" : "true";
            case PluginSettingKind.Int:
                var number = int.TryParse(current, out var parsed) ? parsed : 0;
                var incremented = number + 1;
                if (field.Max.HasValue && incremented > field.Max.Value) incremented = number - 1;
                if (field.Min.HasValue && incremented < field.Min.Value) incremented = field.Min.Value;
                return incremented.ToString();
            case PluginSettingKind.Choice:
                var other = field.Choices?.FirstOrDefault(choice =>
                    !string.Equals(choice.Value, current, StringComparison.Ordinal));
                return other?.Value ?? current;
            default:
                return current + "!lab";
        }
    }
}
#endif
