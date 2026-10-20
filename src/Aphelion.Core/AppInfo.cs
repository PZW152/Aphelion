using System.Reflection;

namespace Aphelion.Core;

/// <summary>Stable product identity. These values must not change between releases.</summary>
public static class AppInfo
{
    public const string Name = "Aphelion";

    /// <summary>Permanent product identifier (uninstall key, mutex names, install manifest).</summary>
    public const string AppId = "6E1C2F0B-4A7D-4C55-9C6B-AF3E1D0A9F21";

    public static string Version { get; } =
        typeof(AppInfo).Assembly.GetCustomAttribute<AssemblyInformationalVersionAttribute>()?.InformationalVersion.Split('+')[0]
        ?? typeof(AppInfo).Assembly.GetName().Version?.ToString(3)
        ?? "0.0.0";

    /// <summary>User agent sent with every request (Modrinth requires an identifying UA).</summary>
    public static string UserAgent => $"{Name}/{Version} (desktop launcher)";
}
