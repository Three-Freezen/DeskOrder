#if DEBUG
using System;
using System.Collections.Generic;
using System.IO;
using System.IO.Compression;
using System.Linq;
using System.Text.Json;
using System.Text.Json.Nodes;
using System.Threading.Tasks;
using System.Windows;
using DeskOrder.PluginApi;
using DesktopZones.Helpers;
using DesktopZones.Models;
using DesktopZones.Services;
using DesktopZones.Views;
using DesktopZones.Views.Pages;

namespace DesktopZones;

/// <summary>
/// 自测台「plugin-settings」用例（API v5）：PluginLocalizedText 解析、schema 校验真值表、
/// 插件设置读写 + Changed 回调、真实示例包端到端（本地化命令名 / 设置生效 / 禁用后撤下），
/// 以及「插件设置…」窗口与插件工具页的截图。单独成文件，避免主自测台文件继续膨胀。
/// </summary>
public partial class App
{
    const string ToolSettingsDemoId = "three-freezen.tool-settings-demo";

    async Task RunPluginSettingsLabCaseAsync(LabCase item, string name, string dir)
    {
        _ = name;
        var loc = LocalizationService.Instance;
        var settingsService = PluginSettingsService.Instance;
        var tools = PluginToolService.Instance;
        var service = PluginService.Instance;
        var originalLanguage = loc.CurrentLanguage;

        // 用例要自己装配 schema / 命令，开头先清干净（隔离 data-root，不影响真实配置）。
        settingsService.Clear();
        tools.Clear();
        loc.SetLanguage("zh");
        try
        {
            // ── 1) PluginLocalizedText.Get 解析规则 ──
            var both = PluginLocalizedText.From("中文", "English");
            item.Check(both.HasAny, "a localized text with zh + en has text");
            item.Check(both.Get("zh") == "中文", "zh resolves to the Chinese text");
            item.Check(both.Get("zh-CN") == "中文", "a zh-* language still resolves to the Chinese text");
            item.Check(both.Get("en") == "English", "en resolves to the English text");
            item.Check(both.Get("ja") == "English", "an unknown language (ja) falls back to English");
            item.Check(both.Get("ar") == "English", "an unknown language (ar) falls back to English");
            var enOnly = PluginLocalizedText.From(null, "Only EN");
            item.Check(enOnly.Get("zh") == "Only EN", "zh falls back to en when there is no Chinese text");
            var zhOnly = PluginLocalizedText.From("只有中文", null);
            item.Check(zhOnly.Get("en") == "只有中文", "en falls back to zh when there is no English text");
            item.Check(zhOnly.Get("ja") == "只有中文", "an unknown language falls back to zh when en is missing");
            item.Check(!PluginLocalizedText.From("", "   ").HasAny, "whitespace-only text does not count as localized");
            item.Check(PluginLocalizedText.From(null, null).Get("en") == "",
                "a localized text without any text resolves to an empty string");

            // ── 2) schema 校验真值表（注册期拒绝非法定义）──
            bool Throws(Action action)
            {
                try { action(); return false; }
                catch { return true; }
            }

            PluginLocalizedText Label(string zh = "标签", string en = "Label")
                => PluginLocalizedText.From(zh, en);

            PluginSettingField Field(string key, PluginSettingKind kind = PluginSettingKind.Text,
                string defaultValue = "", int? min = null, int? max = null,
                List<PluginSettingChoice>? choices = null) => new()
            {
                Key = key,
                Kind = kind,
                Label = Label(),
                DefaultValue = defaultValue,
                Min = min,
                Max = max,
                Choices = choices
            };

            PluginSettingsSchema SchemaWith(params PluginSettingField[] fields)
            {
                var schema = new PluginSettingsSchema { Title = Label("标题", "Title") };
                foreach (var field in fields) schema.Fields.Add(field);
                return schema;
            }

            var validRegistry = new PluginSettingsRegistry();
            validRegistry.Register(SchemaWith(
                Field("suffix"),
                Field("loud", PluginSettingKind.Toggle, "false"),
                Field("count", PluginSettingKind.Int, "1", min: 0, max: 10),
                Field("format", PluginSettingKind.Choice, "iso", choices: new List<PluginSettingChoice>
                {
                    new() { Value = "iso", Label = Label("完整", "Full") },
                    new() { Value = "short", Label = Label("简短", "Short") }
                })));
            item.Check(validRegistry.Schema != null && validRegistry.Schema.Fields.Count == 4,
                "a valid schema with all four field kinds is accepted");
            item.Check(Throws(() => validRegistry.Register(SchemaWith(Field("second")))),
                "a plugin can register at most one settings schema");

            var registry = new PluginSettingsRegistry();
            item.Check(Throws(() => registry.Register(new PluginSettingsSchema
                    { Title = PluginLocalizedText.From(null, null) })),
                "a schema without a title is rejected");
            item.Check(Throws(() => registry.Register(SchemaWith(Field("dup"), Field("dup")))),
                "duplicate field keys are rejected");
            item.Check(Throws(() => registry.Register(SchemaWith(Field("BAD KEY")))),
                "an invalid field key is rejected");
            item.Check(Throws(() => registry.Register(SchemaWith(
                    Field("choice", PluginSettingKind.Choice)))),
                "a Choice field without choices is rejected");
            item.Check(Throws(() => registry.Register(SchemaWith(
                    Field("count", PluginSettingKind.Int, min: 10, max: 1)))),
                "an Int field with Min greater than Max is rejected");
            item.Check(Throws(() => registry.Register(SchemaWith(
                    Field("loud", PluginSettingKind.Toggle, "yes")))),
                "a Toggle field whose default is not true/false is rejected");
            item.Check(Throws(() => registry.Register(new PluginSettingsSchema
                {
                    Title = Label(),
                    Fields =
                    {
                        new PluginSettingField
                        {
                            Key = "unlabeled",
                            Kind = PluginSettingKind.Text,
                            Label = PluginLocalizedText.From("", "")
                        }
                    }
                })),
                "a field without a label is rejected");
            item.Check(Throws(() =>
                {
                    var tooMany = new PluginSettingsSchema { Title = Label() };
                    for (int i = 0; i < PluginSettingsRegistry.MaxFields + 1; i++)
                        tooMany.Fields.Add(Field("f" + i));
                    new PluginSettingsRegistry().Register(tooMany);
                }),
                "more than 20 fields are rejected");
            item.Check(Throws(() => registry.Register(SchemaWith(
                    Field("format", PluginSettingKind.Choice, choices: new List<PluginSettingChoice>
                    {
                        new() { Value = "x", Label = Label() },
                        new() { Value = "x", Label = Label() }
                    })))),
                "duplicate choice values are rejected");
            item.Check(Throws(() => registry.Register(SchemaWith(
                    Field("format", PluginSettingKind.Choice, "missing", choices: new List<PluginSettingChoice>
                    {
                        new() { Value = "x", Label = Label() }
                    })))),
                "a Choice default that is not one of its choices is rejected");

            // ── 3) 发布 / 读默认值 / 保存 / Changed 回调 ──
            var record = new PluginRecord
            {
                Id = "lab.settings",
                Version = "1.0.0",
                Kind = "tool",
                Enabled = true,
                Source = "lab",
                Signature = "unsigned"
            };
            var manifest = new PluginManifest
            {
                SchemaVersion = 1,
                Id = "lab.settings",
                Name = new LocalizedText { Zh = "自测设置", En = "Lab settings" },
                Summary = new LocalizedText { Zh = "自测设置", En = "lab settings" },
                Version = "1.0.0",
                Author = new PluginAuthor { Name = "Three-Freezen", Github = "Three-Freezen" },
                Repo = "Three-Freezen/DeskOrder-Plugin-Lab",
                License = "Apache-2.0",
                Kind = "tool",
                ApiVersion = 5,
                MinAppVersion = "1.2.2",
                Icon = "icon.png"
            };
            var received = new List<(string Key, string Value)>();
            var schema = new PluginSettingsSchema
            {
                Title = PluginLocalizedText.From("设置", "Settings"),
                Changed = (key, value) => received.Add((key, value))
            };
            schema.Fields.Add(Field("suffix", defaultValue: " - demo"));
            schema.Fields.Add(Field("loud", PluginSettingKind.Toggle, "false"));
            schema.Fields.Add(Field("count", PluginSettingKind.Int, "1", min: 0, max: 10));
            schema.Fields.Add(Field("format", PluginSettingKind.Choice, "iso", choices: new List<PluginSettingChoice>
            {
                new() { Value = "iso", Label = Label("完整", "Full") },
                new() { Value = "short", Label = Label("简短", "Short") }
            }));

            settingsService.Publish(record, manifest, schema);
            item.Check(settingsService.Schemas.Count == 1 && settingsService.HasAny,
                "publishing one schema lists exactly one plugin settings entry");
            var entry = settingsService.Find("lab.settings");
            item.Check(entry != null && entry.FieldCount == 4,
                "the published schema is found by plugin id and carries four fields");
            item.Check(entry?.DisplayTitle == "设置", "the schema title resolves in Chinese");
            item.Check(entry?.DisplayPluginName == "自测设置", "the plugin name resolves in Chinese");

            var defaults = entry == null ? new Dictionary<string, string>() : settingsService.ReadValues(entry);
            item.Check(defaults.TryGetValue("suffix", out var suffixDefault) && suffixDefault == " - demo"
                       && defaults["loud"] == "false" && defaults["count"] == "1"
                       && defaults["format"] == "iso",
                "unsaved fields read back their schema defaults");

            var settingsChangedCount = 0;
            Action<string> onSettingsChanged = _ => settingsChangedCount++;
            settingsService.SettingsChanged += onSettingsChanged;
            var toSave = new Dictionary<string, string>
            {
                ["suffix"] = "!lab",
                ["loud"] = "true",
                ["count"] = "7",
                ["format"] = "long"   // 不在候选里 —— 故意先试非法，见下一条
            };
            var rejectedChoice = !settingsService.Save(entry!, toSave, out var choiceError);
            item.Check(rejectedChoice && !string.IsNullOrEmpty(choiceError),
                "an invalid choice value is rejected before anything is written");
            item.Check(settingsService.ReadValues(entry!)["suffix"] == " - demo",
                "a rejected save leaves the stored values untouched");

            toSave["format"] = "short";
            item.Check(settingsService.Save(entry!, toSave, out var saveError),
                "saving valid values succeeds" + (saveError == null ? "" : " (" + saveError + ")"));
            item.Check(received.Count == 4
                       && received.Any(r => r.Key == "suffix" && r.Value == "!lab")
                       && received.Any(r => r.Key == "loud" && r.Value == "true")
                       && received.Any(r => r.Key == "count" && r.Value == "7")
                       && received.Any(r => r.Key == "format" && r.Value == "short"),
                "Changed receives the correct key/value for every changed field");
            item.Check(settingsChangedCount == 1, "SettingsChanged fires once per successful save");

            item.Check(settingsService.Save(entry!, toSave, out _), "saving the same values succeeds again");
            item.Check(received.Count == 4 && settingsChangedCount == 1,
                "saving unchanged values does not fire Changed / SettingsChanged again");

            var persisted = new JsonPluginStorage(PluginStorage.FilePath("lab.settings"), null, null);
            item.Check(persisted.Get("settings.suffix") == "!lab" && persisted.Get("settings.count") == "7"
                       && persisted.Get("settings.loud") == "true" && persisted.Get("settings.format") == "short",
                "values are persisted under settings.<fieldKey> in the plugin data file");
            item.Check(entry!.Storage.Get("settings.format") == "short",
                "the storage instance handed to the plugin sees the new value (no stale cache)");

            var outOfRange = new Dictionary<string, string>(toSave) { ["count"] = "99" };
            item.Check(!settingsService.Save(entry!, outOfRange, out var rangeError)
                       && !string.IsNullOrEmpty(rangeError),
                "an out-of-range Int value is rejected with a localized error");
            item.Check(new JsonPluginStorage(PluginStorage.FilePath("lab.settings"), null, null)
                    .Get("settings.count") == "7",
                "a rejected Int save does not overwrite the persisted value");

            settingsService.SettingsChanged -= onSettingsChanged;
            settingsService.RemovePlugin("lab.settings");
            item.Check(!settingsService.HasAny && settingsService.Find("lab.settings") == null,
                "removing the plugin drops its settings schema from the service");

            // ── 4) 端到端：真实 tool-settings-demo 示例包 ──
            // 包由 plugin-community\tools\build-labs.ps1 生成；自测台启动时由 runner 复制进来。
            var demoPackage = Path.Combine(dir, "tool-settings-demo-1.0.0.dopkg");
            item.Check(File.Exists(demoPackage), "the tool-settings-demo package is present in the lab dir");
            if (!File.Exists(demoPackage))
            {
                item.Check(false, "run plugin-community\\tools\\build-labs.ps1 to produce tool-settings-demo-1.0.0.dopkg");
                return;
            }

            // 示例声明 minAppVersion 1.2.2（v5 随 1.2.2 发布）；当前宿主 worktree 还是 1.2.1，
            // 直接装会被清单校验拦下 —— 先验清单声明，再改成「当前宿主版本」的副本装进来跑真机路径。
            var rewriteDir = Path.Combine(dir, "tool-settings-demo-rewrite");
            if (Directory.Exists(rewriteDir)) Directory.Delete(rewriteDir, recursive: true);
            ZipFile.ExtractToDirectory(demoPackage, rewriteDir);
            var demoManifest = PluginPackages.ReadManifest(rewriteDir, out var demoManifestError);
            item.Check(demoManifest != null && demoManifest.ApiVersion == 5,
                "the sample declares apiVersion 5" + (demoManifestError == null ? "" : " (" + demoManifestError + ")"));
            item.Check(demoManifest?.MinAppVersion == "1.2.2",
                "the sample requires DeskOrder 1.2.2 (the v5 release)");
            var installPackage = demoPackage;
            if (PluginInstaller.HostVersion < new Version(1, 2, 2) && demoManifest != null)
            {
                var manifestPath = Path.Combine(rewriteDir, "plugin.json");
                var manifestJson = JsonNode.Parse(File.ReadAllText(manifestPath))!.AsObject();
                manifestJson["minAppVersion"] = PluginInstaller.HostVersion.ToString(3);
                File.WriteAllText(manifestPath, manifestJson.ToJsonString(
                    new JsonSerializerOptions { WriteIndented = true }));
                installPackage = Path.Combine(dir, "tool-settings-demo-lab.dopkg");
                if (File.Exists(installPackage)) File.Delete(installPackage);
                ZipFile.CreateFromDirectory(rewriteDir, installPackage);
            }

            var install = await service.Installer!.InstallFromFileAsync(installPackage);
            item.Check(install.Success,
                "installing the tool-settings-demo package succeeds"
                + (install.Error == null ? "" : " (" + install.Error + ")"));
            if (!install.Success)
            {
                item.Check(false, "tool-settings-demo install error: " + install.Error);
                return;
            }

            service.RefreshRecords();
            item.Check(service.GetRecord(ToolSettingsDemoId)?.KindValue == PluginKind.Tool,
                "the installed record is a tool plugin");
            item.Check(service.SetEnabled(ToolSettingsDemoId, true),
                "enabling the tool-settings demo plugin succeeds");
            service.ReloadForLab();
            item.Check(service.GetLoaded(ToolSettingsDemoId) != null,
                "the tool-settings demo plugin is loaded after reload");
            item.Check(PluginApiVersion.Current == 5, "the running plugin API version is 5");

            var demoEntry = settingsService.Find(ToolSettingsDemoId);
            item.Check(demoEntry != null && demoEntry.FieldCount == 3,
                "the real plugin DLL registers a schema with three fields");
            item.Check(demoEntry?.DisplayTitle == "时间戳设置",
                "the real schema title resolves in Chinese");
            item.Check(demoEntry?.PluginId == ToolSettingsDemoId,
                "the real schema entry is keyed by the plugin id");

            var localizedTool = tools.Find(ToolSettingsDemoId, "insert-timestamp");
            var plainTool = tools.Find(ToolSettingsDemoId, "preview-settings");
            item.Check(localizedTool != null && plainTool != null,
                "the real plugin DLL registers both commands");
            item.Check(localizedTool?.DisplayName == "插入时间戳",
                "the localized command name resolves in Chinese");
            item.Check(plainTool?.DisplayName == "Preview settings",
                "a command without LocalizedDisplayName keeps its plain DisplayName");

            loc.SetLanguage("en");
            item.Check(localizedTool?.DisplayName == "Insert timestamp",
                "the localized command name resolves in English after the language switch");
            item.Check(plainTool?.DisplayName == "Preview settings",
                "the plain command keeps the same name in English");
            item.Check(demoEntry?.DisplayTitle == "Timestamp settings",
                "the schema title re-resolves in English");
            loc.SetLanguage("zh");
            item.Check(localizedTool?.DisplayName == "插入时间戳",
                "switching back to Chinese restores the localized command name");

            // 命令执行默认值：还没保存过设置，时间戳应带 schema 默认后缀、不转大写。
            item.Check(localizedTool != null && await tools.InvokeAsync(localizedTool, PluginToolSurface.Tray),
                "invoking the localized command succeeds before any setting is saved");
            var demoStoragePath = PluginStorage.FilePath(ToolSettingsDemoId);
            var beforeSave = new JsonPluginStorage(demoStoragePath, null, null).Get("last_insert");
            item.Check(beforeSave.EndsWith(" - DeskOrder", StringComparison.Ordinal),
                "the command uses the default suffix before saving");

            // 通过设置服务保存真实插件的设置：宿主落盘 + Changed 回调更新插件内存状态。
            string? changedPluginId = null;
            Action<string> onChange = id => changedPluginId = id;
            settingsService.SettingsChanged += onChange;
            var demoSave = new Dictionary<string, string>
            {
                ["suffix"] = "!lab",
                ["uppercase"] = "true",
                ["format"] = "long"
            };
            string? demoSaveError = null;
            var demoSaved = demoEntry != null
                            && settingsService.Save(demoEntry, demoSave, out demoSaveError);
            item.Check(demoSaved,
                "saving the real plugin settings succeeds"
                + (demoSaveError == null ? "" : " (" + demoSaveError + ")"));
            settingsService.SettingsChanged -= onChange;
            item.Check(changedPluginId == ToolSettingsDemoId,
                "SettingsChanged carries the real plugin id");

            var demoStorage = new JsonPluginStorage(demoStoragePath, null, null);
            item.Check(demoStorage.Get("settings.suffix") == "!lab"
                       && demoStorage.Get("settings.uppercase") == "true"
                       && demoStorage.Get("settings.format") == "long",
                "the real plugin settings land in settings.json under settings.<key>");

            item.Check(localizedTool != null && await tools.InvokeAsync(localizedTool, PluginToolSurface.Tray),
                "invoking the command again after saving succeeds");
            var afterSave = new JsonPluginStorage(demoStoragePath, null, null).Get("last_insert");
            item.Check(afterSave.EndsWith("!LAB", StringComparison.Ordinal),
                "the command reads the saved suffix and uppercase setting (Changed callback applied)");
            item.Check(afterSave.Contains("年"),
                "the command uses the saved long time format");

            // ── 5) 设置窗口：有 schema 时渲染字段 + 截图 ──
            EnsureManagementWindow();
            var window = ManagementWindow;
            item.Check(window != null, "the management window exists for the settings dialog checks");
            if (window == null) return;
            window.Show();
            window.ShowSection("settings");
            await Task.Delay(900);
            if (window.MainContent.Content is not SettingsPage settingsPage)
            {
                item.Check(false, "the settings page is shown for the plugin settings entry");
                return;
            }

            item.Check(settingsPage.PluginSettingsButton.IsEnabled,
                "the plugin settings button is enabled while a schema is registered");

            var dialog = new PluginSettingsDialog { Owner = window };
            dialog.Show();
            await Task.Delay(700);
            var dialogShot = Path.Combine(dir, "shot-plugin-settings.png");
            SaveScreenshot(dialog, dialogShot);
            item.Check(File.Exists(dialogShot), "plugin settings dialog screenshot written");
            item.Check(dialog.SectionCount >= 1,
                "the settings dialog lists the registered plugin schema");
            item.Check(dialog.FieldCount == 3,
                "the settings dialog renders the real plugin's three fields");
            item.Check(dialog.SaveEnabled, "the settings dialog has an enabled save button");
            item.Check(dialog.SectionHeadersForLab.Any(h => h.Contains("时间戳设置")),
                "the settings dialog header carries the localized schema title");
            item.Check(dialog.FieldLabelsForLab.Contains("时间戳后缀")
                       && dialog.FieldLabelsForLab.Contains("复制时转大写")
                       && dialog.FieldLabelsForLab.Contains("时间格式"),
                "the settings dialog renders the localized field labels");
            dialog.Close();

            // ── 6) 插件工具页：命令名跟随语言 + 截图 ──
            loc.SetLanguage("en");
            window.ShowSection("plugintools");
            await Task.Delay(900);
            if (window.MainContent.Content is PluginToolsPage toolsPage)
            {
                toolsPage.RefreshList();
                item.Check(toolsPage.RowTitlesForLab.Contains("Insert timestamp"),
                    "the plugin tools page renders the English localized command name");
                item.Check(toolsPage.RowTitlesForLab.Contains("Preview settings"),
                    "the plugin tools page keeps the plain command name");
                var toolsShot = Path.Combine(dir, "shot-plugin-tools-localized.png");
                SaveScreenshot(window, toolsShot);
                item.Check(File.Exists(toolsShot), "localized plugin tools screenshot written");
                loc.SetLanguage("zh");
                toolsPage.RefreshList();
                item.Check(toolsPage.RowTitlesForLab.Contains("插入时间戳"),
                    "the plugin tools page refreshes to the Chinese command name");
            }
            else
            {
                item.Check(false, "the plugin tools page is shown for the localized name check");
                loc.SetLanguage("zh");
            }

            // ── 7) 禁用后：schema 消失、设置按钮置灰、空态窗口 ──
            item.Check(service.SetEnabled(ToolSettingsDemoId, false),
                "disabling the tool-settings demo plugin succeeds");
            service.ReloadForLab();
            item.Check(settingsService.Find(ToolSettingsDemoId) == null,
                "disabling the plugin removes its settings schema");
            window.ShowSection("settings");
            await Task.Delay(900);
            if (window.MainContent.Content is SettingsPage pageAfter)
            {
                item.Check(!pageAfter.PluginSettingsButton.IsEnabled,
                    "the plugin settings button is disabled when no schema is registered");
                item.Check(pageAfter.PluginSettingsHint.Text.Length > 0,
                    "the disabled entry still shows a hint");
            }
            else
            {
                item.Check(false, "the settings page is rebuilt after disabling the plugin");
            }

            var emptyDialog = new PluginSettingsDialog { Owner = window };
            emptyDialog.Show();
            await Task.Delay(400);
            item.Check(emptyDialog.EmptyStateVisibility == Visibility.Visible,
                "the settings dialog shows its empty state when no schema is registered");
            item.Check(emptyDialog.FieldCount == 0 && !emptyDialog.SaveEnabled,
                "the empty settings dialog has nothing to render or save");
            emptyDialog.Close();

            // 清理安装记录，别把示例插件留在隔离数据目录里。
            service.Uninstall(ToolSettingsDemoId, deleteData: true, out _);
            service.RefreshRecords();
            item.Check(service.GetRecord(ToolSettingsDemoId) == null,
                "the tool-settings demo plugin is uninstalled again");
        }
        finally
        {
            loc.SetLanguage(originalLanguage);
            settingsService.Clear();
            tools.Clear();
        }
    }
}
#endif
