using System.Reflection;
using System.Resources;
var asm = Assembly.LoadFrom(@"D:\BS\项目\DesktopZones\bin\Debug\net10.0-windows\DeskOrder.dll");
Console.WriteLine($"All {asm.GetManifestResourceNames().Length} top-level resources:");
foreach (var n in asm.GetManifestResourceNames()) Console.WriteLine($"  {n}");
Console.WriteLine("\nContents of DeskOrder.g.resources:");
using var s = asm.GetManifestResourceStream("DeskOrder.g.resources");
using var rr = new ResourceReader(s!);
foreach (var k in rr) Console.WriteLine($"  {k}");
