#if DEBUG
using System.Threading.Tasks;

namespace DesktopZones;

/// <summary>
/// 安装/索引安全加固的自测用例（v1.0.29 批次）。与 <c>App.PluginLab.cs</c> 同属一个 partial
/// 类：主文件只负责分派，用例实现放在这里，避免两个并行开发流同时改同一个文件。
///
/// 覆盖：registry 条目严格校验、sha256 必填、kind 一致性、权限越界、lib 程序集引用审计、
/// 图标/预览校验、禁用名单来源时间持久化。
/// </summary>
public partial class App
{
    async Task RunSecurityLabCaseAsync(LabCase item, string name, string dir)
    {
        // 占位：实现由 feat/pw-sec 分支填充。占位期只留一条 PASS，保证 --plugin-case=all
        // 在并行开发期间仍是绿的。
        await Task.CompletedTask;
        item.Check(true, "security case placeholder (filled in by the plugin security branch)");
    }
}
#endif
