using System.Reflection;
using System.Security.Cryptography;
using Aphelion.Core.Logging;
using Aphelion.Core.Storage;
using Microsoft.Web.WebView2.Core;

namespace Aphelion.App.Platform;

/// <summary>
/// Keeps the published app a single file. WebView2Loader.dll is native, and Windows can only load native
/// libraries from disk, so the copy embedded in the exe is written to
/// <c>runtime/webview2-loader/&lt;sha256&gt;/</c> inside Aphelion's own data root (never %TEMP%), verified
/// against the embedded bytes before every load, and handed to WebView2 with
/// <see cref="CoreWebView2Environment.SetLoaderDllFolderPath"/>. Folders of older builds are removed.
/// </summary>
internal static class WebView2Loader
{
    private const string ResourceName = "native/WebView2Loader.dll";
    private const string FileName = "WebView2Loader.dll";

    /// <summary>Must run before the first WebView2 environment is created. Returns the folder used.</summary>
    public static string? Prepare(AppPaths paths)
    {
        using var stream = Assembly.GetExecutingAssembly().GetManifestResourceStream(ResourceName);
        if (stream is null)
        {
            // Development builds without the embedded copy load the loader that sits beside the executable.
            Log.Warn("No embedded WebView2 loader; using the one next to the executable");
            return null;
        }

        using var buffer = new MemoryStream();
        stream.CopyTo(buffer);
        var bytes = buffer.ToArray();
        var hash = Convert.ToHexStringLower(SHA256.HashData(bytes));
        var parent = Path.Combine(paths.Runtimes, "webview2-loader");
        var folder = Path.Combine(parent, hash[..16]);
        var file = Path.Combine(folder, FileName);

        // Rewrite whenever the file on disk differs from the embedded copy (damage or tampering).
        if (!File.Exists(file) || !CryptographicOperations.FixedTimeEquals(SHA256.HashData(File.ReadAllBytes(file)), Convert.FromHexString(hash)))
        {
            AtomicFile.WriteAllBytes(file, bytes, keepBackup: false);
            Log.Info($"WebView2 loader unpacked to {folder}");
        }

        RemoveOtherVersions(parent, folder);
        CoreWebView2Environment.SetLoaderDllFolderPath(folder);
        return folder;
    }

    private static void RemoveOtherVersions(string parent, string keep)
    {
        foreach (var dir in Directory.EnumerateDirectories(parent))
        {
            if (string.Equals(dir, keep, StringComparison.OrdinalIgnoreCase)) continue;
            try { Directory.Delete(dir, recursive: true); }
            catch (Exception e) when (e is IOException or UnauthorizedAccessException)
            {
                // Still loaded by another running copy of an older build; removed on a later start.
                Log.Debug($"Old WebView2 loader {dir} is in use: {e.Message}");
            }
        }
    }
}
