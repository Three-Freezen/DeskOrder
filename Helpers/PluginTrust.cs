using System;
using System.Collections.Generic;
using DeskOrder.PluginApi;
using DesktopZones.Models;

namespace DesktopZones.Helpers;

public enum TrustLevel
{
    /// <summary>没有签名。</summary>
    Unsigned,
    /// <summary>签名有效，且用的是信任列表里的公钥。</summary>
    Verified,
    /// <summary>有签名但验不过。</summary>
    Invalid
}

public sealed class TrustVerdict
{
    public TrustLevel Level { get; init; }
    public string? Error { get; init; }
    public string? KeyId { get; init; }
    public string? TrustedComment { get; init; }

    public static readonly TrustVerdict Unsigned = new() { Level = TrustLevel.Unsigned };

    public bool IsOfficialKey => Level == TrustLevel.Verified;
}

/// <summary>
/// 插件包的签名信任策略。
///
/// 为什么需要它：官方标记目前只看 registry 里的 <c>repo</c> / <c>author.github</c>，
/// 判定依据（registry.json）与被保护对象（.dopkg）在同一份可变文件里——能改 registry 的
/// 人就能让任意包冒充官方。签名把「发布者身份」挪出 registry：公钥硬编码在应用里，
/// 私钥只在发布者手里，registry 被改了也签不出官方签名。
///
/// 策略：官方代码插件（widget / zonebar / tool / integration）**必须**签名；
/// 声明式包（外观 / 语言）可选，但一旦带了签名就必须验得过，否则按篡改处理。
/// 非官方的包不看签名（我们也不认识它的公钥）。
/// </summary>
public static class PluginTrust
{
    /// <summary>
    /// 官方发布公钥（minisign 格式，56 字符）。私钥不在这里、也不在任何仓库里。
    /// 轮换流程：改这一行 → 发版 → 用新私钥重签；旧签名自然失效（宁可拒装，不可误信）。
    /// </summary>
    public const string OfficialPublicKey =
        "RWT/72KtwY32idb7wimBQFazwnbQLjqgiDOSODgDF1NekT78XEplY1xb";

    static readonly List<string> Keys = new() { OfficialPublicKey };

    /// <summary>自测台注入的临时密钥（只在 Debug 构建里存在，Release 走不到）。</summary>
    public static void AddTemporaryKey(string publicKey)
    {
#if DEBUG
        if (!string.IsNullOrWhiteSpace(publicKey)) Keys.Add(publicKey);
#else
        _ = publicKey;
#endif
    }

    public static void ResetTemporaryKeys()
    {
        Keys.Clear();
        Keys.Add(OfficialPublicKey);
    }

    /// <summary>官方 + 代码类：签名是硬要求。</summary>
    public static bool RequiresSignature(PluginKind kind, bool official)
        => official && kind.IsCode();

    public static TrustVerdict Check(string packagePath, string? signatureText)
    {
        var result = MiniSign.VerifyFile(packagePath, signatureText, Keys);
        return new TrustVerdict
        {
            Level = result.Status switch
            {
                PackageSignature.Valid => TrustLevel.Verified,
                // 第三方作者用自己钥匙签的包：我们没法确认身份，按「未签名」处理而不是「被篡改」。
                PackageSignature.Invalid when result.UntrustedKey => TrustLevel.Unsigned,
                PackageSignature.Invalid => TrustLevel.Invalid,
                _ => TrustLevel.Unsigned
            },
            Error = result.Error,
            KeyId = result.KeyId,
            TrustedComment = result.TrustedComment
        };
    }

    /// <summary>
    /// 判断这个包能不能装。<paramref name="official"/> 来自 registry 或本地签名推导。
    /// 返回 null = 放行；否则是拒绝原因。
    /// </summary>
    public static string? Reject(PluginKind kind, bool official, TrustVerdict verdict)
    {
        if (verdict.Level == TrustLevel.Invalid)
            return "signature check failed: " + (verdict.Error ?? "invalid signature");
        if (RequiresSignature(kind, official) && verdict.Level != TrustLevel.Verified)
            return "official code plugins must carry a valid minisign signature";
        return null;
    }

    /// <summary>签名状态的持久化字符串。</summary>
    public static string ToRecordValue(TrustVerdict verdict) => verdict.Level switch
    {
        TrustLevel.Verified => "verified",
        TrustLevel.Invalid => "invalid",
        _ => "unsigned"
    };
}
