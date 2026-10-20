using System.Runtime.InteropServices;
using System.Text.Json;
using Aphelion.Core.Install;
using Aphelion.Core.Logging;
using Aphelion.Core.Minecraft;
using Aphelion.Core.Net;
using Aphelion.Core.Storage;

namespace Aphelion.Core.Java;

/// <summary>Owns the list of known Java installations, picks one per game version, and installs Mojang runtimes.</summary>
public sealed class JavaManager(string runtimeDirectory, MojangMeta meta, Func<Downloader> downloaderFactory, Func<IEnumerable<string>> userPaths) : IDisposable
{
    public void Dispose()
    {
        _scanGate.Dispose();
        _installGate.Dispose();
    }

    public const string RuntimeIndexUrl = "https://piston-meta.mojang.com/v1/products/java-runtime/2ec0cc96c44e5a76b9c8b7c39df7210883d12871/all.json";

    private readonly SemaphoreSlim _scanGate = new(1, 1);
    private readonly SemaphoreSlim _installGate = new(1, 1);
    private List<JavaInstall>? _cache;

    public async Task<IReadOnlyList<JavaInstall>> ListAsync(bool refresh = false, CancellationToken ct = default)
    {
        await _scanGate.WaitAsync(ct).ConfigureAwait(false);
        try
        {
            if (_cache is null || refresh)
            {
                _cache = await Task.Run(() => JavaDiscovery.Scan(runtimeDirectory, userPaths()), ct).ConfigureAwait(false);
                Log.Info($"Java scan: {string.Join(", ", _cache.Select(j => $"{j.Major}({j.Arch},{j.Source})"))}");
            }
            return _cache;
        }
        finally
        {
            _scanGate.Release();
        }
    }

    /// <summary>The Java major version a game version needs, from its metadata or the release date.</summary>
    public static int RequiredMajor(VersionJson json)
    {
        if (json.JavaVersion is { MajorVersion: > 0 } jv) return jv.MajorVersion;
        // Versions published before Mojang added javaVersion all run on Java 8.
        return 8;
    }

    /// <summary>
    /// Java 8 games get Java 8 only (old Forge and LaunchWrapper break on 9+). Newer games get the exact major
    /// when present, otherwise the closest newer one. 64-bit builds are preferred.
    /// </summary>
    public static JavaInstall? Pick(IEnumerable<JavaInstall> installs, int required)
    {
        return installs
            .Where(j => ArchRunsHere(j.Arch))
            .Where(j => required <= 8 ? j.Major == 8 : j.Major >= required)
            .OrderBy(j => j.Major)
            .ThenByDescending(j => j.Arch == NativeArch)
            .ThenByDescending(j => j.Is64Bit)
            .ThenBy(j => j.Source == JavaSource.Managed ? 0 : 1)
            .FirstOrDefault();
    }

    private static string NativeArch => RuntimeInformation.OSArchitecture switch
    {
        Architecture.Arm64 => "arm64",
        Architecture.X86 => "x86",
        _ => "x64",
    };

    private static bool ArchRunsHere(string arch) => arch switch
    {
        "x86" => true,
        "x64" => RuntimeInformation.OSArchitecture is Architecture.X64 or Architecture.Arm64,
        "arm64" => RuntimeInformation.OSArchitecture == Architecture.Arm64,
        _ => false,
    };

    /// <summary>Returns a suitable Java, downloading Mojang's runtime when none is installed.</summary>
    public async Task<JavaInstall> EnsureAsync(VersionJson json, IProgress<StageProgress>? progress, CancellationToken ct)
    {
        var required = RequiredMajor(json);
        var picked = Pick(await ListAsync(ct: ct).ConfigureAwait(false), required);
        if (picked is not null) return picked;
        var component = json.JavaVersion?.Component ?? ComponentFor(required);
        await InstallRuntimeAsync(component, required, progress, ct).ConfigureAwait(false);
        picked = Pick(await ListAsync(refresh: true, ct).ConfigureAwait(false), required);
        return picked ?? throw AppException.Of("java.none", $"No Java {required} is available", ("major", required));
    }

    public async Task<string> ResolveExecutableAsync(int major, CancellationToken ct)
    {
        var picked = Pick(await ListAsync(ct: ct).ConfigureAwait(false), major);
        if (picked is not null) return picked.Executable;
        await InstallRuntimeAsync(ComponentFor(major), major, null, ct).ConfigureAwait(false);
        picked = Pick(await ListAsync(refresh: true, ct).ConfigureAwait(false), major);
        return picked?.Executable ?? throw AppException.Of("java.none", $"No Java {major} is available", ("major", major));
    }

    public static string ComponentFor(int major) => major switch
    {
        <= 8 => "jre-legacy",
        16 => "java-runtime-alpha",
        17 => "java-runtime-gamma",
        21 => "java-runtime-delta",
        _ => $"java-runtime-{major}",
    };

    private static string? MojangPlatformKey() => (OperatingSystem.IsWindows(), RuntimeInformation.OSArchitecture) switch
    {
        (true, Architecture.X64) => "windows-x64",
        (true, Architecture.Arm64) => "windows-arm64",
        (true, Architecture.X86) => "windows-x86",
        _ => null,
    };

    /// <summary>Downloads a Mojang Java runtime component into runtime/&lt;component&gt; (staged, verified, then swapped in).</summary>
    public async Task<string> InstallRuntimeAsync(string component, int major, IProgress<StageProgress>? progress, CancellationToken ct)
    {
        var platform = MojangPlatformKey() ?? throw AppException.Of("java.platform", "Mojang does not publish runtimes for this platform");
        await _installGate.WaitAsync(ct).ConfigureAwait(false);
        try
        {
            progress?.Report(new StageProgress("java.index"));
            var index = await meta.FetchJsonAsync<JsonElement>(RuntimeIndexUrl, ct).ConfigureAwait(false);
            if (!index.TryGetProperty(platform, out var forPlatform))
                throw AppException.Of("java.platform", $"No runtimes for {platform}");

            JsonElement? entry = null;
            string chosen = component;
            if (forPlatform.TryGetProperty(component, out var list) && list.GetArrayLength() > 0) entry = list[0];
            else
            {
                // Unknown component name: pick any component whose version matches the required major.
                foreach (var prop in forPlatform.EnumerateObject())
                {
                    if (prop.Value.GetArrayLength() == 0) continue;
                    var name = prop.Value[0].GetProperty("version").GetProperty("name").GetString() ?? "";
                    if (JavaProbe.MajorOf(name) == major) { entry = prop.Value[0]; chosen = prop.Name; break; }
                }
            }
            if (entry is not { } e) throw AppException.Of("java.unavailable", $"Mojang publishes no Java {major} for {platform}", ("major", major));

            var manifestRef = e.GetProperty("manifest");
            var versionName = e.GetProperty("version").GetProperty("name").GetString() ?? "";
            var target = Path.Combine(runtimeDirectory, chosen);
            var marker = Path.Combine(target, ".aphelion-runtime");
            if (File.Exists(marker) && File.ReadAllText(marker).Trim() == versionName && JavaProbe.FindExecutable(target) is not null)
                return target;

            var manifestBytes = await meta.FetchBytesAsync(manifestRef.GetProperty("url").GetString()!, ct).ConfigureAwait(false);
            var expectedSha1 = manifestRef.GetProperty("sha1").GetString();
            if (expectedSha1 is not null && !string.Equals(Hashing.Sha1(manifestBytes), expectedSha1, StringComparison.OrdinalIgnoreCase))
                throw new IntegrityException("java runtime manifest");
            using var manifest = JsonDocument.Parse(manifestBytes);

            var staging = Path.Combine(runtimeDirectory, $".{chosen}.staging");
            if (Directory.Exists(staging)) Directory.Delete(staging, recursive: true);
            Directory.CreateDirectory(staging);
            var items = new List<DownloadItem>();
            foreach (var file in manifest.RootElement.GetProperty("files").EnumerateObject())
            {
                var path = SafePath.Combine(staging, file.Name);
                var type = file.Value.GetProperty("type").GetString();
                if (type == "directory") { Directory.CreateDirectory(path); continue; }
                if (type != "file") continue; // symbolic links only occur in macOS bundles
                var raw = file.Value.GetProperty("downloads").GetProperty("raw");
                items.Add(new DownloadItem(raw.GetProperty("url").GetString()!, path, raw.GetProperty("sha1").GetString(), raw.GetProperty("size").GetInt64()));
            }
            progress?.Report(new StageProgress("java.download", 0, $"Java {versionName}"));
            await downloaderFactory().RunAsync(items, progress.ForDownloads("java.download"), ct).ConfigureAwait(false);
            File.WriteAllText(Path.Combine(staging, ".aphelion-runtime"), versionName);

            var old = target + ".old";
            if (Directory.Exists(old)) Directory.Delete(old, recursive: true);
            if (Directory.Exists(target)) Directory.Move(target, old);
            Directory.Move(staging, target);
            if (Directory.Exists(old))
            {
                try { Directory.Delete(old, recursive: true); }
                catch (Exception ex) when (ex is IOException or UnauthorizedAccessException) { Log.Warn($"Old runtime {old} is in use; it will be removed later", ex); }
            }
            Log.Info($"Installed Java runtime {chosen} {versionName}");
            _cache = null;
            return target;
        }
        finally
        {
            _installGate.Release();
        }
    }

    /// <summary>Removes leftovers from interrupted runtime installs.</summary>
    public void CleanupStaging()
    {
        if (!Directory.Exists(runtimeDirectory)) return;
        foreach (var dir in Directory.EnumerateDirectories(runtimeDirectory))
        {
            var name = Path.GetFileName(dir);
            if (!(name.EndsWith(".staging", StringComparison.Ordinal) || name.EndsWith(".old", StringComparison.Ordinal))) continue;
            try { Directory.Delete(dir, recursive: true); }
            catch (Exception e) when (e is IOException or UnauthorizedAccessException) { Log.Warn($"Could not clean {dir}", e); }
        }
    }
}
