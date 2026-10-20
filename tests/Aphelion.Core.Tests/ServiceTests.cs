using System.IO.Compression;
using System.Text.Json;
using Aphelion.Core.Content;
using Aphelion.Core.Install;
using Aphelion.Core.Instances;
using Aphelion.Core.Java;
using Aphelion.Core.Minecraft;
using Aphelion.Core.Storage;
using Aphelion.Core.Tasks;

namespace Aphelion.Core.Tests;

public class JavaTests
{
    private static JavaInstall J(int major, string arch = "x64", JavaSource source = JavaSource.Registry) =>
        new($@"C:\j{major}{arch}\bin\javaw.exe", $@"C:\j{major}{arch}", major, $"{major}.0.1", arch, null, source);

    [Theory]
    [InlineData("1.8.0_392", 8)]
    [InlineData("17.0.9", 17)]
    [InlineData("21", 21)]
    [InlineData("25-ea", 25)]
    [InlineData("garbage", 0)]
    public void Major_version(string version, int major) => Assert.Equal(major, JavaProbe.MajorOf(version));

    [Fact]
    public void Java8_games_only_get_java8()
    {
        Assert.Null(JavaManager.Pick([J(17), J(21)], 8));
        Assert.Equal(8, JavaManager.Pick([J(17), J(8), J(21)], 8)!.Major);
    }

    [Fact]
    public void Exact_major_preferred_then_closest_newer()
    {
        Assert.Equal(17, JavaManager.Pick([J(21), J(17), J(25)], 17)!.Major);
        Assert.Equal(21, JavaManager.Pick([J(25), J(21), J(8)], 17)!.Major);
        Assert.Null(JavaManager.Pick([J(17)], 21));
    }

    [Fact]
    public void Sixty_four_bit_is_preferred()
    {
        var pick = JavaManager.Pick([J(21, "x86"), J(21, "x64")], 21)!;
        Assert.Equal("x64", pick.Arch);
    }

    [Theory]
    [InlineData(8, "jre-legacy")]
    [InlineData(17, "java-runtime-gamma")]
    [InlineData(21, "java-runtime-delta")]
    public void Mojang_components(int major, string component) => Assert.Equal(component, JavaManager.ComponentFor(major));

    [Fact]
    public void Release_file_is_read_without_starting_java()
    {
        using var tmp = new TempDir();
        tmp.File(@"jdk\bin\javaw.exe", "");
        tmp.File(@"jdk\release", "IMPLEMENTOR=\"Eclipse Adoptium\"\nJAVA_VERSION=\"17.0.9\"\nOS_ARCH=\"amd64\"\n");
        var j = JavaProbe.Inspect(Path.Combine(tmp.Path, "jdk"), JavaSource.KnownLocation)!;
        Assert.Equal(17, j.Major);
        Assert.Equal("x64", j.Arch);
        Assert.Equal("Eclipse Adoptium", j.Vendor);
        Assert.Equal(Path.Combine(tmp.Path, "jdk"), JavaProbe.HomeOf(Path.Combine(tmp.Path, "jdk", "bin", "javaw.exe")));
    }
}

public class InstanceStoreTests
{
    private static void Version(GameRoot root, string id, string? inheritsFrom = null, params string[] libraries)
    {
        Directory.CreateDirectory(root.VersionDir(id));
        var json = new VersionJson { Id = id, InheritsFrom = inheritsFrom, MainClass = "Main", Libraries = libraries.Select(l => new Library { Name = l }).ToList() };
        File.WriteAllText(root.VersionJsonPath(id), JsonSerializer.Serialize(json, Json.VersionFile));
    }

    [Fact]
    public void Lists_versions_and_detects_loaders()
    {
        using var tmp = new TempDir();
        var root = new GameRoot(tmp.Path);
        Version(root, "1.20.1");
        Version(root, "forge", "1.20.1", "net.minecraftforge:fmlloader:1.20.1-47.3.0");
        Version(root, "neo", "1.20.1", "net.neoforged:neoforge:21.1.77:universal");
        Directory.CreateDirectory(Path.Combine(root.Versions, "empty-folder"));

        var list = InstanceStore.List(root);
        Assert.Equal(3, list.Count);
        var forge = list.Single(i => i.Id == "forge");
        Assert.Equal(LoaderKind.Forge, forge.Loader.Kind);
        Assert.Equal("47.3.0", forge.Loader.Version);
        Assert.Equal("1.20.1", forge.GameVersion);
        Assert.True(forge.Isolated);
        Assert.False(list.Single(i => i.Id == "1.20.1").Isolated);
        Assert.Equal(LoaderKind.NeoForge, list.Single(i => i.Id == "neo").Loader.Kind);
    }

    [Fact]
    public void Broken_versions_are_listed_with_an_error()
    {
        using var tmp = new TempDir();
        var root = new GameRoot(tmp.Path);
        Version(root, "orphan", "missing-parent");
        var item = Assert.Single(InstanceStore.List(root));
        Assert.NotNull(item.Error);
    }

    [Fact]
    public void Rename_moves_files_rewrites_id_and_children()
    {
        using var tmp = new TempDir();
        var root = new GameRoot(tmp.Path);
        Version(root, "base");
        File.WriteAllText(root.VersionJarPath("base"), "jar");
        Version(root, "child", "base");

        InstanceStore.Rename(root, "base", "新名字");

        Assert.False(Directory.Exists(root.VersionDir("base")));
        Assert.True(File.Exists(root.VersionJarPath("新名字")));
        Assert.Equal("新名字", VersionResolver.ReadVersionFile(root.VersionJsonPath("新名字")).Id);
        Assert.Equal("新名字", VersionResolver.ReadVersionFile(root.VersionJsonPath("child")).InheritsFrom);
    }

    [Fact]
    public void Rename_rejects_existing_and_invalid_names()
    {
        using var tmp = new TempDir();
        var root = new GameRoot(tmp.Path);
        Version(root, "a");
        Version(root, "b");
        Assert.Equal("instance.exists", Assert.Throws<AppException>(() => InstanceStore.Rename(root, "a", "b")).Code);
        Assert.Equal("instance.invalidName", Assert.Throws<AppException>(() => InstanceStore.Rename(root, "a", "con")).Code);
    }

    [Fact]
    public void First_metadata_write_freezes_inferred_values()
    {
        using var tmp = new TempDir();
        var root = new GameRoot(tmp.Path);
        Version(root, "1.20.1");
        InstanceStore.UpdateMeta(root, "1.20.1", m => m.DisplayName = "原版");
        var meta = InstanceStore.ReadMeta(root.VersionDir("1.20.1"))!;
        Assert.Equal("原版", meta.DisplayName);
        Assert.False(meta.Isolated);
        Assert.Equal(LoaderKind.Vanilla, meta.Loader!.Kind);
        Assert.Equal("原版", InstanceStore.Load(root, "1.20.1").Name);
    }

    [Fact]
    public void Stale_staging_folders_are_removed()
    {
        using var tmp = new TempDir();
        var root = new GameRoot(tmp.Path);
        Directory.CreateDirectory(Path.Combine(root.Versions, InstanceInstaller.StagingPrefix + "abc"));
        Version(root, "keep");
        InstanceInstaller.CleanupStaging(root);
        Assert.Single(Directory.GetDirectories(root.Versions));
    }
}

public class ModpackTests
{
    [Fact]
    public void Overrides_cannot_escape_the_game_directory()
    {
        using var tmp = new TempDir();
        var pack = tmp.Zip("evil.mrpack", ("overrides/config/ok.txt", "ok"), ("overrides/../../escaped.txt", "bad"));
        var dest = Path.Combine(tmp.Path, "game");
        using var zip = ZipFile.OpenRead(pack);
        Assert.Throws<InvalidDataException>(() => ModpackService.ExtractFolder(zip, "overrides/", dest));
        Assert.False(File.Exists(Path.Combine(tmp.Path, "escaped.txt")));
    }

    [Fact]
    public void Index_is_validated_and_loader_detected()
    {
        using var tmp = new TempDir();
        var pack = tmp.Zip("p.mrpack", ("modrinth.index.json", """{"formatVersion":1,"game":"minecraft","versionId":"1.0","name":"P","files":[],"dependencies":{"minecraft":"1.21.1","fabric-loader":"0.16.9"}}"""));
        using var zip = ZipFile.OpenRead(pack);
        var index = ModpackService.ReadIndex(zip);
        Assert.Equal((LoaderKind.Fabric, "0.16.9"), ModpackService.LoaderOf(index));

        var bad = tmp.Zip("b.mrpack", ("readme.txt", "x"));
        using var badZip = ZipFile.OpenRead(bad);
        Assert.Equal("modpack.invalid", Assert.Throws<AppException>(() => ModpackService.ReadIndex(badZip)).Code);
    }
}

public class ContentMetadataTests
{
    [Fact]
    public void Fabric_mod_json()
    {
        using var tmp = new TempDir();
        var jar = tmp.Zip("m.jar", ("fabric.mod.json", """{"id":"sodium","name":"Sodium","version":"0.6.0","description":"Fast\nrenderer","authors":["jellysquid3",{"name":"IMS"}]}"""));
        var m = ContentMetadataReader.Read(jar)!;
        Assert.Equal("sodium", m.Id);
        Assert.Equal("Sodium", m.Name);
        Assert.Equal("jellysquid3, IMS", m.Authors);
        Assert.Equal("fabric", m.Loader);
    }

    [Fact]
    public void Forge_mods_toml_with_jar_version_placeholder()
    {
        using var tmp = new TempDir();
        var jar = tmp.Zip("m.jar",
            ("META-INF/mods.toml", "modLoader=\"javafml\"\n[[mods]]\nmodId=\"create\"\nversion=\"${file.jarVersion}\"\ndisplayName=\"Create\"\ndescription='''\nAesthetic technology\n'''\n[[dependencies.create]]\nmodId=\"forge\"\n"),
            ("META-INF/MANIFEST.MF", "Manifest-Version: 1.0\nImplementation-Version: 0.5.1.f\n"));
        var m = ContentMetadataReader.Read(jar)!;
        Assert.Equal("create", m.Id);
        Assert.Equal("0.5.1.f", m.Version);
        Assert.Equal("Aesthetic technology", m.Description);
    }

    [Fact]
    public void Resource_pack_description_drops_formatting_codes()
    {
        using var tmp = new TempDir();
        var zip = tmp.Zip("r.zip", ("pack.mcmeta", """{"pack":{"pack_format":34,"description":"§6Faithful §r32x"}}"""));
        Assert.Equal("Faithful 32x", ContentMetadataReader.Read(zip)!.Description);
    }

    [Fact]
    public void Not_a_zip_returns_null()
    {
        using var tmp = new TempDir();
        Assert.Null(ContentMetadataReader.Read(tmp.File("x.jar", "not a zip")));
    }
}

public class LoaderCatalogTests
{
    [Theory]
    [InlineData("21.1.77", "1.21.1")]
    [InlineData("20.4.237", "1.20.4")]
    [InlineData("21.0.167", "1.21")]
    [InlineData("nonsense", null)]
    public void NeoForge_version_maps_to_game_version(string neo, string? game) => Assert.Equal(game, LoaderCatalog.GameVersionOfNeoForge(neo));

    [Fact]
    public void Forge_installer_url() =>
        Assert.Equal("https://maven.minecraftforge.net/net/minecraftforge/forge/1.20.1-47.3.0/forge-1.20.1-47.3.0-installer.jar",
            LoaderCatalog.ForgeInstallerUrl("1.20.1", "47.3.0"));
}

public class TaskHubTests
{
    [Fact]
    public async Task Successful_task_reports_result_and_progress()
    {
        var hub = new TaskHub();
        var seen = new List<string?>();
        hub.Changed += s => { lock (seen) seen.Add(s.Stage); };
        var id = hub.Start("test", "t", async (p, ct) => { p.Report(new StageProgress("step.one", 0.5)); await Task.Yield(); return 42; });
        var done = await hub.WaitAsync(id);
        Assert.Equal(Tasks.TaskStatus.Succeeded, done.Status);
        Assert.Equal(42, done.Result);
        Assert.Contains("step.one", seen);
    }

    [Fact]
    public async Task Failure_carries_the_app_error_code()
    {
        var hub = new TaskHub();
        var id = hub.Start("test", "t", (_, _) => throw AppException.Of("x.code", "boom", ("a", "1")));
        var done = await hub.WaitAsync(id);
        Assert.Equal(Tasks.TaskStatus.Failed, done.Status);
        Assert.Equal("x.code", done.ErrorCode);
        Assert.Equal("1", done.ErrorArgs!["a"]);
    }

    [Fact]
    public async Task Cancel_stops_the_work()
    {
        var hub = new TaskHub();
        var started = new TaskCompletionSource();
        var id = hub.Start("test", "t", async (_, ct) => { started.SetResult(); await Task.Delay(Timeout.Infinite, ct); return null; });
        await started.Task;
        Assert.True(hub.Cancel(id));
        Assert.Equal(Tasks.TaskStatus.Cancelled, (await hub.WaitAsync(id)).Status);
    }
}
