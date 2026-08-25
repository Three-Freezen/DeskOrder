// ponytail 2026-08-24: 把 icon-{16,32,48,256}.png 打成多分辨率 DesktopZones.ico。
// 现有 .ico 只有 16+32，taskbar 在 125%+ DPI 下糊。
// Vista+ ICO 格式允许直接装 PNG（icon entry dwBytesInRes = PNG 文件大小，dwImageOffset 指向 PNG 字节）。
using System;
using System.IO;

string iconDir = Path.Combine(AppContext.BaseDirectory, "..", "..", "..", "..");
iconDir = Path.GetFullPath(iconDir);

if (args.Length > 0 && args[0] == "inspect")
{
    string ico = Path.Combine(iconDir, "DesktopZones.ico");
    byte[] b = File.ReadAllBytes(ico);
    int count = BitConverter.ToUInt16(b, 4);
    Console.WriteLine($"ICO has {count} entries");
    for (int i = 0; i < count; i++)
    {
        int off = 6 + 16 * i;
        int w = b[off]; int h = b[off + 1];
        int displayW = w == 0 ? 256 : w;
        int displayH = h == 0 ? 256 : h;
        uint size = BitConverter.ToUInt32(b, off + 8);
        Console.WriteLine($"Entry {i}: {displayW}x{displayH} ({size} bytes payload)");
    }
    return;
}

int[] sizes = { 16, 32, 48, 256 };
var entries = new (int Size, byte[] Data)[sizes.Length];
for (int i = 0; i < sizes.Length; i++)
    entries[i] = (sizes[i], File.ReadAllBytes(Path.Combine(iconDir, $"icon-{sizes[i]}.png")));

using var ms = new MemoryStream();
using var bw = new BinaryWriter(ms);
// ICONDIR (6 bytes)
bw.Write((ushort)0);     // reserved
bw.Write((ushort)1);     // type (ico)
bw.Write((ushort)entries.Length);
// ICONDIRENTRY (16 bytes each) — PNG 路径下 bpp=32, planes=1
int dataOffset = 6 + 16 * entries.Length;
foreach (var e in entries)
{
    byte w = e.Size == 256 ? (byte)0 : (byte)e.Size;
    bw.Write(w);          // width (0 = 256)
    bw.Write(w);          // height
    bw.Write((byte)0);    // color count
    bw.Write((byte)0);    // reserved
    bw.Write((ushort)1);  // planes
    bw.Write((ushort)32); // bit count (32-bit RGBA)
    bw.Write((uint)e.Data.Length);
    bw.Write((uint)dataOffset);
    dataOffset += e.Data.Length;
}
foreach (var e in entries) bw.Write(e.Data);
bw.Flush();
string target = Path.Combine(iconDir, "DesktopZones.ico");
File.WriteAllBytes(target, ms.ToArray());
Console.WriteLine($"Wrote {target} ({new FileInfo(target).Length} bytes)");