using Aphelion.Core.Settings;

namespace Aphelion.Core.Net;

/// <summary>
/// Maps official URLs to the BMCLAPI mirror (bmclapi2.bangbang93.com), which serves the same files
/// byte-for-byte. Every file is still verified against the SHA-1 published by the official metadata.
/// </summary>
public sealed class DownloadSources
{
    public const string MirrorBase = "https://bmclapi2.bangbang93.com";

    private static readonly (string Official, string Mirror)[] Rules =
    [
        ("https://piston-meta.mojang.com", MirrorBase),
        ("https://piston-data.mojang.com", MirrorBase),
        ("https://launchermeta.mojang.com", MirrorBase),
        ("https://launcher.mojang.com", MirrorBase),
        ("https://resources.download.minecraft.net", MirrorBase + "/assets"),
        ("https://libraries.minecraft.net", MirrorBase + "/maven"),
        ("https://maven.minecraftforge.net", MirrorBase + "/maven"),
        ("https://files.minecraftforge.net/maven", MirrorBase + "/maven"),
        ("https://maven.neoforged.net/releases", MirrorBase + "/maven"),
        ("https://maven.fabricmc.net", MirrorBase + "/maven"),
        ("https://meta.fabricmc.net", MirrorBase + "/fabric-meta"),
        ("https://maven.quiltmc.org/repository/release", MirrorBase + "/maven"),
        ("https://meta.quiltmc.org", MirrorBase + "/quilt-meta"),
        ("https://authlib-injector.yushi.moe", MirrorBase + "/mirrors/authlib-injector"),
    ];

    public DownloadSourcePreference Preference { get; set; }

    /// <summary>Set by the auto mode after the official source proved unreachable in this session.</summary>
    public bool OfficialUnreachable { get; set; }

    public DownloadSources(DownloadSourcePreference preference) => Preference = preference;

    public static string? ToMirror(string url)
    {
        foreach (var (official, mirror) in Rules)
        {
            if (url.StartsWith(official, StringComparison.OrdinalIgnoreCase))
                return mirror + url[official.Length..];
        }
        return null;
    }

    /// <summary>Candidate URLs in the order they should be tried.</summary>
    public IReadOnlyList<string> Candidates(string url)
    {
        var mirror = ToMirror(url);
        if (mirror is null) return [url];
        return Preference switch
        {
            DownloadSourcePreference.Official => [url, mirror],
            DownloadSourcePreference.Mirror => [mirror, url],
            _ => OfficialUnreachable ? [mirror, url] : [url, mirror],
        };
    }

    public string Describe() => Preference switch
    {
        DownloadSourcePreference.Official => "official",
        DownloadSourcePreference.Mirror => "mirror",
        _ => OfficialUnreachable ? "auto:mirror" : "auto:official",
    };
}
