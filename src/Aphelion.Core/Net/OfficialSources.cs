namespace Aphelion.Core.Net;

/// <summary>
/// Aphelion downloads only from the publishers' own servers. Some publishers run more than one host for the same
/// files; those official alternates are tried, in order, after the URL the metadata names. Every file is still
/// verified against the hash published by the official metadata.
/// </summary>
public static class OfficialSources
{
    /// <summary>Same path, another host of the same publisher (verified to serve identical content).</summary>
    private static readonly (string Primary, string Alternate)[] Alternates =
    [
        ("https://piston-meta.mojang.com/", "https://launchermeta.mojang.com/"),
        ("https://launchermeta.mojang.com/", "https://piston-meta.mojang.com/"),
        ("https://piston-data.mojang.com/", "https://launcher.mojang.com/"),
        ("https://meta.fabricmc.net/", "https://meta2.fabricmc.net/"),
        ("https://files.minecraftforge.net/maven/", "https://maven.minecraftforge.net/"),
    ];

    /// <summary>The hosts the launcher talks to, for the connection check in Settings.</summary>
    public static readonly (string Key, string Probe)[] Hosts =
    [
        ("mojang-meta", "https://piston-meta.mojang.com/mc/game/version_manifest_v2.json"),
        ("mojang-data", "https://piston-data.mojang.com/v1/objects/30c73b1c5da787909b2f73340419fdf13b9def88/client.jar"),
        ("mojang-libraries", "https://libraries.minecraft.net/com/mojang/brigadier/1.3.10/brigadier-1.3.10.jar"),
        ("mojang-assets", "https://resources.download.minecraft.net/00/000c82756fd54e40cb236199f2b479629d0aca2f"),
        ("fabric", "https://meta.fabricmc.net/v2/versions/loader"),
        ("forge", "https://maven.minecraftforge.net/net/minecraftforge/forge/maven-metadata.xml"),
        ("neoforge", "https://maven.neoforged.net/releases/net/neoforged/neoforge/maven-metadata.xml"),
        ("quilt", "https://meta.quiltmc.org/v3/versions/loader"),
        ("modrinth", "https://api.modrinth.com/v2/tag/loader"),
    ];

    /// <summary>Candidate URLs in the order they should be tried: the named URL, then official alternates.</summary>
    public static IReadOnlyList<string> Candidates(string url)
    {
        var list = new List<string> { url };
        foreach (var (primary, alternate) in Alternates)
        {
            if (url.StartsWith(primary, StringComparison.OrdinalIgnoreCase))
                list.Add(alternate + url[primary.Length..]);
        }
        return list;
    }
}
