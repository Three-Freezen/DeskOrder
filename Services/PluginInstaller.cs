using System;
using System.IO;
using System.IO.Compression;
using System.Net.Http;
using System.Threading;
using System.Threading.Tasks;
using System.Windows.Media.Imaging;
using DeskOrder.PluginApi;
using DesktopZones.Helpers;
using DesktopZones.Models;

namespace DesktopZones.Services;

public sealed class PluginInstallResult
{
    public bool Success { get; set; }
    public string? Error { get; set; }
    public PluginManifest? Manifest { get; set; }
    public PluginRecord? Record { get; set; }
    public string? InstallDir { get; set; }
    public string Sha256 { get; set; } = "";
}

/// <summary>本地包的「安装前预览」（不落盘到插件目录）。</summary>
public sealed class PluginPackagePreview
{
    public required PluginManifest Manifest { get; init; }
    public string Sha256 { get; init; } = "";
    public long Size { get; init; }
    /// <summary>安装确认弹窗的「包含内容」：语言包给覆盖率，代码包暂空。</summary>
    public string Contents { get; init; } = "";
    /// <summary>语言包按宿主词条重算的覆盖率（0..1）。</summary>
    public double? Coverage { get; init; }
    /// <summary>包旁的 .minisig 验签结果（没有签名文件时是 Unsigned）。</summary>
    public TrustVerdict Trust { get; init; } = TrustVerdict.Unsigned;
    /// <summary>签名由官方公钥签发 → 即使脱离 registry 也认官方。</summary>
    public bool Official { get; init; }
}

/// <summary>下载 / 校验 / 安全解压 / 原子落盘 / 卸载。</summary>
public sealed class PluginInstaller
{
    static readonly HttpClient Http = new() { Timeout = TimeSpan.FromSeconds(90) };

    readonly ConfigService _configService;

    public PluginInstaller(ConfigService configService) => _configService = configService;

    public static readonly Version HostVersion = ParseHostVersion();

    const long MaxSignatureBytes = 16 * 1024;

    // 图标 / 截图也要限：manifest 只保证路径在包内，不保证文件真的是图片。
    // 卡片与详情页都会把 icon 解成位图，一张 100 MB 的「png」或 PNG 炸弹会直接把 UI 拖死。
    const long MaxIconBytes = 256 * 1024;
    const long MaxPreviewBytes = 1536 * 1024;
    const int MinIconSide = 16;
    const int MaxIconSide = 1024;
    const int MaxPreviews = 4;
    static readonly string[] SignatureHosts =
    {
        "github.com", "raw.githubusercontent.com", "objects.githubusercontent.com"
    };

    static Version ParseHostVersion()
    {
        var raw = AppVersion.Current;
        var dash = raw.IndexOfAny(new[] { '-', '+' });
        if (dash > 0) raw = raw[..dash];
        return Version.TryParse(raw, out var version) ? version : new Version(1, 0, 0);
    }

    /// <summary>registry 条目声明的签名：内联文本优先，其次是 Release 资产地址。</summary>
    static async Task<string?> LoadSignatureTextAsync(PluginCatalogEntry entry, CancellationToken ct)
    {
        if (!string.IsNullOrWhiteSpace(entry.Signature)) return entry.Signature;
        if (string.IsNullOrWhiteSpace(entry.SignatureUrl)) return null;

        if (!Uri.TryCreate(entry.SignatureUrl.Trim(), UriKind.Absolute, out var uri)) return null;
        if (uri.Scheme != Uri.UriSchemeHttps) return null;
        // 只从 GitHub 域名取签名：签名文件很小，但也没必要为它打开任意 URL 的出口。
        if (!SignatureHosts.Any(host => uri.Host.Equals(host, StringComparison.OrdinalIgnoreCase))) return null;

        try
        {
            using var response = await Http.GetAsync(uri, HttpCompletionOption.ResponseHeadersRead, ct)
                .ConfigureAwait(false);
            response.EnsureSuccessStatusCode();
            if (response.Content.Headers.ContentLength > MaxSignatureBytes) return null;
            var text = await response.Content.ReadAsStringAsync(ct).ConfigureAwait(false);
            return text.Length <= MaxSignatureBytes ? text : null;
        }
        catch
        {
            // 签名取不到不能当成「验签通过」：返回 null → 官方代码包会被拒装。
            return null;
        }
    }

    /// <summary>本地导入时，包旁边如果放着同名 <c>.minisig</c>，一并验一下。</summary>
    static string? ReadSidecarSignature(string packagePath)
    {
        try
        {
            var path = packagePath + ".minisig";
            if (!File.Exists(path)) return null;
            if (new FileInfo(path).Length > MaxSignatureBytes) return null;
            return File.ReadAllText(path);
        }
        catch { return null; }
    }

    public async Task<PluginInstallResult> InstallFromCatalogAsync(PluginCatalogEntry entry,
        IProgress<double>? progress = null, CancellationToken ct = default)
    {
        if (string.IsNullOrWhiteSpace(entry.DownloadUrl))
            return new PluginInstallResult { Error = "downloadUrl is missing" };
        if (entry.Size > PluginPackages.MaxPackageBytes)
            return new PluginInstallResult { Error = "package is larger than 50 MB" };
        // 市场安装必须能核对字节：registry 没写 sha256（或被改成非法值）时直接拒装，
        // 不能退回「下载完就当它是真的」——那就是给中间人/CDN 错误一份免费通行证。
        if (!PluginPackages.IsValidSha256(entry.Sha256))
            return new PluginInstallResult
            {
                Error = "the marketplace entry has no valid sha256; refusing to install"
            };

        var blocked = PluginBlocklist.Find(entry.Id, entry.Version);
        if (blocked != null)
            return new PluginInstallResult
            {
                Error = "this version is on the blocked list: "
                        + PluginBlocklist.ReasonOf(blocked, LocalizationService.Instance.CurrentLanguage)
            };

        PluginPaths.EnsureCreated();
        // 临时文件名不能直接用 registry 里的 id/version：id 允许 "."，像 "..\\..\\x" 这样的值
        // 拼进路径就能写到 Plugins 目录之外。改成 id@version 的 SHA-256 前缀，永不含分隔符。
        var tempName = Convert.ToHexString(
            System.Security.Cryptography.SHA256.HashData(
                System.Text.Encoding.UTF8.GetBytes($"{entry.Id}@{entry.Version}")))[..16].ToLowerInvariant();
        var partPath = Path.Combine(PluginPaths.CacheDir, $"{tempName}.part");
        try
        {
            using var response = await Http.GetAsync(entry.DownloadUrl, HttpCompletionOption.ResponseHeadersRead, ct)
                .ConfigureAwait(false);
            response.EnsureSuccessStatusCode();
            if (response.Content.Headers.ContentLength > PluginPackages.MaxPackageBytes)
                return new PluginInstallResult { Error = "package is larger than 50 MB" };

            await using (var input = await response.Content.ReadAsStreamAsync(ct).ConfigureAwait(false))
            await using (var output = File.Create(partPath))
            {
                var buffer = new byte[81920];
                long total = 0;
                long? length = response.Content.Headers.ContentLength;
                int read;
                while ((read = await input.ReadAsync(buffer, ct).ConfigureAwait(false)) > 0)
                {
                    total += read;
                    if (total > PluginPackages.MaxPackageBytes)
                        return new PluginInstallResult { Error = "package is larger than 50 MB" };
                    await output.WriteAsync(buffer.AsMemory(0, read), ct).ConfigureAwait(false);
                    if (length is > 0) progress?.Report((double)total / length.Value);
                }
            }

            var actual = PluginPackages.Sha256OfFile(partPath);
            if (!string.Equals(actual, entry.Sha256.Trim(), StringComparison.OrdinalIgnoreCase))
                return new PluginInstallResult
                {
                    Error = "SHA-256 mismatch (the package may have been replaced)"
                };

            var signatureText = await LoadSignatureTextAsync(entry, ct).ConfigureAwait(false);
            var existingEnabled = _configService.Load().Plugins
                .FirstOrDefault(r => string.Equals(r.Id, entry.Id, StringComparison.OrdinalIgnoreCase))?.Enabled;
            return await Task.Run(() =>
            {
                // 验签在后台线程：Ed25519 + BLAKE2b 是纯 CPU 工作，别卡 UI。
                var trust = PluginTrust.Check(partPath, signatureText);
                return InstallPackageCore(partPath, "market", entry.Official,
                    entry.Sha256, entry.Id, entry.Version, trust, entry.Permissions, existingEnabled,
                    entry.KindValue);
            }, ct).ConfigureAwait(false);
        }
        catch (OperationCanceledException)
        {
            return new PluginInstallResult { Error = "cancelled" };
        }
        catch (Exception ex)
        {
            return new PluginInstallResult { Error = ex.Message };
        }
        finally
        {
            try { if (File.Exists(partPath)) File.Delete(partPath); } catch { }
        }
    }

    public Task<PluginInstallResult> InstallFromFileAsync(string path)
        => Task.Run(() => InstallFromFileCore(path));

    /// <summary>本地导入前预览：解到临时目录读 manifest，不落安装目录。</summary>
    public PluginPackagePreview? PreviewPackage(string path, out string? error)
    {
        error = null;
        var stage = Path.Combine(PluginPaths.CacheDir, "preview-" + Guid.NewGuid().ToString("N"));
        try
        {
            using (var archive = ZipFile.OpenRead(path))
            {
                if (!PluginPackages.Extract(archive, stage, out var extractError, out _))
                {
                    error = extractError ?? "cannot read package";
                    return null;
                }
            }
            var manifest = PluginPackages.ReadManifest(stage, out var manifestError);
            if (manifest == null)
            {
                error = manifestError;
                return null;
            }
            var errors = manifest.Validate(HostVersion, forLocalImport: true);
            if (errors.Count > 0)
            {
                error = string.Join("; ", errors);
                return null;
            }
            if (manifest.KindValue is not (PluginKind.Widget or PluginKind.ZoneBar or PluginKind.Tool or PluginKind.Language or PluginKind.Appearance))
            {
                error = "this build supports widgets, zone bays, tools, language packs and appearance packs; data sources arrive in the next release";
                return null;
            }

            // 包旁的 .minisig：若有且由官方公钥签发，就按官方包对待（签名比 registry 更可信）。
            var signatureText = ReadSidecarSignature(path);
            var trust = signatureText == null ? TrustVerdict.Unsigned : PluginTrust.Check(path, signatureText);
            if (trust.Level == TrustLevel.Invalid)
            {
                error = trust.Error ?? "signature check failed";
                return null;
            }

            double? coverage = null;
            var contents = "";
            if (manifest.KindValue == PluginKind.Language)
            {
                var validation = LanguagePackService.Instance.Validate(stage, manifest);
                if (!validation.Ok)
                {
                    error = string.Join("; ", System.Linq.Enumerable.Take(validation.Errors, 3));
                    return null;
                }
                coverage = validation.Coverage;
                contents = LocalizationService.Instance.Get("Plugin.Contents.Language",
                    manifest.Language!.NativeName,
                    (int)Math.Round(validation.Coverage * 100),
                    string.IsNullOrWhiteSpace(manifest.Language.Base) ? "en" : manifest.Language.Base);
            }
            else if (manifest.KindValue == PluginKind.Appearance)
            {
                var validation = AppearancePackService.Instance.Validate(stage, manifest);
                if (!validation.Ok)
                {
                    error = string.Join("; ", System.Linq.Enumerable.Take(validation.Errors, 3));
                    return null;
                }
                contents = LocalizationService.Instance.Get("Plugin.Contents.Appearance",
                    validation.MaterialCount, validation.PresetCount);
            }

            return new PluginPackagePreview
            {
                Manifest = manifest,
                Sha256 = PluginPackages.Sha256OfFile(path),
                Size = new FileInfo(path).Length,
                Contents = contents,
                Coverage = coverage,
                Trust = trust,
                Official = trust.Level == TrustLevel.Verified
            };
        }
        catch (Exception ex)
        {
            error = ex.Message;
            return null;
        }
        finally
        {
            try { if (Directory.Exists(stage)) Directory.Delete(stage, recursive: true); } catch { }
        }
    }

    PluginInstallResult InstallFromFileCore(string path)
    {
        if (!File.Exists(path)) return new PluginInstallResult { Error = "file not found" };
        if (new FileInfo(path).Length > PluginPackages.MaxPackageBytes)
            return new PluginInstallResult { Error = "package is larger than 50 MB" };
        var sha = PluginPackages.Sha256OfFile(path);
        // 本地导入默认不当官方；但如果旁边有官方签名的 .minisig 且验得过，
        // 就说明这份字节确实出自官方发布，可以认官方标记。
        var signatureText = ReadSidecarSignature(path);
        var trust = signatureText == null ? TrustVerdict.Unsigned : PluginTrust.Check(path, signatureText);
        if (trust.Level == TrustLevel.Invalid)
            return new PluginInstallResult { Error = trust.Error ?? "signature check failed" };
        var result = InstallPackageCore(path, "local", trust.Level == TrustLevel.Verified, sha, null, null, trust);
        result.Sha256 = sha;
        return result;
    }

    /// <remarks>
    /// <paramref name="expectedKind"/> 是市场条目声明的类型（本地导入传 null）。确认弹窗按
    /// registry 的类型渲染（「声明式，不执行代码」等文案全来自它），真正执行的是包内 manifest；
    /// 两者不一致时必须拒装，否则作者能把代码插件伪装成不执行代码的类型展示给用户。
    /// </remarks>
    internal PluginInstallResult InstallPackageCore(string packagePath, string source, bool official,
        string? expectedSha, string? expectedId, string? expectedVersion, TrustVerdict? trust = null,
        IReadOnlyList<string>? declaredPermissions = null, bool? enabledOverride = null,
        PluginKind? expectedKind = null)
    {
        trust ??= TrustVerdict.Unsigned;
        var stage = Path.Combine(PluginPaths.CacheDir, "stage-" + Guid.NewGuid().ToString("N"));
        try
        {
            using var archive = ZipFile.OpenRead(packagePath);
            if (!PluginPackages.Extract(archive, stage, out var extractError, out _))
                return new PluginInstallResult { Error = extractError ?? "cannot extract package" };
        }
        catch (InvalidDataException)
        {
            return new PluginInstallResult { Error = "not a valid .dopkg (zip) file" };
        }
        catch (Exception ex)
        {
            return new PluginInstallResult { Error = ex.Message };
        }

        try
        {
            var manifest = PluginPackages.ReadManifest(stage, out var manifestError);
            if (manifest == null) return new PluginInstallResult { Error = manifestError };

            var errors = manifest.Validate(HostVersion, forLocalImport: source == "local");
            if (errors.Count > 0)
                return new PluginInstallResult { Error = string.Join("; ", errors) };

            if (expectedId != null
                && !string.Equals(expectedId, manifest.Id, StringComparison.OrdinalIgnoreCase))
                return new PluginInstallResult { Error = "plugin id does not match the registry entry" };
            if (expectedVersion != null
                && !string.Equals(expectedVersion, manifest.Version, StringComparison.OrdinalIgnoreCase))
                return new PluginInstallResult { Error = "plugin version does not match the registry entry" };
            if (expectedKind is { } declaredKind && manifest.KindValue != declaredKind)
                return new PluginInstallResult
                {
                    Error = "plugin kind does not match the registry entry "
                            + $"(registry: {declaredKind.ToId()}, package: {manifest.KindValue.ToId()})"
                };

            var kind = manifest.KindValue;
            if (kind is not (PluginKind.Widget or PluginKind.ZoneBar or PluginKind.Tool or PluginKind.Language or PluginKind.Appearance))
                return new PluginInstallResult
                {
                    Error = "this build supports widgets, zone bays, tools, language packs and appearance packs; data sources arrive in the next release"
                };

            // 禁用名单以「插件 id + 版本」为准，本地导入也拦：被下架的版本不该从任何路径装上。
            var blocked = PluginBlocklist.Find(manifest.Id, manifest.Version);
            if (blocked != null)
                return new PluginInstallResult
                {
                    Error = "this version is on the blocked list: "
                            + PluginBlocklist.ReasonOf(blocked, LocalizationService.Instance.CurrentLanguage)
                };

            // 权限不得超出市场条目的声明：更新弹窗的「新增联网权限要重新确认」全都基于
            // registry 里的 permissions，而真正执行的是包内 plugin.json 的 permissions。
            // 两者不一致时必须拒绝——否则作者只要在包里偷偷加 network 就能绕过确认。
            // registry 没写 permissions 时按空集处理：以前整体跳过，等于让「没声明条目」
            // 上的任何权限都自动获批。
            if (source == "market")
            {
                var declared = NormalizePermissions(declaredPermissions);
                var actual = NormalizePermissions(manifest.Permissions);
                var undeclared = actual.Except(declared, StringComparer.Ordinal).ToList();
                if (undeclared.Count > 0)
                    return new PluginInstallResult
                    {
                        Error = "the package requests permissions the marketplace listing does not declare: "
                                + string.Join(", ", undeclared)
                    };
            }

            // 签名策略：官方代码类必须验签通过；任何包只要验签失败（篡改）就拒装。
            var reject = PluginTrust.Reject(kind, official, trust);
            if (reject != null) return new PluginInstallResult { Error = reject };

            if (kind == PluginKind.Language)
            {
                // 声明式包：校验 payload（key / 占位符 / 长度 / 覆盖率），失败直接拒装。
                var validation = LanguagePackService.Instance.Validate(stage, manifest);
                if (!validation.Ok)
                    return new PluginInstallResult
                    {
                        Error = string.Join("; ", System.Linq.Enumerable.Take(validation.Errors, 5))
                    };
                if (Math.Abs(validation.Coverage - (manifest.Language?.Coverage ?? 0)) > 0.02)
                {
                    DzTrace.Always($"[Plugin] {manifest.Id} declares coverage {manifest.Language?.Coverage:0.00} " +
                                   $"but actually covers {validation.Coverage:0.00}");
                }
            }
            else if (kind == PluginKind.Appearance)
            {
                // 材质 schema + 预设文件类型/可解析性，全部在落盘前校验。
                var validation = AppearancePackService.Instance.Validate(stage, manifest);
                if (!validation.Ok)
                    return new PluginInstallResult
                    {
                        Error = string.Join("; ", System.Linq.Enumerable.Take(validation.Errors, 5))
                    };
            }

            // 落盘前把图标/预览也验一遍：manifest 只保证路径在包内，不保证文件存在、是图片、
            // 或者不是一张 1024x1024 的噪声图。失败信息必须点名是 icon 还是第几张 preview。
            var artworkError = ValidateArtwork(stage, manifest);
            if (artworkError != null) return new PluginInstallResult { Error = artworkError };

            if (manifest.Entry != null)
            {
                var inspection = PluginAssemblyInspector.Inspect(stage, manifest.Entry.Assembly, manifest.Entry.Type);
                if (!inspection.Ok)
                    return new PluginInstallResult { Error = string.Join("; ", inspection.Errors) };
            }

            var finalDir = PluginPaths.VersionDir(manifest.Id, manifest.Version);
            Directory.CreateDirectory(PluginPaths.PluginDir(manifest.Id));
            if (Directory.Exists(finalDir))
            {
                try { Directory.Delete(finalDir, recursive: true); }
                catch (Exception ex)
                {
                    return new PluginInstallResult { Error = "cannot replace the installed version: " + ex.Message };
                }
            }

            Directory.Move(stage, finalDir);
            stage = ""; // moved

            if (kind == PluginKind.Appearance)
            {
                // 预设是「复制进用户预设目录」的，落盘之后才能导入；失败回滚整个安装。
                if (!AppearancePackService.Instance.ImportPresets(finalDir, manifest, out var presetError))
                {
                    try { Directory.Delete(finalDir, recursive: true); } catch { }
                    return new PluginInstallResult { Error = presetError ?? "cannot import presets" };
                }
            }

            var sha = expectedSha ?? PluginPackages.Sha256OfFile(packagePath);
            var info = new PluginInstallInfo
            {
                Id = manifest.Id,
                Version = manifest.Version,
                Kind = kind.ToId(),
                Source = source,
                Official = official,
                Sha256 = sha,
                InstalledAtUtc = DateTime.UtcNow
            };
            PluginPackages.WriteInstallInfo(PluginPaths.PluginDir(manifest.Id), info);

            var record = new PluginRecord
            {
                Id = manifest.Id,
                Version = manifest.Version,
                Kind = kind.ToId(),
                // 声明式包安装即生效（语言包进语言列表，但不自动切换）；代码类需显式启用。
                // 更新时继承旧记录的启停状态：否则「已启用的代码插件」一更新就被静默停用，
                // 用户看到的是「更新成功、版本没变、组件全变占位」。
                Enabled = enabledOverride ?? !kind.IsCode(),
                Official = official,
                Source = source,
                Sha256 = sha,
                Signature = PluginTrust.ToRecordValue(trust),
                SignedBy = trust.KeyId,
                InstalledAtUtc = DateTime.UtcNow
            };
            UpsertRecord(record);

            // 更新：新版本落盘后清掉旧版本目录（DLL 被占用时登记到下次启动删）。
            RemoveObsoleteVersions(manifest.Id, manifest.Version);

            DzTrace.Always($"[Plugin] installed {manifest.Id} {manifest.Version} ({kind}) source={source} " +
                           $"trust={PluginTrust.ToRecordValue(trust)}");

            return new PluginInstallResult
            {
                Success = true,
                Manifest = manifest,
                Record = record,
                InstallDir = finalDir,
                Sha256 = sha
            };
        }
        catch (Exception ex)
        {
            return new PluginInstallResult { Error = ex.Message };
        }
        finally
        {
            if (!string.IsNullOrEmpty(stage) && Directory.Exists(stage))
            {
                try { Directory.Delete(stage, recursive: true); } catch { }
            }
        }
    }

    /// <summary>插件 id 只能是一段安全文件名（manifest 的 id 正则不允许分隔符，但配置可能被改）。</summary>
    static bool IsSafePluginId(string? pluginId) => PluginPaths.IsSafePluginId(pluginId);

    /// <summary>权限名归一化：大小写/空白不同不该被当成两种权限。</summary>
    static HashSet<string> NormalizePermissions(IEnumerable<string>? permissions)
    {
        var set = new HashSet<string>(StringComparer.Ordinal);
        if (permissions == null) return set;
        foreach (var permission in permissions)
        {
            var normalized = permission?.Trim().ToLowerInvariant();
            if (!string.IsNullOrEmpty(normalized)) set.Add(normalized);
        }
        return set;
    }

    /// <summary>
    /// 图标与预览图的落盘前校验：存在 / 大小 / 可解码 / 尺寸。返回错误文案；null = 通过。
    /// manifest.Validate 只保证路径安全，没有任何一条能挡住「icon.png 其实是 100MB 的文本」。
    /// </summary>
    static string? ValidateArtwork(string stage, PluginManifest manifest)
    {
        var iconPath = PluginPaths.ResolveInside(stage, manifest.Icon);
        if (iconPath == null || !File.Exists(iconPath))
            return "icon file is missing: " + manifest.Icon;
        long iconSize;
        try { iconSize = new FileInfo(iconPath).Length; }
        catch (Exception ex) { return "cannot read the icon file: " + ex.Message; }
        if (iconSize > MaxIconBytes)
            return $"icon is larger than 256 KB ({iconSize} bytes): {manifest.Icon}";
        if (!TryReadImageSize(iconPath, out var iconWidth, out var iconHeight))
            return "icon cannot be decoded as an image: " + manifest.Icon;
        if (iconWidth < MinIconSide || iconHeight < MinIconSide
            || iconWidth > MaxIconSide || iconHeight > MaxIconSide)
            return $"icon must be between {MinIconSide} and {MaxIconSide} pixels wide and high " +
                   $"({iconWidth}x{iconHeight}): {manifest.Icon}";

        var previews = manifest.Previews;
        if (previews == null) return null;
        if (previews.Count > MaxPreviews)
            return $"at most {MaxPreviews} preview images are allowed (found {previews.Count})";
        for (var index = 0; index < previews.Count; index++)
        {
            var relative = previews[index];
            var number = index + 1;
            var previewPath = PluginPaths.ResolveInside(stage, relative);
            if (previewPath == null || !File.Exists(previewPath))
                return $"preview image #{number} is missing: {relative}";
            long previewSize;
            try { previewSize = new FileInfo(previewPath).Length; }
            catch (Exception ex) { return $"cannot read preview image #{number}: " + ex.Message; }
            if (previewSize > MaxPreviewBytes)
                return $"preview image #{number} is larger than 1.5 MB ({previewSize} bytes): {relative}";
            if (!TryReadImageSize(previewPath, out _, out _))
                return $"preview image #{number} cannot be decoded as an image: {relative}";
        }
        return null;
    }

    /// <summary>只读宽高，不解码整张图到内存（BitmapDecoder 用 WIC 的头部信息）。</summary>
    static bool TryReadImageSize(string path, out int width, out int height)
    {
        if (TryReadImageSizeOnThisThread(path, out width, out height)) return true;

        // 安装走 Task.Run（MTA 线程），而 WPF 的图片解码底层是 WIC/COM：某些机器上在 MTA
        // 线程直接解码会抛 InvalidOperationException（COM 未初始化）而不是「图片坏了」。
        // 那种情况下回 UI 线程（STA，自测台与真实应用都有 Dispatcher）再解一次；
        // 真正损坏的图片两次都会失败，所以不会放过坏图。
        var dispatcher = System.Windows.Application.Current?.Dispatcher;
        if (dispatcher == null || dispatcher.CheckAccess()) return false;
        try
        {
            var size = dispatcher.Invoke(() =>
                TryReadImageSizeOnThisThread(path, out var w, out var h) ? (w, h) : (0, 0));
            width = size.Item1;
            height = size.Item2;
            return width > 0 && height > 0;
        }
        catch
        {
            width = height = 0;
            return false;
        }
    }

    static bool TryReadImageSizeOnThisThread(string path, out int width, out int height)
    {
        width = height = 0;
        try
        {
            using var stream = File.OpenRead(path);
            var decoder = BitmapDecoder.Create(stream, BitmapCreateOptions.None, BitmapCacheOption.OnLoad);
            if (decoder.Frames.Count == 0) return false;
            var frame = decoder.Frames[0];
            width = frame.PixelWidth;
            height = frame.PixelHeight;
            return width > 0 && height > 0;
        }
        catch
        {
            return false;
        }
    }

    void UpsertRecord(PluginRecord record)
    {
        ConfigSaver.SavePreservingPanelSettings(_configService, cfg =>
        {
            cfg.Plugins.RemoveAll(r => string.Equals(r.Id, record.Id, StringComparison.OrdinalIgnoreCase));
            cfg.Plugins.Add(record);
        });
    }

    /// <summary>更新后清掉旧版本目录；DLL 被占用时登记到下次启动删。</summary>
    static void RemoveObsoleteVersions(string pluginId, string keepVersion)
    {
        try
        {
            var dir = PluginPaths.PluginDir(pluginId);
            if (!Directory.Exists(dir)) return;
            foreach (var child in Directory.GetDirectories(dir))
            {
                if (string.Equals(Path.GetFileName(child), keepVersion, StringComparison.OrdinalIgnoreCase)) continue;
                try { Directory.Delete(child, recursive: true); }
                catch { RegisterPendingDelete(child); }
            }
        }
        catch (Exception ex)
        {
            DzTrace.Always("[Plugin] cannot clean up old versions: " + ex.Message);
        }
    }

    /// <summary>卸载：删目录（被占用则登记下次启动删），移除记录；组件保留占位。</summary>
    /// <param name="kindHint">
    /// 调用方已知的类型。install.json 缺失/损坏时仍能按记录里的 Kind 清理外观包预设，
    /// 不会留下不可见但占位的 <c>Presets\*</c> 文件。
    /// </param>
    public bool Uninstall(string pluginId, bool deleteData, out string? error, string? kindHint = null)
    {
        error = null;
        // 递归删除前必须确认 id 是单个安全的目录名：id 若来自被改过的配置（如 ".." 或
        // 绝对路径），Path.Combine 会直接返回它，等于删任意目录。
        if (!IsSafePluginId(pluginId))
        {
            error = "invalid plugin id";
            return false;
        }
        // 外观包导入过用户预设目录：先按 SourcePackId 清掉，再删包目录。
        var installInfo = PluginPackages.ReadInstallInfo(PluginPaths.PluginDir(pluginId));
        var kind = installInfo?.Kind ?? kindHint;
        if (string.Equals(kind, "appearance", StringComparison.OrdinalIgnoreCase))
        {
            try { AppearancePackService.Instance.OnUninstalled(pluginId); }
            catch (Exception ex) { DzTrace.Always("[Plugin] appearance cleanup: " + ex.Message); }
        }
        try
        {
            if (Directory.Exists(PluginPaths.PluginDir(pluginId)))
                Directory.Delete(PluginPaths.PluginDir(pluginId), recursive: true);
        }
        catch (Exception ex)
        {
            error = ex.Message;
            RegisterPendingDelete(PluginPaths.PluginDir(pluginId));
        }

        if (deleteData)
        {
            try
            {
                if (Directory.Exists(PluginPaths.PluginDataDir(pluginId)))
                    Directory.Delete(PluginPaths.PluginDataDir(pluginId), recursive: true);
            }
            catch { }
        }

        ConfigSaver.SavePreservingPanelSettings(_configService, cfg =>
            cfg.Plugins.RemoveAll(r => string.Equals(r.Id, pluginId, StringComparison.OrdinalIgnoreCase)));

        try
        {
            File.Delete(PluginPaths.LoadingMarker(pluginId));
            File.Delete(PluginPaths.CrashCountPath(pluginId));
        }
        catch { }

        DzTrace.Always($"[Plugin] uninstalled {pluginId} (deleteData={deleteData})");
        // 目录删不掉时不能报成功：上层会据此移除记录，留下删不掉的孤儿目录。
        return error == null;
    }

    static readonly object PendingDeleteGate = new();

    public static void RegisterPendingDelete(string path)
    {
        try
        {
            lock (PendingDeleteGate)
            {
                PluginPaths.EnsureCreated();
                var pathFile = PluginPaths.PendingDeletePath;
                var list = new System.Collections.Generic.List<string>();
                if (File.Exists(pathFile))
                {
                    try
                    {
                        list = System.Text.Json.JsonSerializer.Deserialize<System.Collections.Generic.List<string>>(
                            File.ReadAllText(pathFile)) ?? new();
                    }
                    catch { }
                }
                if (!list.Contains(path, StringComparer.OrdinalIgnoreCase)) list.Add(path);
                File.WriteAllText(pathFile, System.Text.Json.JsonSerializer.Serialize(list));
            }
        }
        catch { }
    }

    /// <summary>启动时清理上次没删掉的目录（DLL 被锁等）。</summary>
    public static void ProcessPendingDeletes()
    {
        try
        {
            var path = PluginPaths.PendingDeletePath;
            if (!File.Exists(path)) return;
            var list = System.Text.Json.JsonSerializer.Deserialize<System.Collections.Generic.List<string>>(
                File.ReadAllText(path)) ?? new();
            var remaining = new System.Collections.Generic.List<string>();
            var root = System.IO.Path.GetFullPath(PluginPaths.Root).TrimEnd(System.IO.Path.DirectorySeparatorChar)
                       + System.IO.Path.DirectorySeparatorChar;
            foreach (var dir in list)
            {
                // 只清理 Plugins 根目录下的路径，避免 pending-delete 被篡改后删到别处。
                string full;
                try { full = System.IO.Path.GetFullPath(dir); } catch { continue; }
                if (!full.StartsWith(root, StringComparison.OrdinalIgnoreCase)) continue;
                try
                {
                    if (Directory.Exists(full)) Directory.Delete(full, recursive: true);
                }
                catch
                {
                    remaining.Add(dir);
                }
            }
            if (remaining.Count == 0) File.Delete(path);
            else File.WriteAllText(path, System.Text.Json.JsonSerializer.Serialize(remaining));
        }
        catch { }
    }
}
