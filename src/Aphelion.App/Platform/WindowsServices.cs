using System.Diagnostics;
using System.Security.Cryptography;
using Aphelion.Core.Accounts;
using Aphelion.Core.Logging;
using Microsoft.VisualBasic.FileIO;

namespace Aphelion.App.Platform;

/// <summary>DPAPI, current-user scope: secrets are readable only by this Windows account on this machine.</summary>
internal sealed class DpapiProtector : ISecretProtector
{
    private static readonly byte[] Entropy = "Aphelion.AccountSecret.v1"u8.ToArray();

    public byte[] Protect(byte[] plain) => ProtectedData.Protect(plain, Entropy, DataProtectionScope.CurrentUser);
    public byte[] Unprotect(byte[] protectedData) => ProtectedData.Unprotect(protectedData, Entropy, DataProtectionScope.CurrentUser);
}

internal static class Shell
{
    /// <summary>Opens a folder in Explorer, creating it first so the action never fails silently.</summary>
    public static void OpenFolder(string path)
    {
        Directory.CreateDirectory(path);
        Process.Start(new ProcessStartInfo("explorer.exe") { ArgumentList = { path }, UseShellExecute = false });
    }

    /// <summary>Selects a file in Explorer.</summary>
    public static void RevealFile(string path)
    {
        if (!File.Exists(path)) { OpenFolder(Path.GetDirectoryName(path)!); return; }
        Process.Start(new ProcessStartInfo("explorer.exe") { ArgumentList = { "/select,", path }, UseShellExecute = false });
    }

    /// <summary>Opens http(s) links in the default browser; anything else is refused.</summary>
    public static void OpenUrl(string url)
    {
        if (!Uri.TryCreate(url, UriKind.Absolute, out var uri) || (uri.Scheme != Uri.UriSchemeHttps && uri.Scheme != Uri.UriSchemeHttp))
            throw new ArgumentException("Only web links can be opened.", nameof(url));
        Process.Start(new ProcessStartInfo(uri.AbsoluteUri) { UseShellExecute = true });
    }

    /// <summary>Moves a file or folder to the Recycle Bin so deletions made from the launcher stay recoverable.</summary>
    public static void Recycle(string path)
    {
        if (File.Exists(path))
            FileSystem.DeleteFile(path, UIOption.OnlyErrorDialogs, RecycleOption.SendToRecycleBin);
        else if (Directory.Exists(path))
            FileSystem.DeleteDirectory(path, UIOption.OnlyErrorDialogs, RecycleOption.SendToRecycleBin);
        Log.Info($"Recycled {path}");
    }
}
