using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Reflection;
using System.Reflection.Metadata;
using System.Reflection.PortableExecutable;
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
/// 程序集引用审计。对包内每个托管 DLL 做静态元数据检查（不执行插件代码），再一次性
/// collectible ALC 加载入口 DLL 确认入口类型。注意：这是卫生检查（防止插件误引宿主内部
/// 类型 / 夹带宿主程序集），不是安全沙箱。
/// </summary>
public static class PluginAssemblyInspector
{
    static readonly string[] AllowedPrefixes =
    {
        "System", "mscorlib", "netstandard", "WindowsBase",
        "PresentationCore", "PresentationFramework", "System.Xaml",
        "DeskOrder.PluginApi"
    };

    /// <summary>宿主程序集名：入口 DLL 的文件名/程序集名等于它 = 夹带宿主。</summary>
    static readonly string HostAssemblyName =
        typeof(PluginAssemblyInspector).Assembly.GetName().Name ?? "DeskOrder";

    static readonly string HostApiAssemblyName =
        typeof(IDeskOrderPlugin).Assembly.GetName().Name ?? "DeskOrder.PluginApi";

    public static PluginInspection Inspect(string pluginDir, string assemblyRelativePath, string entryType)
    {
        var result = new PluginInspection();

        // 1) 全包静态审计。旧实现只加载入口 DLL，于是 lib\ 里的第二个 DLL 完全不看：
        //    入口干净、旁边放一个引用 DeskOrder 的辅助 DLL 就能混进来。
        AuditPackageAssemblies(pluginDir, result);

        var assemblyPath = PluginPaths.ResolveInside(pluginDir, assemblyRelativePath);
        if (assemblyPath == null || !File.Exists(assemblyPath))
        {
            result.Ok = false;
            result.Errors.Add("entry assembly not found: " + assemblyRelativePath);
            return result;
        }
        // 静态审计已经判死（非法引用 / 坏 DLL）时不再加载入口：加载没有更多信息，
        // 只会把同一批引用错误重复报一遍。
        if (!result.Ok) return result;

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

    /// <summary>
    /// 扫描包内所有 <c>.dll</c>（包根、<c>lib\</c>、其它子目录 —— 与 .dopkg 的白名单一致），
    /// 用 <see cref="MetadataReader"/> 只读元数据、不加载程序集：
    /// <list type="bullet">
    /// <item>引用宿主主程序集 → 拒（保留旧文案，自测台断言了这个子串）；</item>
    /// <item>文件名或程序集名等于宿主程序集 → 拒（夹带宿主实现会让插件绕过 API 边界）；</item>
    /// <item>引用了既不在白名单、也不是包内其它 DLL 的程序集 → 拒；</item>
    /// <item>原生 DLL（合法 PE、没有 CorHeader）不加载也不拒，只记一条日志；</item>
    /// <item>托管 DLL 不可读 / 不是合法 PE / 有 CorHeader 但读不出元数据 → 拒。</item>
    /// </list>
    /// </summary>
    static void AuditPackageAssemblies(string pluginDir, PluginInspection result)
    {
        var assemblyPaths = Directory.EnumerateFiles(pluginDir, "*.dll", SearchOption.AllDirectories)
            .Where(path => string.Equals(Path.GetExtension(path), ".dll", StringComparison.OrdinalIgnoreCase))
            .OrderBy(path => path, StringComparer.OrdinalIgnoreCase)
            .ToList();
        if (assemblyPaths.Count == 0) return;

        // 包内 DLL 的简单名：插件可以引用包里的辅助 DLL，但只能引用包里真实存在的。
        var packageAssemblyNames = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
        foreach (var path in assemblyPaths) packageAssemblyNames.Add(Path.GetFileNameWithoutExtension(path));

        var seenReferences = new HashSet<string>(result.References, StringComparer.OrdinalIgnoreCase);
        foreach (var path in assemblyPaths)
        {
            var relative = PluginPaths.ToPackagePath(path, pluginDir);
            var stem = Path.GetFileNameWithoutExtension(path);
            if (string.Equals(stem, HostAssemblyName, StringComparison.OrdinalIgnoreCase)
                || string.Equals(stem, HostApiAssemblyName, StringComparison.OrdinalIgnoreCase))
            {
                result.Ok = false;
                result.Errors.Add($"package must not bundle a host assembly: {relative}");
                continue;
            }

            try
            {
                using var stream = File.OpenRead(path);
                using var peReader = new PEReader(stream);
                var headers = peReader.PEHeaders; // 非法 PE 在这里抛 BadImageFormatException
                if (headers.CorHeader == null)
                {
                    DzTrace.Always($"[Plugin] native assembly allowed but not inspected: {relative}");
                    continue;
                }
                if (!peReader.HasMetadata)
                {
                    result.Ok = false;
                    result.Errors.Add($"{relative} is a managed assembly without readable metadata");
                    continue;
                }

                var metadata = peReader.GetMetadataReader();
                var definition = metadata.GetAssemblyDefinition();
                var assemblyName = metadata.GetString(definition.Name);
                if (string.Equals(assemblyName, HostAssemblyName, StringComparison.OrdinalIgnoreCase)
                    || string.Equals(assemblyName, HostApiAssemblyName, StringComparison.OrdinalIgnoreCase))
                {
                    result.Ok = false;
                    result.Errors.Add($"package must not bundle a host assembly: {relative}");
                    continue;
                }

                foreach (var handle in metadata.AssemblyReferences)
                {
                    var reference = metadata.GetAssemblyReference(handle);
                    var name = metadata.GetString(reference.Name);
                    if (!seenReferences.Add(name)) continue;
                    result.References.Add(name);

                    if (string.Equals(name, HostAssemblyName, StringComparison.OrdinalIgnoreCase))
                    {
                        result.Ok = false;
                        result.Errors.Add("plugins must not reference DeskOrder (use DeskOrder.PluginApi)");
                        continue;
                    }
                    if (IsAllowed(name)) continue;
                    if (packageAssemblyNames.Contains(name)) continue;
                    result.Ok = false;
                    result.Errors.Add("reference not allowed: " + name);
                }
            }
            catch (BadImageFormatException)
            {
                result.Ok = false;
                result.Errors.Add($"{relative} is not a managed assembly");
            }
            catch (Exception ex) when (ex is IOException or UnauthorizedAccessException)
            {
                result.Ok = false;
                result.Errors.Add($"cannot read assembly {relative}: {ex.Message}");
            }
            catch (Exception ex)
            {
                result.Ok = false;
                result.Errors.Add($"cannot inspect assembly {relative}: {ex.Message}");
            }
        }
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
