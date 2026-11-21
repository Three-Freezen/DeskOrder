#if DEBUG
using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Text.Json;
using System.Threading.Tasks;
using System.Windows;
using DesktopZones.Helpers;
using DesktopZones.Models;
using DesktopZones.Services;
using DesktopZones.Views;

namespace DesktopZones;

/// <summary>
/// 语言包 RTL + 复数规则的自测用例（v1.2）。与 <c>App.PluginLab.cs</c> 同属一个 partial 类：
/// 主文件只负责分派，用例实现放在这里，避免两个文件同时长胖。
///
/// <c>lang-rtl</c>：运行时合成一个 ar（阿拉伯语）语言包 → 安装/切换 → 断言 RTL 文本、FlowDirection、
/// 缺词条回退、六种复数形态、桌面层窗口不镜像、切回/卸载后的恢复。
/// <c>lang-plural</c>：<see cref="PluralRules"/> 分类表 + 内置 en/zh 复数 + 运行时合成 de 包验证
/// 语言包复数覆盖，删掉 <c>_plural</c> 后退回普通模板。
/// 两个用例都在 finally 里卸载合成的语言包并切回中文，不污染同一 lab 目录的后续用例。
/// </summary>
public partial class App
{
    const string RtlLabPackId = "lab.lang-ar";
    const string PluralLabPackId = "lab.lang-de";

    // ── lang-rtl ──

    async Task RunLangRtlCaseAsync(LabCase item, string dir)
    {
        var service = PluginService.Instance;
        var loc = LocalizationService.Instance;
        var installer = service.Installer;
        if (installer == null) { item.Check(false, "the plugin installer is initialized"); return; }
        ManagementWindow? window = null;
        Guid? labZoneId = null;
        try
        {
            item.Check(LanguagePackService.IsRtlLanguage("ar"), "IsRtlLanguage(ar) is true");
            item.Check(LanguagePackService.IsRtlLanguage("he-IL") && !LanguagePackService.IsRtlLanguage("ja"),
                "IsRtlLanguage uses the primary language subtag");

            // 上一次失败运行可能留下包：先清掉再开始（用例要能重复跑）。
            CleanupLabLanguagePack(service, RtlLabPackId);
            loc.TrySetLanguage("zh");
            loc.ReloadLanguagePacks();

            var package = BuildLanguageLabPackage(dir, "lang-ar-1.0.0.dopkg", RtlLabPackId,
                "ar", "العربية", "en", ArabicLabTable(), ArabicLabPlurals());
            var install = await installer.InstallFromFileAsync(package);
            item.Check(install.Success, "an RTL language pack installs"
                                        + (install.Success ? "" : ": " + install.Error));
            if (!install.Success) return;
            service.RefreshRecords();
            loc.ReloadLanguagePacks();

            var record = service.GetRecord(RtlLabPackId);
            item.Check(record != null && record.Enabled && record.KindValue == PluginKind.Language,
                "the RTL pack is installed, enabled and marked as a language pack");
            item.Check(loc.AvailableLanguages.Contains("ar"), "ar appears in AvailableLanguages");
            item.Check(LanguagePackService.Instance.Resolve("ar")?.Rtl == true,
                "the pack entry carries the RTL flag");
            item.Check(LanguagePackService.Instance.GetPluralTable("ar") is { Count: > 0 },
                "the ar plural table is cached");
            var expectedCoverage = ArabicLabTable().Count / (double)LocalizationService.ReadHostSource().Count;
            var actualCoverage = LanguagePackService.Instance.Resolve("ar")?.Coverage ?? -1;
            item.Check(Math.Abs(actualCoverage - expectedCoverage) < 0.0001,
                "coverage counts normal entries only (_plural is excluded)");

            item.Check(loc.TrySetLanguage("ar"), "switching to ar succeeds");
            item.Check(loc.CurrentLanguage == "ar", "CurrentLanguage is ar");
            item.Check(loc.IsRightToLeft, "IsRightToLeft is true for ar");
            item.Check(loc["Manage.Zones"] == "المناطق", "translated key comes from the RTL pack");
            item.Check(loc["Plugin.Count"].Contains("المجتمع"), "two-placeholder template keeps its placeholders");

            // 缺词条回退：base=en，应该拿到内置英文而不是 key 本身。
            var english = LocalizationService.LoadFromDiskForTest(
                Path.Combine(LocalizationService.GetI18nDir(), "source.en.json"));
            item.Check(loc["Plugin.RestartNeeded"] == english["Plugin.RestartNeeded"],
                "a missing key falls back to the base language (en), not the key itself");
            item.Check(loc["Plugin.RestartNeeded"] != "Plugin.RestartNeeded", "no raw key leaks into the UI");

            // 阿拉伯语的六个复数类别各命中一次。
            item.Check(loc.Plural("PluginWidget.Count", 0) == "صفر عناصر (0)", "ar count 0 hits zero");
            item.Check(loc.Plural("PluginWidget.Count", 1) == "عنصر واحد (1)", "ar count 1 hits one");
            item.Check(loc.Plural("PluginWidget.Count", 2) == "عنصران (2)", "ar count 2 hits two");
            item.Check(loc.Plural("PluginWidget.Count", 3) == "3 عناصر", "ar count 3 hits few");
            item.Check(loc.Plural("PluginWidget.Count", 11) == "11 عنصرًا", "ar count 11 hits many");
            item.Check(loc.Plural("PluginWidget.Count", 100) == "100 عنصر", "ar count 100 hits other");

            // 管理窗口：RTL 语言下整体镜像（FlowDirection 由 App.Rtl.cs 的类处理器 + 语言事件维护）。
            EnsureManagementWindow();
            window = ManagementWindow;
            if (window == null) { item.Check(false, "management window exists"); return; }
            window.Show();
            window.ShowSection("zones");
            await Task.Delay(1200);
            item.Check(window.FlowDirection == FlowDirection.RightToLeft,
                "the management window is mirrored to RTL");
            SaveRtlWindowShot(window, Path.Combine(dir, "shot-lang-rtl.png"));
            item.Check(File.Exists(Path.Combine(dir, "shot-lang-rtl.png")), "RTL screenshot written");

            // 桌面层窗口按设计不镜像：分区窗口的拖拽/吸附/芯片顺序依赖绝对坐标，整体镜像会全乱。
            var zone = _zoneManager!.CreateZone("RtlLabZone", 320, 260, 420, 300);
            labZoneId = zone.Id;
            _zoneManager.ShowZone(zone);
            await Task.Delay(700);
            var zoneWindow = _zoneManager.GetZoneWindow(zone.Id);
            item.Check(zoneWindow != null && zoneWindow.IsVisible, "the desktop zone window is visible under RTL");
            item.Check(zoneWindow?.FlowDirection == FlowDirection.LeftToRight,
                "desktop-layer windows stay LTR while the UI language is RTL");
            _zoneManager.DeleteZone(zone.Id);
            labZoneId = null;

            // 切回中文：管理窗口立刻回到 LTR。
            loc.SetLanguage("zh");
            await Task.Delay(300);
            item.Check(loc.CurrentLanguage == "zh", "switched back to zh");
            item.Check(!loc.IsRightToLeft, "IsRightToLeft is false for zh");
            item.Check(window.FlowDirection == FlowDirection.LeftToRight,
                "the management window returns to LTR when the language changes");
            item.Check(loc["Manage.Zones"] == "分区", "built-in strings are back");

            // 再切回 ar，然后卸载包：当前语言回退 zh，窗口方向也要跟着恢复。
            item.Check(loc.TrySetLanguage("ar") && window.FlowDirection == FlowDirection.RightToLeft,
                "switching to ar again mirrors the window");
            var uninstall = service.Uninstall(RtlLabPackId, deleteData: true, out var uninstallError);
            item.Check(uninstall, "the RTL pack uninstalls"
                                  + (uninstallError == null ? "" : " (" + uninstallError + ")"));
            service.RefreshRecords();
            await Task.Delay(300);
            item.Check(service.GetRecord(RtlLabPackId) == null, "the pack record is removed");
            item.Check(loc.CurrentLanguage == "zh", "the active language falls back to zh");
            item.Check(!loc.AvailableLanguages.Contains("ar"), "ar disappears from the language list");
            item.Check(!loc.IsRightToLeft, "IsRightToLeft resets after the fallback");
            item.Check(window.FlowDirection == FlowDirection.LeftToRight,
                "the management window returns to LTR after the pack is removed");
            item.Check(loc["Manage.Zones"] == "分区", "the UI is not empty after the fallback");
        }
        catch (Exception ex)
        {
            item.Check(false, "exception: " + ex.Message);
            item.Details.Add(ex.ToString());
        }
        finally
        {
            try { if (labZoneId is { } id) _zoneManager?.DeleteZone(id); } catch { }
            CleanupLabLanguagePack(service, RtlLabPackId);
            try { loc.TrySetLanguage("zh"); } catch { }
            try { loc.ReloadLanguagePacks(); } catch { }
            try { window?.Hide(); } catch { }
        }
    }

    /// <summary>ar 语言包的普通词条（约 25 条宿主词条，占位符与宿主源词条一致）。</summary>
    static Dictionary<string, string> ArabicLabTable() => new()
    {
        ["Manage.Zones"] = "المناطق",
        ["Manage.Clocks"] = "الساعات",
        ["Manage.Calendars"] = "التقاويم",
        ["Manage.Docks"] = "الأرصفة",
        ["Manage.Notes"] = "الملاحظات",
        ["Manage.MediaPlayers"] = "المشغلات",
        ["Manage.Title"] = "إدارة المناطق",
        ["Manage.Sidebar.Zones"] = "{0} مناطق",
        ["Manage.Sidebar.Clocks"] = "{0} ساعات",
        ["Manage.Sidebar.Calendars"] = "{0} تقاويم",
        ["Manage.Sidebar.Docks"] = "{0} أرصفة",
        ["Manage.Sidebar.Notes"] = "{0} ملاحظات",
        ["Manage.Sidebar.MediaPlayers"] = "{0} مشغلات",
        ["Manage.Sidebar.Plugins"] = "{0} إضافات",
        ["Plugin.Count"] = "{0} في المجتمع · {1} مثبت",
        ["PluginWidget.Count"] = "{0} عنصر",
        ["PluginTool.Count"] = "{0} أوامر",
        ["Plugin.WidgetCount"] = "{0} ودجة",
        ["ZoneBay.Count"] = "{0} أرصفة",
        ["Plugin.PageTitle"] = "سوق الإضافات",
        ["ZoneBay.Title"] = "رصيف المناطق",
        ["Common.Cancel"] = "إلغاء",
        ["Common.Save"] = "حفظ",
        ["Common.Close"] = "إغلاق",
        ["Settings.PageTitle"] = "الإعدادات"
    };

    /// <summary>ar 语言包的 _plural 段：PluginWidget.Count 的六种形态都要带 {0}（宿主占位符）。</summary>
    static Dictionary<string, Dictionary<string, string>> ArabicLabPlurals() => new()
    {
        ["PluginWidget.Count"] = new Dictionary<string, string>
        {
            ["zero"] = "صفر عناصر ({0})",
            ["one"] = "عنصر واحد ({0})",
            ["two"] = "عنصران ({0})",
            ["few"] = "{0} عناصر",
            ["many"] = "{0} عنصرًا",
            ["other"] = "{0} عنصر"
        }
    };

    // ── lang-plural ──

    async Task RunLangPluralCaseAsync(LabCase item, string dir)
    {
        var service = PluginService.Instance;
        var loc = LocalizationService.Instance;
        var installer = service.Installer;
        if (installer == null) { item.Check(false, "the plugin installer is initialized"); return; }
        try
        {
            // 1) 纯函数：按语言 / 数字分类。
            item.Check(PluralRules.Category("en", 1) == "one" && PluralRules.Category("en", 0) == "other"
                        && PluralRules.Category("en", 2) == "other",
                "en: 1 = one, 0/2 = other");
            item.Check(PluralRules.Category("zh", 1) == "other" && PluralRules.Category("ja", 2) == "other"
                        && PluralRules.Category("ko", 0) == "other" && PluralRules.Category("vi", 5) == "other",
                "zh/ja/ko/vi always use other");
            item.Check(PluralRules.Category("fr", 0) == "one" && PluralRules.Category("fr", 1) == "one"
                        && PluralRules.Category("fr", 2) == "other",
                "fr: 0/1 = one, 2 = other");
            item.Check(PluralRules.Category("ru", 1) == "one" && PluralRules.Category("ru", 21) == "one"
                        && PluralRules.Category("ru", 11) == "many",
                "ru: 1/21 = one, 11 = many");
            item.Check(PluralRules.Category("ru", 2) == "few" && PluralRules.Category("ru", 4) == "few"
                        && PluralRules.Category("ru", 5) == "many" && PluralRules.Category("ru", 0) == "many",
                "ru: 2..4 = few, 0/5 = many");
            item.Check(PluralRules.Category("uk", 3) == "few" && PluralRules.Category("uk", 13) == "many",
                "uk follows the same rule as ru");
            item.Check(PluralRules.Category("pl", 1) == "one" && PluralRules.Category("pl", 2) == "few"
                        && PluralRules.Category("pl", 22) == "few" && PluralRules.Category("pl", 12) == "many"
                        && PluralRules.Category("pl", 5) == "many",
                "pl: 1 = one, 2/22 = few, 12/5 = many");
            item.Check(PluralRules.Category("cs", 1) == "one" && PluralRules.Category("cs", 2) == "few"
                        && PluralRules.Category("cs", 4) == "few" && PluralRules.Category("cs", 5) == "other",
                "cs: 1 = one, 2..4 = few, 5 = other");
            item.Check(PluralRules.Category("sk", 3) == "few", "sk follows the same rule as cs");
            item.Check(PluralRules.Category("ar", 0) == "zero" && PluralRules.Category("ar", 1) == "one"
                        && PluralRules.Category("ar", 2) == "two" && PluralRules.Category("ar", 3) == "few"
                        && PluralRules.Category("ar", 11) == "many" && PluralRules.Category("ar", 100) == "other"
                        && PluralRules.Category("ar", 103) == "few",
                "ar: 0/1/2/3/11/100/103 map to zero/one/two/few/many/other/few");
            item.Check(PluralRules.Category("he", 1) == "one" && PluralRules.Category("he", 2) == "two"
                        && PluralRules.Category("he", 3) == "other",
                "he: 1/2 = one/two, 3 = other");
            item.Check(PluralRules.Category("de", 1) == "one" && PluralRules.Category("de", 2) == "other"
                        && PluralRules.Category("tr", 1) == "one" && PluralRules.Category("tr", 2) == "other",
                "de/tr use one -> else other");
            item.Check(PluralRules.Category("ru-RU", 2) == "few" && PluralRules.Category("AR", 0) == "zero"
                        && PluralRules.Category("EN", 1) == "one",
                "language codes are case-insensitive and use the primary subtag");
            item.Check(PluralRules.Category("xx", 1) == "other" && PluralRules.Category(null, 1) == "other"
                        && PluralRules.Category("", 2) == "other",
                "unknown/empty languages fall back to other");

            // 2) 内置 en / zh 的复数文案。
            item.Check(loc.TrySetLanguage("en"), "switching to en succeeds");
            var enOne = loc.Plural("PluginWidget.Count", 1);
            var enTwo = loc.Plural("PluginWidget.Count", 2);
            item.Check(enOne == "1 item", "built-in en count 1 uses the singular form");
            item.Check(enTwo == "2 item(s)", "built-in en count 2 keeps the existing plural form");
            item.Check(enOne != enTwo, "en singular and plural differ");
            item.Check(!loc.IsRightToLeft, "IsRightToLeft is false for en");

            item.Check(loc.TrySetLanguage("zh"), "switching to zh succeeds");
            var zhOne = loc.Plural("PluginWidget.Count", 1);
            var zhFive = loc.Plural("PluginWidget.Count", 5);
            item.Check(zhOne == "1 项" && zhFive == "5 项", "built-in zh uses the single template for both counts");
            item.Check(zhOne.Substring(1) == zhFive.Substring(1), "zh keeps the same form regardless of the number");

            // 3) 语言包的 _plural 覆盖 + 删除后退回普通模板。
            CleanupLabLanguagePack(service, PluralLabPackId);
            loc.ReloadLanguagePacks();
            var package = BuildLanguageLabPackage(dir, "lang-de-1.0.0.dopkg", PluralLabPackId,
                "de", "Deutsch", "en", GermanLabTable(), GermanLabPlurals());
            var install = await installer.InstallFromFileAsync(package);
            item.Check(install.Success, "the German pack installs"
                                        + (install.Success ? "" : ": " + install.Error));
            if (!install.Success) return;
            service.RefreshRecords();
            loc.ReloadLanguagePacks();
            item.Check(loc.TrySetLanguage("de"), "switching to de succeeds");

            item.Check(loc.Plural("PluginWidget.Count", 1) == "1 Element",
                "the pack _plural wins for the singular");
            item.Check(loc.Plural("PluginWidget.Count", 2) == "2 Elemente",
                "the pack _plural wins for the plural");
            item.Check(loc.Plural("Manage.Sidebar.Zones", 1) == "1 Bereich"
                        && loc.Plural("Manage.Sidebar.Zones", 3) == "3 Bereiche",
                "a second key also mirrors the one/other forms");
            item.Check(LanguagePackService.Instance.GetPluralTable("de") is { Count: 2 },
                "the German plural table caches two keys");
            item.Check(!loc.IsRightToLeft, "German is not RTL");

            // 非法 _plural 必须拒装并说清楚是哪个 key / 哪个问题。
            var badCategory = BuildLanguageLabPackage(dir, "lang-plural-bad-category-1.0.0.dopkg",
                "lab.lang-bad-plural-category", "de", "Deutsch", "en", GermanLabTable(),
                new Dictionary<string, Dictionary<string, string>>
                {
                    ["PluginWidget.Count"] = new() { ["quad"] = "{0} X" }
                });
            var badCategoryResult = await installer.InstallFromFileAsync(badCategory);
            item.Check(!badCategoryResult.Success
                       && (badCategoryResult.Error ?? "").Contains("unknown plural category"),
                "a _plural entry with an unknown category is rejected");
            var badPlaceholder = BuildLanguageLabPackage(dir, "lang-plural-bad-placeholder-1.0.0.dopkg",
                "lab.lang-bad-plural-placeholder", "de", "Deutsch", "en", GermanLabTable(),
                new Dictionary<string, Dictionary<string, string>>
                {
                    ["Manage.Sidebar.Zones"] = new() { ["one"] = "ein Bereich" }
                });
            var badPlaceholderResult = await installer.InstallFromFileAsync(badPlaceholder);
            item.Check(!badPlaceholderResult.Success
                       && (badPlaceholderResult.Error ?? "").Contains("Manage.Sidebar.Zones")
                       && (badPlaceholderResult.Error ?? "").Contains("{0}"),
                "a _plural form with the wrong placeholders is rejected and names the key");
            item.Check(service.GetRecord("lab.lang-bad-plural-category") == null
                       && service.GetRecord("lab.lang-bad-plural-placeholder") == null,
                "rejected plural packs leave no record behind");

            // 删掉 payload 里的 _plural：重新校验后语言包只提供普通模板，Plural() 退回 Get()。
            var payloadPath = Path.Combine(PluginPaths.VersionDir(PluralLabPackId, "1.0.0"), "i18n", "de.json");
            item.Check(File.Exists(payloadPath), "the installed payload is on disk");
            var withoutPlural = new Dictionary<string, string>
            {
                ["Manage.Zones"] = "Bereiche",
                ["Manage.Sidebar.Zones"] = "{0} Bereiche",
                ["PluginWidget.Count"] = "{0} Elemente gesamt",
                ["PluginTool.Count"] = "{0} Befehle",
                ["Common.Cancel"] = "Abbrechen"
            };
            File.WriteAllText(payloadPath,
                JsonSerializer.Serialize(withoutPlural, new JsonSerializerOptions { WriteIndented = true }));
            service.RefreshRecords();
            loc.ReloadLanguagePacks();
            item.Check(LanguagePackService.Instance.GetPluralTable("de") is { Count: 0 },
                "removing _plural clears the cached plural table");
            item.Check(loc.Plural("PluginWidget.Count", 1) == "1 Elemente gesamt",
                "without _plural Plural() falls back to the normal template");
            item.Check(loc.Plural("PluginWidget.Count", 5) == "5 Elemente gesamt",
                "the fallback works for every count (no exception)");

            // 4) 卸载后回退：语言回 zh，内置中文复数恢复。
            var uninstall = service.Uninstall(PluralLabPackId, deleteData: true, out var uninstallError);
            item.Check(uninstall, "the German pack uninstalls"
                                  + (uninstallError == null ? "" : " (" + uninstallError + ")"));
            service.RefreshRecords();
            item.Check(loc.CurrentLanguage == "zh", "the active language falls back to zh");
            item.Check(loc.Plural("PluginWidget.Count", 2) == "2 项",
                "built-in zh plural is restored after the fallback");
        }
        catch (Exception ex)
        {
            item.Check(false, "exception: " + ex.Message);
            item.Details.Add(ex.ToString());
        }
        finally
        {
            CleanupLabLanguagePack(service, PluralLabPackId);
            try { loc.TrySetLanguage("zh"); } catch { }
            try { loc.ReloadLanguagePacks(); } catch { }
        }
    }

    /// <summary>de 语言包的普通词条；PluginWidget.Count 的普通模板用于验证「删掉 _plural 后回退」。</summary>
    static Dictionary<string, string> GermanLabTable() => new()
    {
        ["Manage.Zones"] = "Bereiche",
        ["Manage.Sidebar.Zones"] = "{0} Bereiche",
        ["PluginWidget.Count"] = "{0} Elemente gesamt",
        ["PluginTool.Count"] = "{0} Befehle",
        ["Common.Cancel"] = "Abbrechen"
    };

    static Dictionary<string, Dictionary<string, string>> GermanLabPlurals() => new()
    {
        ["PluginWidget.Count"] = new Dictionary<string, string>
        {
            ["one"] = "{0} Element",
            ["other"] = "{0} Elemente"
        },
        ["Manage.Sidebar.Zones"] = new Dictionary<string, string>
        {
            ["one"] = "{0} Bereich",
            ["other"] = "{0} Bereiche"
        }
    };

    // ── 夹具 ──

    /// <summary>
    /// 运行时合成一个语言包 .dopkg（plugin.json + i18n/&lt;code&gt;.json + icon.png）。
    /// 只用于自测，不落配置索引之外的任何东西。
    /// </summary>
    static string BuildLanguageLabPackage(string dir, string fileName, string id,
        string code, string nativeName, string baseCode,
        Dictionary<string, string> table,
        Dictionary<string, Dictionary<string, string>>? plurals)
    {
        var stage = Path.Combine(dir, "lang-stage-" + Guid.NewGuid().ToString("N"));
        try
        {
            Directory.CreateDirectory(Path.Combine(stage, "i18n"));
            var payload = new Dictionary<string, object?>();
            foreach (var pair in table) payload[pair.Key] = pair.Value;
            if (plurals != null && plurals.Count > 0)
            {
                var pluralNode = new Dictionary<string, object?>();
                foreach (var pair in plurals) pluralNode[pair.Key] = pair.Value;
                payload["_plural"] = pluralNode;
            }
            File.WriteAllText(Path.Combine(stage, "i18n", code + ".json"),
                JsonSerializer.Serialize(payload, new JsonSerializerOptions { WriteIndented = true }));

            var hostKeys = LocalizationService.ReadHostSource().Count;
            var manifest = new Dictionary<string, object?>
            {
                ["schemaVersion"] = 1,
                ["id"] = id,
                ["name"] = new Dictionary<string, string>
                {
                    ["zh"] = "语言包自测", ["en"] = "Language lab pack"
                },
                ["summary"] = new Dictionary<string, string>
                {
                    ["zh"] = "自测用例运行时合成的语言包。", ["en"] = "A runtime language pack for self-tests."
                },
                ["version"] = "1.0.0",
                ["author"] = new Dictionary<string, string> { ["name"] = "lab", ["github"] = "lab" },
                ["repo"] = "lab/DeskOrder-LanguageLab",
                ["license"] = "MIT",
                ["kind"] = "language",
                ["apiVersion"] = 0,
                ["minAppVersion"] = "1.0.0",
                ["language"] = new Dictionary<string, object?>
                {
                    ["code"] = code,
                    ["nativeName"] = nativeName,
                    ["base"] = baseCode,
                    ["coverage"] = hostKeys <= 0 ? 0.05 : Math.Min(1.0, table.Count / (double)hostKeys)
                },
                ["payload"] = new Dictionary<string, string> { ["file"] = "i18n/" + code + ".json" },
                ["icon"] = "icon.png"
            };
            File.WriteAllText(Path.Combine(stage, "plugin.json"),
                JsonSerializer.Serialize(manifest, new JsonSerializerOptions { WriteIndented = true }));
            File.WriteAllBytes(Path.Combine(stage, "icon.png"), CreatePng(64, 64));

            var package = Path.Combine(dir, fileName);
            PluginPackages.CreatePackage(stage, package);
            return package;
        }
        finally
        {
            try { if (Directory.Exists(stage)) Directory.Delete(stage, recursive: true); } catch { }
        }
    }

    /// <summary>卸载自测语言包（不存在时静默），并让语言列表回到最新状态。</summary>
    static void CleanupLabLanguagePack(PluginService service, string pluginId)
    {
        try
        {
            if (service.GetRecord(pluginId) != null)
                service.Uninstall(pluginId, deleteData: true, out _);
        }
        catch { }
    }

    /// <summary>
    /// RTL 窗口截图。WPF 的 RTL 是在窗口根上做水平镜像（文字渲染端预先反向一次），
    /// <see cref="SaveScreenshot"/> 抓内容根得到的是「镜像前」的画面，直接看文字是左右颠倒的。
    /// 这里抓完再水平翻转一次，得到的就是用户实际看到的镜像布局 + 正常可读的文字。
    /// （不用屏幕抓取：自测台跑的时候桌面上可能压着别的窗口。）
    /// </summary>
    static void SaveRtlWindowShot(Window window, string path)
    {
        SaveScreenshot(window, path);
        FlipPngHorizontally(path);
    }

    /// <summary>水平翻转 PNG（RTL 的 RenderTargetBitmap 截图补正）。</summary>
    static void FlipPngHorizontally(string path)
    {
        try
        {
            using var source = new System.Drawing.Bitmap(path);
            source.RotateFlip(System.Drawing.RotateFlipType.RotateNoneFlipX);
            source.Save(path, System.Drawing.Imaging.ImageFormat.Png);
        }
        catch (Exception ex)
        {
            DzTrace.Always("[LangLab] cannot flip the RTL screenshot: " + ex.Message);
        }
    }
}
#endif
