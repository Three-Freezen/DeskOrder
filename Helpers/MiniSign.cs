using System;
using System.Collections.Generic;
using System.IO;
using System.Text;

namespace DesktopZones.Helpers;

/// <summary>包的签名状态。</summary>
public enum PackageSignature
{
    /// <summary>没有签名文件。</summary>
    None,
    /// <summary>签名有效（用的是信任列表里的公钥）。</summary>
    Valid,
    /// <summary>有签名但验不过：包被改过、签名被搬过、或不是这把钥匙签的。</summary>
    Invalid
}

public sealed class MiniSignResult
{
    public PackageSignature Status { get; init; }
    public string? Error { get; init; }
    /// <summary>签名本身能验，但签发公钥不在信任列表里（第三方作者自签的情形）。</summary>
    public bool UntrustedKey { get; init; }
    /// <summary>签名里带的公钥 id（8 字节十六进制），用于显示「由哪把钥匙签的」。</summary>
    public string? KeyId { get; init; }
    public string? TrustedComment { get; init; }
    /// <summary>签名覆盖的是文件原文（<c>Ed</c>）还是 BLAKE2b-512 摘要（<c>ED</c>）。</summary>
    public string? Algorithm { get; init; }
}

/// <summary>
/// minisign 签名文件的解析与验证（兼容 minisign 0.9+ 的两种算法标识）。
///
/// 文件格式（4 行文本）：
/// <code>
/// untrusted comment: signature from minisign secret key
/// &lt;base64: 2B 算法标识 "Ed"/"ED" + 8B 公钥 id + 64B Ed25519 签名&gt;
/// trusted comment: timestamp:...	file:x.dopkg[	prehashed]
/// &lt;base64: 64B 全局签名，签的是「上面 64B 签名 + trusted comment 原文」&gt;
/// </code>
///
/// 第二行是主签名，覆盖文件内容；第四行是全局签名，把 trusted comment 绑到主签名上，
/// 防止有人把一段签名挪到另一段 trust 说明下。两道都验过才算有效。
/// </summary>
public static class MiniSign
{
    const int KeyIdBytes = 8;

    /// <summary>校验一个已落盘的文件。<paramref name="signatureText"/> 可以是整个 .minisig 文件内容。</summary>
    public static MiniSignResult VerifyFile(string path, string? signatureText, IReadOnlyList<string> trustedKeys)
    {
        byte[] data;
        try
        {
            data = File.ReadAllBytes(path);
        }
        catch (Exception ex)
        {
            return new MiniSignResult { Status = PackageSignature.Invalid, Error = "cannot read package: " + ex.Message };
        }
        return VerifyBytes(data, signatureText, trustedKeys);
    }

    public static MiniSignResult VerifyBytes(ReadOnlySpan<byte> data, string? signatureText,
        IReadOnlyList<string> trustedKeys)
    {
        if (string.IsNullOrWhiteSpace(signatureText))
            return new MiniSignResult { Status = PackageSignature.None };
        if (trustedKeys == null || trustedKeys.Count == 0)
            return new MiniSignResult { Status = PackageSignature.Invalid, Error = "no trusted public key is configured" };

        if (!TryParseSignature(signatureText, out var sig, out var parseError))
            return new MiniSignResult { Status = PackageSignature.Invalid, Error = parseError };

        // 签名里带公钥 id：只跟 id 对得上的公钥验签，不必拿每一把钥匙去试。
        var candidates = new List<byte[]>();
        foreach (var keyText in trustedKeys)
        {
            if (!TryParsePublicKey(keyText, out var keyId, out var key, out _)) continue;
            if (EqualsId(keyId, sig.KeyId)) candidates.Add(key);
        }
        if (candidates.Count == 0)
            return new MiniSignResult
            {
                Status = PackageSignature.Invalid,
                Error = $"the signature was made by an untrusted key (key id {sig.KeyIdHex})",
                UntrustedKey = true,
                KeyId = sig.KeyIdHex,
                TrustedComment = sig.TrustedComment,
                Algorithm = sig.Algorithm
            };

        // 有 trusted comment 却没有合法全局签名 = 文件被截断/拼接过。
        // 主签名仍然能对，但 trusted comment 成了没人签过的明文，不能算完整有效。
        if (sig.TrustedComment != null && sig.GlobalSignature == null)
            return new MiniSignResult
            {
                Status = PackageSignature.Invalid,
                Error = "the trusted comment is not signed (the .minisig file is truncated or was edited)",
                KeyId = sig.KeyIdHex,
                TrustedComment = sig.TrustedComment,
                Algorithm = sig.Algorithm
            };

        // 预哈希签名覆盖的是文件摘要；非预哈希覆盖文件原文。
        var payload = sig.Prehashed ? Blake2b.Hash512(data) : data.ToArray();

        foreach (var key in candidates)
        {
            if (!Ed25519.Verify(key, payload, sig.Signature)) continue;

            // 全局签名：把主签名与 trusted comment 绑死。
            if (sig.GlobalSignature is { Length: 64 })
            {
                var commentBytes = Encoding.UTF8.GetBytes(sig.TrustedComment ?? "");
                var signed = new byte[sig.Signature.Length + commentBytes.Length];
                sig.Signature.CopyTo(signed, 0);
                commentBytes.CopyTo(signed, sig.Signature.Length);
                if (!Ed25519.Verify(key, signed, sig.GlobalSignature))
                    return new MiniSignResult
                    {
                        Status = PackageSignature.Invalid,
                        Error = "the trusted comment signature does not match",
                        KeyId = sig.KeyIdHex,
                        TrustedComment = sig.TrustedComment,
                        Algorithm = sig.Algorithm
                    };
            }

            return new MiniSignResult
            {
                Status = PackageSignature.Valid,
                KeyId = sig.KeyIdHex,
                TrustedComment = sig.TrustedComment,
                Algorithm = sig.Algorithm
            };
        }

        return new MiniSignResult
        {
            Status = PackageSignature.Invalid,
            Error = "the signature does not match this package (it may have been replaced)",
            KeyId = sig.KeyIdHex,
            TrustedComment = sig.TrustedComment,
            Algorithm = sig.Algorithm
        };
    }

    /// <summary>
    /// 解析公钥文本：接受裸 base64（56 字符，<c>RW…</c>），也接受整个 <c>.pub</c> 文件
    /// （第一行 untrusted comment，第二行 base64）。
    /// </summary>
    public static bool TryParsePublicKey(string? text, out byte[] keyId, out byte[] publicKey, out string? error)
    {
        keyId = Array.Empty<byte>();
        publicKey = Array.Empty<byte>();
        error = null;
        if (string.IsNullOrWhiteSpace(text)) { error = "public key is empty"; return false; }

        string? payload = null;
        foreach (var line in SplitLines(text))
        {
            if (IsCommentLine(line)) continue;
            payload = line;
            break;
        }
        if (payload == null) { error = "public key has no base64 payload"; return false; }

        byte[] raw;
        try { raw = Convert.FromBase64String(payload); }
        catch { error = "public key is not valid base64"; return false; }

        // 42 = 2 字节算法 + 8 字节 id + 32 字节公钥
        if (raw.Length != 2 + KeyIdBytes + 32) { error = "public key has an unexpected length"; return false; }
        if (raw[0] != 'E' || raw[1] != 'd') { error = "public key is not an Ed25519 key"; return false; }

        keyId = raw.AsSpan(2, KeyIdBytes).ToArray();
        publicKey = raw.AsSpan(2 + KeyIdBytes, 32).ToArray();
        return true;
    }

    sealed class ParsedSignature
    {
        public string Algorithm = "";
        public bool Prehashed;
        public byte[] KeyId = Array.Empty<byte>();
        public string KeyIdHex = "";
        public byte[] Signature = Array.Empty<byte>();
        public string? TrustedComment;
        public byte[]? GlobalSignature;
    }

    static bool TryParseSignature(string text, out ParsedSignature signature, out string? error)
    {
        signature = new ParsedSignature();
        error = null;

        var lines = SplitLines(text);
        var payloads = new List<string>();
        var hasTrustedComment = false;
        foreach (var line in lines)
        {
            if (line.StartsWith("trusted comment:", StringComparison.OrdinalIgnoreCase))
            {
                hasTrustedComment = true;
                continue;
            }
            if (line.StartsWith("untrusted comment:", StringComparison.OrdinalIgnoreCase)) continue;
            payloads.Add(line);
        }
        if (payloads.Count == 0) { error = "signature file has no base64 payload"; return false; }

        byte[] raw;
        try { raw = Convert.FromBase64String(payloads[0]); }
        catch { error = "signature is not valid base64"; return false; }

        // 74 = 2 字节算法 + 8 字节 id + 64 字节签名
        if (raw.Length != 2 + KeyIdBytes + 64) { error = "signature has an unexpected length"; return false; }

        var algorithm = new string(new[] { (char)raw[0], (char)raw[1] });
        if (algorithm is not ("Ed" or "ED")) { error = $"unsupported signature algorithm '{algorithm}'"; return false; }

        signature.Algorithm = algorithm;
        signature.Prehashed = algorithm == "ED";
        signature.KeyId = raw.AsSpan(2, KeyIdBytes).ToArray();
        signature.KeyIdHex = Convert.ToHexString(signature.KeyId).ToLowerInvariant();
        signature.Signature = raw.AsSpan(2 + KeyIdBytes, 64).ToArray();

        // trusted comment 的原文要按「行内原样」取（里面常有 tab），所以不能走 Trim 后的列表。
        if (hasTrustedComment)
        {
            signature.TrustedComment = FindTrustedComment(text);
            if (payloads.Count > 1)
            {
                try
                {
                    var global = Convert.FromBase64String(payloads[1]);
                    if (global.Length == 64) signature.GlobalSignature = global;
                }
                catch { /* 下面按「缺全局签名」处理，由调用方决定是否接受 */ }
            }
        }
        return true;
    }

    /// <summary>取 trusted comment 的原文（去掉前缀与行尾 CR，保留中间的 tab）。</summary>
    static string? FindTrustedComment(string text)
    {
        foreach (var line in text.Replace("\r\n", "\n").Split('\n'))
        {
            if (!line.StartsWith("trusted comment:", StringComparison.OrdinalIgnoreCase)) continue;
            return line["trusted comment:".Length..].TrimStart();
        }
        return null;
    }

    static bool IsCommentLine(string line)
        => line.StartsWith("untrusted comment:", StringComparison.OrdinalIgnoreCase)
           || line.StartsWith("trusted comment:", StringComparison.OrdinalIgnoreCase);

    static List<string> SplitLines(string text)
    {
        var result = new List<string>();
        foreach (var line in text.Replace("\r\n", "\n").Replace('\r', '\n').Split('\n'))
        {
            var trimmed = line.Trim();
            if (trimmed.Length > 0) result.Add(trimmed);
        }
        return result;
    }

    static bool EqualsId(byte[] a, byte[] b)
    {
        if (a.Length != b.Length) return false;
        for (var i = 0; i < a.Length; i++) if (a[i] != b[i]) return false;
        return true;
    }
}
