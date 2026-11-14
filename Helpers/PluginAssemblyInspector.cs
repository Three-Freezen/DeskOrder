using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Reflection;
using System.Runtime.Loader;
using DeskOrder.PluginApi;
using DesktopZones.Models;

namespace DesktopZones.Helpers;

public sealed class PluginInspection
{
    public bool Ok { get; set; } = true;
    public List<string> Errors { get; } = new();
    public List<string> References { get; } = new();
    public List<string> WidgetTypeIds { get; } = new();
}

/// <summary>
/// 程序集引用审计。一次性 collectible ALC 加载入口 DLL，比对引用白名单后卸载。
/// 注意：这是卫生检查（防止插件误引宿主内部类型），不是安全沙箱。
/// </summary>
public static class PluginAssemblyInspector
{
    static readonly string[] AllowedPrefixes =
    {
        "System", "mscorlib", "netstandard", "WindowsBase",
        "PresentationCore", "PresentationFramework", "System.Xaml",
        "DeskOrder.PluginApi"
    };

    public static PluginInspection Inspect(string pluginDir, string assemblyRelativePath, string entryType)
    {
        var result = new PluginInspection();
        var assemblyPath = PluginPaths.ResolveInside(pluginDir, assemblyRelativePath);
        if (assemblyPath == null || !File.Exists(assemblyPath))
        {
            result.Ok = false;
            result.Errors.Add("entry assembly not found: " + assemblyRelativePath);
            return result;
        }

        var alc = new InspectLoadContext(pluginDir);
        try
        {
            Assembly assembly;
            try
            {
                assembly = alc.LoadFile(assemblyPath);
            }
            catch (BadImageFormatException)
            {
                result.Ok = false;
                result.Errors.Add("entry assembly is not a managed .NET assembly");
                return result;
            }
            catch (Exception ex)
            {
                result.Ok = false;
                result.Errors.Add("cannot load entry assembly: " + ex.Message);
                return result;
            }

            foreach (var reference in assembly.GetReferencedAssemblies())
            {
                var name = reference.Name ?? "";
                result.References.Add(name);
                if (string.Equals(name, "DeskOrder", StringComparison.OrdinalIgnoreCase))
                {
                    result.Ok = false;
                    result.Errors.Add("plugins must not reference DeskOrder (use DeskOrder.PluginApi)");
                    continue;
                }
                if (IsAllowed(name)) continue;
                var local = Path.Combine(pluginDir, name + ".dll");
                if (File.Exists(local)) continue;
                result.Ok = false;
                result.Errors.Add("reference not allowed: " + name);
            }

            Type? type;
            try
            {
                type = assembly.GetType(entryType, throwOnError: false);
            }
            catch (Exception ex)
            {
                result.Ok = false;
                result.Errors.Add("cannot read plugin types: " + ex.Message);
                return result;
            }

            if (type == null)
            {
                result.Ok = false;
                result.Errors.Add($"entry type not found: {entryType}");
                return result;
            }
            if (!typeof(IDeskOrderPlugin).IsAssignableFrom(type))
            {
                result.Ok = false;
                result.Errors.Add($"entry type does not implement IDeskOrderPlugin: {entryType}");
            }
            if (type.IsAbstract || type.GetConstructor(Type.EmptyTypes) == null)
            {
                result.Ok = false;
                result.Errors.Add("entry type needs a public parameterless constructor");
            }
        }
        catch (Exception ex)
        {
            result.Ok = false;
            result.Errors.Add(ex.Message);
        }
        finally
        {
            try { alc.Unload(); } catch { }
        }
        return result;
    }

    public static bool IsAllowed(string assemblyName)
        => AllowedPrefixes.Any(p =>
            assemblyName.Equals(p, StringComparison.OrdinalIgnoreCase)
            || assemblyName.StartsWith(p + ".", StringComparison.OrdinalIgnoreCase));

    /// <summary>审计用 ALC：包内 DLL 从插件目录加载，宿主/BCL 回落到默认 ALC
    /// （保证 <c>DeskOrder.PluginApi</c> 的类型标识与宿主一致）。</summary>
    sealed class InspectLoadContext : AssemblyLoadContext
    {
        readonly string _dir;

        public InspectLoadContext(string dir) : base("DeskOrder.PluginInspect", isCollectible: true)
        {
            _dir = dir;
        }

        protected override Assembly? Load(AssemblyName assemblyName)
        {
            var local = Path.Combine(_dir, assemblyName.Name + ".dll");
            if (File.Exists(local)) return LoadFile(local);
            var lib = Path.Combine(_dir, "lib", assemblyName.Name + ".dll");
            if (File.Exists(lib)) return LoadFile(lib);
            return null; // 回落到 Default ALC
        }

        public Assembly LoadFile(string path)
        {
            using var stream = File.OpenRead(path);
            using var buffer = new MemoryStream((int)stream.Length);
            stream.CopyTo(buffer);
            buffer.Position = 0;
            return LoadFromStream(buffer);
        }
    }
}
