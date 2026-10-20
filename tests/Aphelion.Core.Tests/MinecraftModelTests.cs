using System.Runtime.InteropServices;
using System.Text.Json;
using Aphelion.Core.Minecraft;
using Aphelion.Core.Storage;

namespace Aphelion.Core.Tests;

public class RulesTests
{
    private static readonly RuntimePlatform Win64 = new("windows", "10.0.26100", Architecture.X64);
    private static readonly RuntimePlatform Linux = new("linux", "6.1", Architecture.X64);
    private static readonly RuntimePlatform Win32 = new("windows", "10.0", Architecture.X86);

    [Fact]
    public void No_rules_allow()
    {
        Assert.True(Rules.Allows(null, Win64));
        Assert.True(Rules.Allows([], Win64));
    }

    [Fact]
    public void Allow_then_disallow_osx_matches_the_launcher()
    {
        var rules = new List<Rule> { new() { Action = "allow" }, new() { Action = "disallow", Os = new OsRule { Name = "osx" } } };
        Assert.True(Rules.Allows(rules, Win64));
        Assert.False(Rules.Allows(rules, new RuntimePlatform("osx", "14", Architecture.Arm64)));
    }

    [Fact]
    public void Os_only_rule_disallows_everything_else()
    {
        var rules = new List<Rule> { new() { Action = "allow", Os = new OsRule { Name = "windows" } } };
        Assert.True(Rules.Allows(rules, Win64));
        Assert.False(Rules.Allows(rules, Linux));
    }

    [Fact]
    public void Arch_x86_means_32_bit()
    {
        var rules = new List<Rule> { new() { Action = "allow", Os = new OsRule { Arch = "x86" } } };
        Assert.True(Rules.Allows(rules, Win32));
        Assert.False(Rules.Allows(rules, Win64));
    }

    [Fact]
    public void Features_must_match_and_missing_features_are_false()
    {
        var rules = new List<Rule> { new() { Action = "allow", Features = new() { ["has_custom_resolution"] = true } } };
        Assert.False(Rules.Allows(rules, Win64));
        Assert.False(Rules.Allows(rules, Win64, new Dictionary<string, bool> { ["has_custom_resolution"] = false }));
        Assert.True(Rules.Allows(rules, Win64, new Dictionary<string, bool> { ["has_custom_resolution"] = true }));
    }

    [Fact]
    public void Os_version_regex_is_applied()
    {
        var rules = new List<Rule> { new() { Action = "allow", Os = new OsRule { Name = "windows", Version = "^10\\." } } };
        Assert.True(Rules.Allows(rules, Win64));
        Assert.False(Rules.Allows(rules, new RuntimePlatform("windows", "6.1.7601", Architecture.X64)));
    }
}

public class MavenCoordinateTests
{
    [Theory]
    [InlineData("org.ow2.asm:asm:9.6", "org/ow2/asm/asm/9.6/asm-9.6.jar")]
    [InlineData("org.lwjgl:lwjgl:3.3.3:natives-windows", "org/lwjgl/lwjgl/3.3.3/lwjgl-3.3.3-natives-windows.jar")]
    [InlineData("de.oceanlabs.mcp:mcp_config:1.20.1-20230612.114412@zip", "de/oceanlabs/mcp/mcp_config/1.20.1-20230612.114412/mcp_config-1.20.1-20230612.114412.zip")]
    [InlineData("net.minecraft:client:1.20.1-20230612.114412:mappings@txt", "net/minecraft/client/1.20.1-20230612.114412/client-1.20.1-20230612.114412-mappings.txt")]
    public void Relative_path(string coordinate, string expected) =>
        Assert.Equal(expected, MavenCoordinate.Parse(coordinate).RelativePath);

    [Theory]
    [InlineData("")]
    [InlineData("a:b")]
    [InlineData("a:b:c:d:e")]
    [InlineData("..:b:1")]
    [InlineData("a:b/../../x:1")]
    [InlineData("a:b:1@../../evil")]
    public void Rejects_invalid_and_traversing_coordinates(string coordinate) =>
        Assert.False(MavenCoordinate.TryParse(coordinate, out _));

    [Fact]
    public void Key_ignores_version_but_keeps_classifier()
    {
        Assert.Equal(MavenCoordinate.Parse("a:b:1").Key, MavenCoordinate.Parse("a:b:2").Key);
        Assert.NotEqual(MavenCoordinate.Parse("a:b:1").Key, MavenCoordinate.Parse("a:b:1:natives-windows").Key);
    }
}

public class VersionResolverTests
{
    private static void WriteVersion(GameRoot root, VersionJson json)
    {
        Directory.CreateDirectory(root.VersionDir(json.Id));
        File.WriteAllText(root.VersionJsonPath(json.Id), JsonSerializer.Serialize(json, Json.VersionFile));
    }

    private static VersionJson Vanilla() => new()
    {
        Id = "1.21.1",
        Type = "release",
        MainClass = "net.minecraft.client.main.Main",
        Downloads = new VersionDownloads { Client = new DownloadInfo { Url = "https://piston-data.mojang.com/client.jar", Sha1 = "aa", Size = 1 } },
        AssetIndex = new AssetIndexRef { Id = "17", Url = "https://x/17.json" },
        JavaVersion = new JavaVersionRef { Component = "java-runtime-delta", MajorVersion = 21 },
        Libraries = [new Library { Name = "org.ow2.asm:asm:9.3" }, new Library { Name = "com.mojang:brigadier:1.3.10" }],
        Arguments = new VersionArguments { Game = [ArgumentEntry.Of("--username"), ArgumentEntry.Of("${auth_player_name}")], Jvm = [ArgumentEntry.Of("-cp"), ArgumentEntry.Of("${classpath}")] },
    };

    private static VersionJson Fabric() => new()
    {
        Id = "fabric-1.21.1",
        InheritsFrom = "1.21.1",
        MainClass = "net.fabricmc.loader.impl.launch.knot.KnotClient",
        Libraries = [new Library { Name = "org.ow2.asm:asm:9.7", Url = "https://maven.fabricmc.net/" }, new Library { Name = "net.fabricmc:fabric-loader:0.16.9", Url = "https://maven.fabricmc.net/" }],
        Arguments = new VersionArguments { Jvm = [ArgumentEntry.Of("-DFabricMcEmu= net.minecraft.client.main.Main ")] },
    };

    [Fact]
    public void Child_overrides_main_class_and_wins_library_conflicts()
    {
        using var tmp = new TempDir();
        var root = new GameRoot(tmp.Path);
        WriteVersion(root, Vanilla());
        WriteVersion(root, Fabric());

        var resolved = VersionResolver.Resolve(root, "fabric-1.21.1");

        Assert.Equal("net.fabricmc.loader.impl.launch.knot.KnotClient", resolved.Json.MainClass);
        Assert.Equal(["fabric-1.21.1", "1.21.1"], resolved.Chain);
        Assert.Equal("1.21.1", resolved.ClientJarId);
        var asm = Assert.Single(resolved.Json.Libraries, l => l.Name.StartsWith("org.ow2.asm:asm:", StringComparison.Ordinal));
        Assert.Equal("org.ow2.asm:asm:9.7", asm.Name);
        Assert.Equal(3, resolved.Json.Libraries.Count);
        Assert.Equal(21, resolved.Json.JavaVersion!.MajorVersion);
        Assert.Equal(3, resolved.Json.Arguments!.Jvm.Count); // parent args first, then child
        Assert.Equal("1.21.1", VersionResolver.GameVersionOf(resolved));
    }

    [Fact]
    public void Missing_parent_reports_which_version()
    {
        using var tmp = new TempDir();
        var root = new GameRoot(tmp.Path);
        WriteVersion(root, Fabric());
        var e = Assert.Throws<AppException>(() => VersionResolver.Resolve(root, "fabric-1.21.1"));
        Assert.Equal("version.missingParent", e.Code);
        Assert.Equal("1.21.1", e.Args["id"]);
    }

    [Fact]
    public void Inheritance_cycle_is_detected()
    {
        using var tmp = new TempDir();
        var root = new GameRoot(tmp.Path);
        WriteVersion(root, new VersionJson { Id = "a", InheritsFrom = "b" });
        WriteVersion(root, new VersionJson { Id = "b", InheritsFrom = "a" });
        Assert.Equal("version.cycle", Assert.Throws<AppException>(() => VersionResolver.Resolve(root, "a")).Code);
    }

    [Fact]
    public void Merged_instance_reports_recorded_game_version()
    {
        var merged = VersionResolver.Merge(Vanilla(), Fabric());
        merged.Id = "My Pack";
        merged.Extra = new() { ["aphelionGameVersion"] = JsonSerializer.SerializeToElement("1.21.1") };
        var resolved = VersionResolver.Flatten([merged]);
        Assert.Equal("My Pack", resolved.ClientJarId);
        Assert.Equal("1.21.1", VersionResolver.GameVersionOf(resolved));
    }

    [Fact]
    public void Loader_profiles_with_nonstandard_timestamps_parse()
    {
        // Fabric meta writes offsets without a colon, which DateTimeOffset parsing rejects.
        const string fabric = """{"id":"fabric-loader-0.16.9-1.21.1","inheritsFrom":"1.21.1","releaseTime":"2024-11-14T08:27:59+0000","time":"2024-11-14T08:27:59+0000","type":"release","mainClass":"net.fabricmc.loader.impl.launch.knot.KnotClient","arguments":{"game":[],"jvm":["-DFabricMcEmu= net.minecraft.client.main.Main "]},"libraries":[{"name":"net.fabricmc:fabric-loader:0.16.9","url":"https://maven.fabricmc.net/","sha1":"abc","size":1}]}""";
        var json = JsonSerializer.Deserialize<VersionJson>(fabric, Json.External)!;
        Assert.Equal("2024-11-14T08:27:59+0000", json.ReleaseTime);
        Assert.Equal("abc", json.Libraries[0].Sha1);
    }

    [Fact]
    public void Arguments_round_trip_both_shapes()
    {
        const string text = """{"id":"x","arguments":{"game":["--a",{"rules":[{"action":"allow","features":{"is_demo_user":true}}],"value":"--demo"},{"rules":[],"value":["--w","1"]}],"jvm":[]}}""";
        var json = JsonSerializer.Deserialize<VersionJson>(text, Json.External)!;
        Assert.Equal(3, json.Arguments!.Game.Count);
        Assert.Equal(["--w", "1"], json.Arguments.Game[2].Values);
        var again = JsonSerializer.Deserialize<VersionJson>(JsonSerializer.Serialize(json, Json.External), Json.External)!;
        Assert.Equal("--demo", again.Arguments!.Game[1].Values.Single());
        Assert.True(again.Arguments.Game[1].Rules![0].Features!["is_demo_user"]);
    }
}

public class LibraryResolverTests
{
    private static readonly RuntimePlatform Win64 = new("windows", "10.0", Architecture.X64);

    [Fact]
    public void Legacy_natives_use_arch_placeholder()
    {
        var root = new GameRoot(Path.GetTempPath());
        var lib = new Library
        {
            Name = "tv.twitch:twitch-platform:6.5",
            Natives = new() { ["windows"] = "natives-windows-${arch}" },
            Downloads = new LibraryDownloads { Classifiers = new() { ["natives-windows-64"] = new DownloadInfo { Path = "tv/twitch/twitch-platform/6.5/twitch-platform-6.5-natives-windows-64.jar", Url = "https://libraries.minecraft.net/x.jar", Sha1 = "s", Size = 5 } } },
            Extract = new ExtractRules { Exclude = ["META-INF/"] },
        };
        var r = LibraryResolver.Resolve(lib, root, Win64)!;
        Assert.Null(r.Artifact);
        Assert.EndsWith("twitch-platform-6.5-natives-windows-64.jar", r.Native!.Path);
        Assert.Equal(["META-INF/"], r.NativeExcludes);
    }

    [Fact]
    public void Natives_for_other_os_are_skipped()
    {
        var lib = new Library { Name = "org.lwjgl.lwjgl:lwjgl-platform:2.9.4", Natives = new() { ["osx"] = "natives-osx" } };
        Assert.Null(LibraryResolver.Resolve(lib, new GameRoot(Path.GetTempPath()), Win64));
    }

    [Fact]
    public void Empty_url_means_generated_locally()
    {
        var lib = new Library { Name = "net.minecraftforge:forge:1.20.1-47.3.0:client", Downloads = new LibraryDownloads { Artifact = new DownloadInfo { Path = "net/minecraftforge/forge/1.20.1-47.3.0/forge-1.20.1-47.3.0-client.jar", Url = "", Sha1 = "x" } } };
        var r = LibraryResolver.Resolve(lib, new GameRoot(Path.GetTempPath()), Win64)!;
        Assert.Null(r.Artifact!.Url);
    }

    [Fact]
    public void Maven_style_library_builds_url_from_repository()
    {
        var lib = new Library { Name = "net.fabricmc:intermediary:1.21.1", Url = "https://maven.fabricmc.net/" };
        var r = LibraryResolver.Resolve(lib, new GameRoot(Path.GetTempPath()), Win64)!;
        Assert.Equal("https://maven.fabricmc.net/net/fabricmc/intermediary/1.21.1/intermediary-1.21.1.jar", r.Artifact!.Url);
    }

    [Theory]
    [InlineData("http://files.minecraftforge.net/maven/", "https://maven.minecraftforge.net/")]
    [InlineData("http://example.org/repo", "https://example.org/repo")]
    [InlineData("https://maven.fabricmc.net/", "https://maven.fabricmc.net/")]
    public void Legacy_repositories_are_rewritten(string input, string expected) =>
        Assert.Equal(expected, LibraryResolver.NormalizeRepository(input));

    [Fact]
    public void Server_only_legacy_forge_library_is_ignored()
    {
        var lib = new Library { Name = "a:b:1", Clientreq = false };
        Assert.Null(LibraryResolver.Resolve(lib, new GameRoot(Path.GetTempPath()), Win64));
    }
}

public class SafePathTests
{
    [Theory]
    [InlineData("../evil")]
    [InlineData("a/../../evil")]
    [InlineData("C:\\Windows\\x")]
    [InlineData("/etc/passwd")]
    [InlineData("")]
    public void Traversal_is_rejected(string relative) =>
        Assert.Throws<InvalidDataException>(() => SafePath.Combine(Path.GetTempPath(), relative));

    [Fact]
    public void Nested_relative_path_is_allowed() =>
        Assert.EndsWith(Path.Combine("mods", "a.jar"), SafePath.Combine(Path.GetTempPath(), "mods/a.jar"));

    [Theory]
    [InlineData("主線生存", true)]
    [InlineData("My Pack 1.21", true)]
    [InlineData("CON", false)]
    [InlineData("a/b", false)]
    [InlineData("trailing.", false)]
    [InlineData(" lead", false)]
    [InlineData("..", false)]
    public void Instance_names(string name, bool valid) => Assert.Equal(valid, SafePath.IsValidName(name));
}
