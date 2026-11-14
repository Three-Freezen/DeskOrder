using System;
using System.Buffers.Binary;

namespace DesktopZones.Helpers;

/// <summary>
/// BLAKE2b-512（RFC 7693）。只用于校验 minisign 的「预哈希」签名：
/// minisign 默认签名的是文件的 BLAKE2b-512 摘要（算法标识 <c>ED</c>），
/// 非预哈希模式（<c>Ed</c>）签的是文件原文。两种都要认，否则作者用官方
/// minisign 默认参数签出来的包会验不过。
/// </summary>
internal static class Blake2b
{
    const int BlockBytes = 128;

    static readonly ulong[] Iv =
    {
        0x6a09e667f3bcc908UL, 0xbb67ae8584caa73bUL, 0x3c6ef372fe94f82bUL, 0xa54ff53a5f1d36f1UL,
        0x510e527fade682d1UL, 0x9b05688c2b3e6c1fUL, 0x1f83d9abfb41bd6bUL, 0x5be0cd19137e2179UL
    };

    static readonly byte[,] Sigma = new byte[12, 16]
    {
        { 0, 1, 2, 3, 4, 5, 6, 7, 8, 9, 10, 11, 12, 13, 14, 15 },
        { 14, 10, 4, 8, 9, 15, 13, 6, 1, 12, 0, 2, 11, 7, 5, 3 },
        { 11, 8, 12, 0, 5, 2, 15, 13, 10, 14, 3, 6, 7, 1, 9, 4 },
        { 7, 9, 3, 1, 13, 12, 11, 14, 2, 6, 5, 10, 4, 0, 15, 8 },
        { 9, 0, 5, 7, 2, 4, 10, 15, 14, 1, 11, 12, 6, 8, 3, 13 },
        { 2, 12, 6, 10, 0, 11, 8, 3, 4, 13, 7, 5, 15, 14, 1, 9 },
        { 12, 5, 1, 15, 14, 13, 4, 10, 0, 7, 6, 3, 9, 2, 8, 11 },
        { 13, 11, 7, 14, 12, 1, 3, 9, 5, 0, 15, 4, 8, 6, 2, 10 },
        { 6, 15, 14, 9, 11, 3, 0, 8, 12, 2, 13, 7, 1, 4, 10, 5 },
        { 10, 2, 8, 4, 7, 6, 1, 5, 15, 11, 9, 14, 3, 12, 13, 0 },
        { 0, 1, 2, 3, 4, 5, 6, 7, 8, 9, 10, 11, 12, 13, 14, 15 },
        { 14, 10, 4, 8, 9, 15, 13, 6, 1, 12, 0, 2, 11, 7, 5, 3 }
    };

    /// <summary>计算 64 字节摘要。</summary>
    public static byte[] Hash512(ReadOnlySpan<byte> data)
    {
        var h = (ulong[])Iv.Clone();
        h[0] ^= 0x01010000UL ^ 64UL; // 无密钥、64 字节输出

        var block = new byte[BlockBytes];
        var m = new ulong[16];
        var v = new ulong[16];
        var offset = 0;

        // 保持最后一个块不压缩，作为终块带 final 标志。
        // RFC 7693 的计数器 t 是「累计已压缩字节数」，不是单块长度——
        // 传 128 而不是 offset+128，会让所有 >128 字节的输入算出错误摘要。
        while (data.Length - offset > BlockBytes)
        {
            data.Slice(offset, BlockBytes).CopyTo(block);
            Compress(h, block, m, v, (ulong)(offset + BlockBytes), isFinal: false);
            offset += BlockBytes;
        }

        var remaining = data.Length - offset;
        Array.Clear(block);
        data.Slice(offset, remaining).CopyTo(block);
        Compress(h, block, m, v, (ulong)data.Length, isFinal: true);

        var output = new byte[64];
        for (var i = 0; i < 8; i++)
            BinaryPrimitives.WriteUInt64LittleEndian(output.AsSpan(i * 8, 8), h[i]);
        return output;
    }

    static void Compress(ulong[] h, byte[] block, ulong[] m, ulong[] v, ulong counter, bool isFinal)
    {
        for (var i = 0; i < 16; i++)
            m[i] = BinaryPrimitives.ReadUInt64LittleEndian(block.AsSpan(i * 8, 8));

        Array.Copy(h, 0, v, 0, 8);
        Array.Copy(Iv, 0, v, 8, 8);
        v[12] ^= counter;
        v[13] ^= 0; // 高 64 位计数在 50 MB 上限内不可能溢出
        if (isFinal) v[14] = ~v[14];

        for (var round = 0; round < 12; round++)
        {
            G(v, 0, 4, 8, 12, m[Sigma[round, 0]], m[Sigma[round, 1]]);
            G(v, 1, 5, 9, 13, m[Sigma[round, 2]], m[Sigma[round, 3]]);
            G(v, 2, 6, 10, 14, m[Sigma[round, 4]], m[Sigma[round, 5]]);
            G(v, 3, 7, 11, 15, m[Sigma[round, 6]], m[Sigma[round, 7]]);
            G(v, 0, 5, 10, 15, m[Sigma[round, 8]], m[Sigma[round, 9]]);
            G(v, 1, 6, 11, 12, m[Sigma[round, 10]], m[Sigma[round, 11]]);
            G(v, 2, 7, 8, 13, m[Sigma[round, 12]], m[Sigma[round, 13]]);
            G(v, 3, 4, 9, 14, m[Sigma[round, 14]], m[Sigma[round, 15]]);
        }

        for (var i = 0; i < 8; i++) h[i] ^= v[i] ^ v[i + 8];
    }

    static void G(ulong[] v, int a, int b, int c, int d, ulong x, ulong y)
    {
        v[a] = v[a] + v[b] + x;
        v[d] = RotateRight(v[d] ^ v[a], 32);
        v[c] += v[d];
        v[b] = RotateRight(v[b] ^ v[c], 24);
        v[a] = v[a] + v[b] + y;
        v[d] = RotateRight(v[d] ^ v[a], 16);
        v[c] += v[d];
        v[b] = RotateRight(v[b] ^ v[c], 63);
    }

    static ulong RotateRight(ulong value, int bits) => (value >> bits) | (value << (64 - bits));
}
