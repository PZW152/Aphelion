using System.Runtime.InteropServices;
using Aphelion.Core.Accounts;
using Aphelion.Core.Launch;
using Aphelion.Core.Minecraft;

namespace Aphelion.Core.Tests;

public class LaunchArgumentsTests
{
    private static readonly RuntimePlatform Win64 = new("windows", "10.0", Architecture.X64);
    private static readonly LaunchIdentity Offline = new("Kestrel_07", "0123456789abcdef0123456789abcdef", "0123456789abcdef0123456789abcdef", "legacy", "", null, null);

    private static LaunchContext Context(VersionJson json, LaunchIdentity? identity = null, string? server = null, bool fullscreen = false) => new()
    {
        Root = new GameRoot(@"C:\mc"),
        Version = new ResolvedVersion { Json = json, Chain = [json.Id], ClientJarId = json.Id },
        GameDirectory = @"C:\mc\versions\" + json.Id,
        NativesDirectory = @"C:\tmp\natives",
        AssetsRoot = @"C:\mc\assets",
        Identity = identity ?? Offline,
        HeapMb = 4096,
        JavaMajor = 21,
        Width = 1280,
        Height = 720,
        Fullscreen = fullscreen,
        JoinServer = server,
        Platform = Win64,
    };

    private static VersionJson Modern() => new()
    {
        Id = "1.21.1",
        Type = "release",
        MainClass = "net.minecraft.client.main.Main",
        AssetIndex = new AssetIndexRef { Id = "17" },
        Libraries =
        [
            new Library { Name = "com.mojang:brigadier:1.3.10" },
            new Library { Name = "org.lwjgl:lwjgl:3.3.3:natives-macos", Rules = [new Rule { Action = "allow", Os = new OsRule { Name = "osx" } }] },
        ],
        Arguments = new VersionArguments
        {
            Game =
            [
                ArgumentEntry.Of("--username"), ArgumentEntry.Of("${auth_player_name}"),
                ArgumentEntry.Of("--uuid"), ArgumentEntry.Of("${auth_uuid}"),
                ArgumentEntry.Of("--accessToken"), ArgumentEntry.Of("${auth_access_token}"),
                ArgumentEntry.Of("--clientId"), ArgumentEntry.Of("${clientid}"),
                ArgumentEntry.Of("--xuid"), ArgumentEntry.Of("${auth_xuid}"),
                ArgumentEntry.Of("--assetIndex"), ArgumentEntry.Of("${assets_index_name}"),
                new ArgumentEntry { Values = ["--demo"], Rules = [new Rule { Features = new() { ["is_demo_user"] = true } }] },
                new ArgumentEntry { Values = ["--width", "${resolution_width}", "--height", "${resolution_height}"], Rules = [new Rule { Features = new() { ["has_custom_resolution"] = true } }] },
                new ArgumentEntry { Values = ["--quickPlayMultiplayer", "${quickPlayMultiplayer}"], Rules = [new Rule { Features = new() { ["is_quick_play_multiplayer"] = true } }] },
            ],
            Jvm =
            [
                new ArgumentEntry { Values = ["-XX:HeapDumpPath=MojangTricksIntelDriversForPerformance_javaw.exe_minecraft.exe.heapdump"], Rules = [new Rule { Os = new OsRule { Name = "windows" } }] },
                ArgumentEntry.Of("-Djava.library.path=${natives_directory}"),
                ArgumentEntry.Of("-cp"), ArgumentEntry.Of("${classpath}"),
            ],
        },
    };

    [Fact]
    public void Modern_arguments_follow_rules_and_features()
    {
        var cmd = LaunchArguments.Build(Context(Modern()));
        Assert.Equal("net.minecraft.client.main.Main", cmd.MainClass);
        Assert.Contains("-Xmx4096m", cmd.JvmArguments);
        Assert.Contains(@"-Djava.library.path=C:\tmp\natives", cmd.JvmArguments);
        Assert.Contains("-XX:HeapDumpPath=MojangTricksIntelDriversForPerformance_javaw.exe_minecraft.exe.heapdump", cmd.JvmArguments);
        Assert.DoesNotContain("--demo", cmd.GameArguments);
        Assert.DoesNotContain("--quickPlayMultiplayer", cmd.GameArguments);
        var g = cmd.GameArguments.ToList();
        Assert.Equal("Kestrel_07", g[g.IndexOf("--username") + 1]);
        Assert.Equal("1280", g[g.IndexOf("--width") + 1]);
        Assert.Equal("17", g[g.IndexOf("--assetIndex") + 1]);
        // Empty optional values are dropped instead of passing "--clientId ''".
        Assert.DoesNotContain("--clientId", g);
        Assert.DoesNotContain("--xuid", g);
    }

    [Fact]
    public void Classpath_keeps_library_order_skips_other_os_and_ends_with_client_jar()
    {
        var cmd = LaunchArguments.Build(Context(Modern()));
        Assert.Equal(2, cmd.Classpath.Count);
        Assert.EndsWith("brigadier-1.3.10.jar", cmd.Classpath[0]);
        Assert.Equal(@"C:\mc\versions\1.21.1\1.21.1.jar", cmd.Classpath[^1]);
        var cpArg = cmd.JvmArguments[cmd.JvmArguments.ToList().IndexOf("-cp") + 1];
        Assert.Equal(string.Join(';', cmd.Classpath), cpArg);
    }

    [Fact]
    public void Quick_play_is_used_when_the_version_supports_it()
    {
        var cmd = LaunchArguments.Build(Context(Modern(), server: "mc.example.org:25570"));
        var g = cmd.GameArguments.ToList();
        Assert.Equal("mc.example.org:25570", g[g.IndexOf("--quickPlayMultiplayer") + 1]);
        Assert.DoesNotContain("--server", g);
    }

    [Fact]
    public void Legacy_versions_get_classpath_server_and_resolution_arguments()
    {
        var json = new VersionJson
        {
            Id = "1.8.9",
            MainClass = "net.minecraft.client.main.Main",
            Assets = "1.8",
            MinecraftArguments = "--username ${auth_player_name} --version ${version_name} --gameDir ${game_directory} --assetsDir ${assets_root} --assetIndex ${assets_index_name} --uuid ${auth_uuid} --accessToken ${auth_access_token} --userProperties ${user_properties} --userType ${user_type}",
        };
        var cmd = LaunchArguments.Build(Context(json, server: "play.example.org") with { JavaMajor = 8 });
        Assert.Contains("-cp", cmd.JvmArguments);
        Assert.Contains("-Dsun.stdout.encoding=UTF-8", cmd.JvmArguments);
        var g = cmd.GameArguments.ToList();
        Assert.Equal("{}", g[g.IndexOf("--userProperties") + 1]);
        Assert.Equal("legacy", g[g.IndexOf("--userType") + 1]);
        Assert.Equal("play.example.org", g[g.IndexOf("--server") + 1]);
        Assert.Equal("25565", g[g.IndexOf("--port") + 1]);
        Assert.Equal("720", g[g.IndexOf("--height") + 1]);
    }

    [Fact]
    public void Fullscreen_replaces_window_size()
    {
        var cmd = LaunchArguments.Build(Context(Modern(), fullscreen: true));
        Assert.Contains("--fullscreen", cmd.GameArguments);
        Assert.DoesNotContain("--width", cmd.GameArguments);
    }

    [Fact]
    public void Authlib_injector_agent_is_added_for_yggdrasil_accounts()
    {
        var ygg = new LaunchIdentity("Steve", "u", "token-123456", "mojang", "", "https://skin.example/api/yggdrasil/", "eyJtZXRhIjp7fX0=");
        var cmd = LaunchArguments.Build(Context(Modern(), identity: ygg) with { AuthlibInjectorJar = @"C:\rt\authlib-injector.jar" });
        Assert.Contains(@"-javaagent:C:\rt\authlib-injector.jar=https://skin.example/api/yggdrasil/", cmd.JvmArguments);
        Assert.Contains("-Dauthlibinjector.yggdrasil.prefetched=eyJtZXRhIjp7fX0=", cmd.JvmArguments);
    }

    [Fact]
    public void User_gc_choice_replaces_default_collector_flags()
    {
        var cmd = LaunchArguments.Build(Context(Modern()) with { ExtraJvmArguments = ["-XX:+UseZGC"] });
        Assert.DoesNotContain("-XX:+UseG1GC", cmd.JvmArguments);
        Assert.Equal("-XX:+UseZGC", cmd.JvmArguments[^1]);
    }

    [Fact]
    public void Described_command_line_never_contains_the_token()
    {
        var msa = new LaunchIdentity("Alex", "u", "SECRET-TOKEN-VALUE", "msa", "", null, null);
        var cmd = LaunchArguments.Build(Context(Modern(), identity: msa));
        var text = LaunchArguments.Describe(@"C:\java\bin\javaw.exe", cmd, msa);
        Assert.DoesNotContain("SECRET-TOKEN-VALUE", text);
        Assert.Contains("--accessToken ***", text);
    }

    [Theory]
    [InlineData("-Xss2m -Dfoo=bar", new[] { "-Xss2m", "-Dfoo=bar" })]
    [InlineData("  -Da=\"two words\"  -Db ", new[] { "-Da=two words", "-Db" })]
    [InlineData("", new string[0])]
    public void User_arguments_are_split_with_quotes(string text, string[] expected) =>
        Assert.Equal(expected, LaunchArguments.ParseUserArguments(text));
}

public class MemoryTests
{
    [Fact]
    public void Vanilla_gets_two_gigabytes_when_available() =>
        Assert.Equal(2048, SystemMemory.AutoHeapMb(new MemoryStatus(32768, 20000), 0, modded: false));

    [Fact]
    public void Large_modpacks_get_more_but_never_exceed_what_is_free()
    {
        Assert.Equal(6144, SystemMemory.AutoHeapMb(new MemoryStatus(32768, 20000), 150, modded: true));
        Assert.Equal(2304, SystemMemory.AutoHeapMb(new MemoryStatus(8192, 3100), 150, modded: true));
    }

    [Fact]
    public void Never_below_one_gigabyte() =>
        Assert.Equal(1024, SystemMemory.AutoHeapMb(new MemoryStatus(4096, 300), 300, modded: true));
}

public class OfflineAccountTests
{
    [Fact]
    public void Offline_uuid_matches_the_vanilla_algorithm() =>
        Assert.Equal("b50ad385829d3141a2167e7d7539ba7f", OfflineAccounts.UuidFor("Notch"));

    [Theory]
    [InlineData("Kestrel_07", true)]
    [InlineData("ab", false)]
    [InlineData("seventeen_chars__", false)]
    [InlineData("名字", false)]
    [InlineData("has space", false)]
    public void Names(string name, bool valid) => Assert.Equal(valid, OfflineAccounts.IsValidName(name));
}

public class GameLogParserTests
{
    [Fact]
    public void Parses_log4j_xml_events_with_throwables()
    {
        var parser = new GameLogParser();
        var lines = new[]
        {
            "<log4j:Event logger=\"net.minecraft.client.Minecraft\" timestamp=\"1700000000000\" level=\"ERROR\" thread=\"Render thread\">",
            "  <log4j:Message><![CDATA[Something broke & failed]]></log4j:Message>",
            "  <log4j:Throwable><![CDATA[java.lang.IllegalStateException: x",
            "\tat a.b.C.d(C.java:1)",
            "]]></log4j:Throwable>",
            "</log4j:Event>",
        }.SelectMany(parser.Feed).ToList();
        var line = Assert.Single(lines);
        Assert.Equal(GameLogLevel.Error, line.Level);
        Assert.Equal("Render thread", line.Thread);
        Assert.StartsWith("Something broke & failed\njava.lang.IllegalStateException: x", line.Message);
    }

    [Fact]
    public void Classifies_plain_console_lines()
    {
        var parser = new GameLogParser();
        var line = parser.Feed("[14:02:26] [main/WARN]: Mod 'x' uses a deprecated mixin config").Single();
        Assert.Equal(GameLogLevel.Warn, line.Level);
        Assert.Equal("main", line.Thread);
        Assert.Equal("Mod 'x' uses a deprecated mixin config", line.Message);
        Assert.Equal(GameLogLevel.Info, parser.Feed("hello").Single().Level);
        Assert.Equal(GameLogLevel.Error, parser.Feed("\tat net.minecraft.Foo.bar(Foo.java:3)").Single().Level);
    }
}
