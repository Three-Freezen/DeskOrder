using System;
using System.Numerics;
using System.Security.Cryptography;

namespace DesktopZones.Helpers;

/// <summary>
/// Ed25519 验签（只验证，不签名、不生成随机数）。
///
/// 为什么自带：minisign 生态全程 Ed25519，而 .NET 10 的 BCL 只加了 ML-DSA / SLH-DSA 这类
/// 后量子算法，没有 Ed25519 的公开 API。这里按 RFC 8032 §5.1.7 用 <see cref="BigInteger"/>
/// 做域运算：单次验证毫秒级，只在安装/更新时调用一次，性能足够，也免得为一个验签引入
/// 第三方密码学库（项目约定零依赖）。
///
/// 安全边界：本类不接触私钥，输入只来自「公钥 32B / 签名 64B / 消息」。解析失败一律返回
/// false，不做任何"尽力恢复"，避免把畸形输入变成旁路。
/// </summary>
internal static class Ed25519
{
    static readonly BigInteger P = BigInteger.Pow(2, 255) - 19;
    static readonly BigInteger L = BigInteger.Pow(2, 252)
        + BigInteger.Parse("27742317777372353535851937790883648493");

    static readonly BigInteger D;
    static readonly BigInteger D2;
    static readonly BigInteger SqrtM1 = BigInteger.ModPow(2, (P - 1) / 4, P);
    static readonly Point Base;

    static Ed25519()
    {
        D = Mod(-121665 * Inverse(121666));
        D2 = Mod(2 * D);
        // 标准基点编码：y = 4/5，符号位 0。
        var encoded = Convert.FromHexString(
            "5866666666666666666666666666666666666666666666666666666666666666");
        if (!TryDecompress(encoded, out Base))
            throw new InvalidOperationException("Ed25519 base point failed to decode");
    }

    /// <summary>RFC 8032 验证。<paramref name="publicKey"/> 32B，<paramref name="signature"/> 64B。</summary>
    public static bool Verify(ReadOnlySpan<byte> publicKey, ReadOnlySpan<byte> message, ReadOnlySpan<byte> signature)
    {
        if (publicKey.Length != 32 || signature.Length != 64) return false;

        if (!TryDecompress(publicKey, out var a)) return false;
        if (!TryDecompress(signature[..32], out var r)) return false;

        var s = new BigInteger(signature[32..64], isUnsigned: true, isBigEndian: false);
        if (s >= L) return false;

        // k = SHA-512(R || A || M)：签名的前 32 字节是 R，后 32 字节是 S，S 不参与哈希。
        var buffer = new byte[32 + 32 + message.Length];
        signature[..32].CopyTo(buffer);
        publicKey.CopyTo(buffer.AsSpan(32));
        message.CopyTo(buffer.AsSpan(64));
        // RFC 8032：整个 512 位摘要按小端解释后 mod L（不是只取前 32 字节）。
        var hash = SHA512.HashData(buffer);
        var k = new BigInteger(hash, isUnsigned: true, isBigEndian: false) % L;

        var left = ScalarMult(Base, s);
        var right = Add(r, ScalarMult(a, k));
        return ProjectivelyEqual(left, right);
    }

    readonly struct Point
    {
        public readonly BigInteger X, Y, Z, T;
        public Point(BigInteger x, BigInteger y, BigInteger z, BigInteger t) { X = x; Y = y; Z = z; T = t; }
    }

    static readonly Point Identity = new(0, 1, 1, 0);

    static BigInteger Mod(BigInteger value)
    {
        var result = value % P;
        return result.Sign < 0 ? result + P : result;
    }

    static BigInteger Inverse(BigInteger value) => BigInteger.ModPow(Mod(value), P - 2, P);

    /// <summary>twisted Edwards 统一加法（a = -1）；同一套公式对倍点也成立。</summary>
    static Point Add(in Point p, in Point q)
    {
        var a = Mod((p.Y - p.X) * (q.Y - q.X));
        var b = Mod((p.Y + p.X) * (q.Y + q.X));
        var c = Mod(p.T * D2 * q.T);
        var d = Mod(p.Z * 2 * q.Z);
        var e = Mod(b - a);
        var f = Mod(d - c);
        var g = Mod(d + c);
        var h = Mod(b + a);
        return new Point(Mod(e * f), Mod(g * h), Mod(f * g), Mod(e * h));
    }

    static Point ScalarMult(in Point p, BigInteger scalar)
    {
        var result = Identity;
        var addend = p;
        while (scalar > 0)
        {
            if (!scalar.IsEven) result = Add(result, addend);
            addend = Add(addend, addend);
            scalar >>= 1;
        }
        return result;
    }

    static bool ProjectivelyEqual(in Point a, in Point b)
        => Mod(a.X * b.Z - b.X * a.Z).IsZero && Mod(a.Y * b.Z - b.Y * a.Z).IsZero;

    /// <summary>从 32 字节编码还原点（RFC 8032 §5.1.3）。任何非法编码直接 false。</summary>
    static bool TryDecompress(ReadOnlySpan<byte> encoded, out Point point)
    {
        point = Identity;
        if (encoded.Length != 32) return false;

        var yBytes = encoded.ToArray();
        var sign = (yBytes[31] >> 7) & 1;
        yBytes[31] &= 0x7F;
        var y = new BigInteger(yBytes, isUnsigned: true, isBigEndian: false);
        if (y >= P) return false;

        // x² = (y² - 1) / (d·y² + 1)
        var y2 = Mod(y * y);
        var u = Mod(y2 - 1);
        var v = Mod(D * y2 + 1);
        var x = BigInteger.ModPow(Mod(u * Inverse(v)), (P + 3) / 8, P);

        if (Mod(v * x * x) != u)
        {
            x = Mod(x * SqrtM1);
            if (Mod(v * x * x) != u) return false;
        }

        if (x.IsZero && sign == 1) return false;
        if ((int)(x & 1) != sign) x = P - x;

        point = new Point(x, y, 1, Mod(x * y));
        return true;
    }
}
