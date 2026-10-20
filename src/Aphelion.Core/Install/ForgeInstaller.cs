using System.Diagnostics;
using System.IO.Compression;
using System.Text;
using System.Text.Json;
using System.Text.RegularExpressions;
using Aphelion.Core.Logging;
using Aphelion.Core.Minecraft;
using Aphelion.Core.Net;
using Aphelion.Core.Storage;

namespace Aphelion.Core.Install;

/// <summary>Returns the path of a java executable whose major version suits <paramref name="majorVersion"/>.</summary>
public delegate Task<string> JavaResolver(int majorVersion, CancellationToken ct);

/// <summary>
/// Installs Forge or NeoForge from the official installer jar without running its GUI:
/// the modern format (1.13+) runs the declared client processors; the legacy format (≤1.12)
/// extracts the universal jar. The returned version JSON still inherits from the vanilla release.
/// </summary>
public sealed partial class ForgeInstaller(GameRoot root, Downloader downloader, JavaResolver java, string tempDirectory)
{
    public const string MavenCentral = "https://repo1.maven.org/maven2/";

    public async Task<VersionJson> InstallAsync(string installerUrl, string gameVersion, string clientJar, int javaMajor,
        IProgress<StageProgress>? progress, CancellationToken ct)
    {
        var work = Path.Combine(tempDirectory, "forge-" + Guid.NewGuid().ToString("N")[..10]);
        Directory.CreateDirectory(work);
        try
        {
            progress?.Report(new StageProgress("forge.installer"));
            var installer = Path.Combine(work, "installer.jar");
            await downloader.RunAsync([new DownloadItem(installerUrl, installer)], progress.ForDownloads("forge.installer"), ct).ConfigureAwait(false);

            using var zip = ZipFile.OpenRead(installer);
            var profileEntry = zip.GetEntry("install_profile.json") ?? throw AppException.Of("forge.badInstaller", "install_profile.json missing from installer");
            using var profileDoc = await JsonDocument.ParseAsync(profileEntry.Open(), cancellationToken: ct).ConfigureAwait(false);
            var profile = profileDoc.RootElement;

            return profile.TryGetProperty("versionInfo", out _)
                ? await InstallLegacyAsync(zip, profile, progress, ct).ConfigureAwait(false)
                : await InstallModernAsync(zip, profile, installer, work, gameVersion, clientJar, javaMajor, progress, ct).ConfigureAwait(false);
        }
        finally
        {
            try { Directory.Delete(work, recursive: true); } catch (IOException) { } catch (UnauthorizedAccessException) { }
        }
    }

    private async Task<VersionJson> InstallLegacyAsync(ZipArchive zip, JsonElement profile, IProgress<StageProgress>? progress, CancellationToken ct)
    {
        var version = profile.GetProperty("versionInfo").Deserialize<VersionJson>(Json.External)
                      ?? throw AppException.Of("forge.badInstaller", "versionInfo is empty");
        var install = profile.GetProperty("install");
        var universalCoord = MavenCoordinate.Parse(install.GetProperty("path").GetString()!);
        var universalEntry = zip.GetEntry(install.GetProperty("filePath").GetString()!)
                             ?? throw AppException.Of("forge.badInstaller", "universal jar missing from installer");
        var universalPath = root.LibraryPath(universalCoord.RelativePath);
        ExtractEntry(universalEntry, universalPath);

        var items = new List<DownloadItem>();
        foreach (var lib in version.Libraries)
        {
            if (lib.Name == universalCoord.ToString() || !MavenCoordinate.TryParse(lib.Name, out var c)) continue;
            if (lib.Clientreq == false) continue;
            var resolved = LibraryResolver.Resolve(lib, root, RuntimePlatform.Current);
            if (resolved?.Artifact is not { } a || a.Url is null) continue;
            items.Add(new DownloadItem(a.Url, a.Path, a.Sha1, a.Size) { Fallbacks = [LibraryResolver.AppendPath(MavenCentral, c.RelativePath)] });
            // Legacy Forge declares checksums for some artifacts but serves them without size; never trust a stale file blindly.
        }
        progress?.Report(new StageProgress("forge.libraries"));
        await downloader.RunAsync(items, progress.ForDownloads("forge.libraries"), ct).ConfigureAwait(false);
        foreach (var lib in version.Libraries) lib.Clientreq = null; // installed; keep the launch-time resolver simple
        return version;
    }

    private async Task<VersionJson> InstallModernAsync(ZipArchive zip, JsonElement profile, string installerPath, string work, string gameVersion,
        string clientJar, int javaMajor, IProgress<StageProgress>? progress, CancellationToken ct)
    {
        var jsonPath = profile.TryGetProperty("json", out var jp) ? jp.GetString()!.TrimStart('/') : "version.json";
        var versionEntry = zip.GetEntry(jsonPath) ?? throw AppException.Of("forge.badInstaller", "version.json missing from installer");
        VersionJson version;
        using (var s = versionEntry.Open())
            version = await JsonSerializer.DeserializeAsync<VersionJson>(s, Json.External, ct).ConfigureAwait(false)
                      ?? throw AppException.Of("forge.badInstaller", "version.json is empty");

        var profileLibraries = profile.TryGetProperty("libraries", out var pl) ? pl.Deserialize<List<Library>>(Json.External) ?? [] : [];

        // 1. Libraries: download from their URL, or take the copy embedded under maven/ in the installer.
        var downloads = new List<DownloadItem>();
        foreach (var lib in profileLibraries.Concat(version.Libraries))
        {
            var resolved = LibraryResolver.Resolve(lib, root, RuntimePlatform.Current);
            if (resolved?.Artifact is not { } a) continue;
            var embedded = resolved.Coordinate is { } c ? zip.GetEntry("maven/" + c.RelativePath) : null;
            if (embedded is not null)
            {
                if (!File.Exists(a.Path) || (a.Sha1 is not null && !string.Equals(await Hashing.Sha1FileAsync(a.Path, ct).ConfigureAwait(false), a.Sha1, StringComparison.OrdinalIgnoreCase)))
                    ExtractEntry(embedded, a.Path);
            }
            else if (a.Url is not null)
            {
                downloads.Add(new DownloadItem(a.Url, a.Path, a.Sha1, a.Size));
            }
        }
        progress?.Report(new StageProgress("forge.libraries"));
        await downloader.RunAsync(downloads, progress.ForDownloads("forge.libraries"), ct).ConfigureAwait(false);

        // 2. Data map for processor arguments.
        var data = new Dictionary<string, string>(StringComparer.Ordinal)
        {
            ["SIDE"] = "client",
            ["MINECRAFT_JAR"] = clientJar,
            ["MINECRAFT_VERSION"] = gameVersion,
            ["ROOT"] = root.Path,
            ["INSTALLER"] = installerPath,
            ["LIBRARY_DIR"] = root.Libraries,
        };
        if (profile.TryGetProperty("data", out var dataNode))
        {
            foreach (var entry in dataNode.EnumerateObject())
            {
                if (!entry.Value.TryGetProperty("client", out var clientValue)) continue;
                data[entry.Name] = ResolveDataValue(clientValue.GetString() ?? "", zip, work);
            }
        }

        // 3. Processors (client side only).
        var processors = profile.TryGetProperty("processors", out var procNode) ? procNode.EnumerateArray().ToList() : [];
        var javaPath = processors.Count > 0 ? await java(javaMajor, ct).ConfigureAwait(false) : "";
        for (var i = 0; i < processors.Count; i++)
        {
            ct.ThrowIfCancellationRequested();
            var proc = processors[i];
            if (proc.TryGetProperty("sides", out var sides) && !sides.EnumerateArray().Any(s => s.GetString() == "client")) continue;
            progress?.Report(new StageProgress("forge.processors", (double)i / processors.Count, $"{i + 1}/{processors.Count}"));
            await RunProcessorAsync(proc, data, javaPath, work, ct).ConfigureAwait(false);
        }
        progress?.Report(new StageProgress("forge.processors", 1));
        return version;
    }

    private async Task RunProcessorAsync(JsonElement proc, Dictionary<string, string> data, string javaPath, string work, CancellationToken ct)
    {
        var jar = LibraryPathOf(proc.GetProperty("jar").GetString()!);
        var classpath = new List<string> { jar };
        if (proc.TryGetProperty("classpath", out var cp))
            classpath.AddRange(cp.EnumerateArray().Select(e => LibraryPathOf(e.GetString()!)));
        var args = proc.TryGetProperty("args", out var a) ? a.EnumerateArray().Select(e => Substitute(e.GetString() ?? "", data)).ToList() : [];

        var outputs = new Dictionary<string, string>();
        if (proc.TryGetProperty("outputs", out var outNode))
            foreach (var o in outNode.EnumerateObject())
                outputs[Substitute(o.Name, data)] = Substitute(o.Value.GetString() ?? "", data);

        if (outputs.Count > 0 && await OutputsValidAsync(outputs, ct).ConfigureAwait(false))
        {
            Log.Info($"Forge processor {Path.GetFileName(jar)} skipped: outputs up to date");
            return;
        }

        var mainClass = ReadMainClass(jar) ?? throw AppException.Of("forge.processor", $"No Main-Class in {Path.GetFileName(jar)}", ("jar", Path.GetFileName(jar)));
        var psi = new ProcessStartInfo(javaPath)
        {
            WorkingDirectory = work,
            UseShellExecute = false,
            CreateNoWindow = true,
            RedirectStandardOutput = true,
            RedirectStandardError = true,
            StandardOutputEncoding = Encoding.UTF8,
            StandardErrorEncoding = Encoding.UTF8,
        };
        psi.ArgumentList.Add("-cp");
        psi.ArgumentList.Add(string.Join(RuntimePlatform.Current.ClasspathSeparator, classpath));
        psi.ArgumentList.Add(mainClass);
        foreach (var arg in args) psi.ArgumentList.Add(arg);

        Log.Info($"Running Forge processor {mainClass}");
        using var process = Process.Start(psi) ?? throw new InvalidOperationException("Failed to start java");
        var tail = new Queue<string>();
        void Capture(string? line)
        {
            if (line is null) return;
            lock (tail)
            {
                tail.Enqueue(line);
                if (tail.Count > 40) tail.Dequeue();
            }
        }
        process.OutputDataReceived += (_, e) => Capture(e.Data);
        process.ErrorDataReceived += (_, e) => Capture(e.Data);
        process.BeginOutputReadLine();
        process.BeginErrorReadLine();

        using var timeout = CancellationTokenSource.CreateLinkedTokenSource(ct);
        timeout.CancelAfter(TimeSpan.FromMinutes(15));
        try
        {
            await process.WaitForExitAsync(timeout.Token).ConfigureAwait(false);
        }
        catch (OperationCanceledException)
        {
            try { process.Kill(entireProcessTree: true); } catch (InvalidOperationException) { }
            throw;
        }

        if (process.ExitCode != 0)
        {
            string output;
            lock (tail) output = string.Join('\n', tail);
            Log.Error($"Forge processor {mainClass} failed ({process.ExitCode}):\n{output}");
            throw AppException.Of("forge.processor", $"Forge processor {mainClass} exited with {process.ExitCode}", ("main", mainClass), ("code", process.ExitCode));
        }
        if (outputs.Count > 0 && !await OutputsValidAsync(outputs, ct).ConfigureAwait(false))
            throw AppException.Of("forge.processorOutput", $"Forge processor {mainClass} produced unexpected output", ("main", mainClass));
    }

    private static async Task<bool> OutputsValidAsync(Dictionary<string, string> outputs, CancellationToken ct)
    {
        foreach (var (file, sha1) in outputs)
        {
            if (!File.Exists(file)) return false;
            if (!string.Equals(await Hashing.Sha1FileAsync(file, ct).ConfigureAwait(false), sha1.Trim('\''), StringComparison.OrdinalIgnoreCase)) return false;
        }
        return true;
    }

    private string ResolveDataValue(string value, ZipArchive zip, string work)
    {
        if (value.Length >= 2 && value[0] == '[' && value[^1] == ']') return LibraryPathOf(value[1..^1]);
        if (value.Length >= 2 && value[0] == '\'' && value[^1] == '\'') return value[1..^1];
        if (value.StartsWith('/'))
        {
            var entry = zip.GetEntry(value.TrimStart('/')) ?? throw AppException.Of("forge.badInstaller", $"Installer entry {value} missing");
            var target = SafePath.Combine(Path.Combine(work, "data"), value.TrimStart('/'));
            ExtractEntry(entry, target);
            return target;
        }
        return value;
    }

    private string Substitute(string arg, Dictionary<string, string> data)
    {
        if (arg.Length >= 2 && arg[0] == '[' && arg[^1] == ']') return LibraryPathOf(arg[1..^1]);
        return TokenPattern().Replace(arg, m => data.TryGetValue(m.Groups[1].Value, out var v) ? v : m.Value);
    }

    private string LibraryPathOf(string coordinate) => root.LibraryPath(MavenCoordinate.Parse(coordinate).RelativePath);

    internal static string? ReadMainClass(string jar)
    {
        using var zip = ZipFile.OpenRead(jar);
        var manifest = zip.GetEntry("META-INF/MANIFEST.MF");
        if (manifest is null) return null;
        using var reader = new StreamReader(manifest.Open());
        while (reader.ReadLine() is { } line)
        {
            if (line.StartsWith("Main-Class:", StringComparison.OrdinalIgnoreCase)) return line["Main-Class:".Length..].Trim();
        }
        return null;
    }

    private static void ExtractEntry(ZipArchiveEntry entry, string destination)
    {
        Directory.CreateDirectory(Path.GetDirectoryName(destination)!);
        var temp = destination + ".part";
        entry.ExtractToFile(temp, overwrite: true);
        File.Move(temp, destination, overwrite: true);
    }

    [GeneratedRegex(@"\{([A-Z0-9_]+)\}")]
    private static partial Regex TokenPattern();
}
