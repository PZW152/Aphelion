using System.Text.Json.Nodes;
using Aphelion.Core.Accounts;
using Aphelion.Core.Logging;
using Aphelion.Core.Net;
using Aphelion.Core.Settings;
using Aphelion.Core.Storage;

namespace Aphelion.Core.Tests;

public class AtomicFileTests
{
    private sealed class Doc { public int Value { get; set; } }

    [Fact]
    public void Writes_and_keeps_previous_version_as_backup()
    {
        using var tmp = new TempDir();
        var path = Path.Combine(tmp.Path, "a", "doc.json");
        AtomicFile.WriteJson(path, new Doc { Value = 1 });
        AtomicFile.WriteJson(path, new Doc { Value = 2 });
        Assert.Equal(2, AtomicFile.ReadJson<Doc>(path)!.Value);
        Assert.Equal(1, AtomicFile.ReadJson<Doc>(path + ".bak")!.Value);
        Assert.Empty(Directory.GetFiles(Path.GetDirectoryName(path)!, "*.tmp-*"));
    }

    [Fact]
    public void Corrupt_file_falls_back_to_backup_and_is_quarantined()
    {
        using var tmp = new TempDir();
        var path = Path.Combine(tmp.Path, "doc.json");
        AtomicFile.WriteJson(path, new Doc { Value = 1 });
        AtomicFile.WriteJson(path, new Doc { Value = 2 });
        File.WriteAllText(path, "{ not json");
        Assert.Equal(1, AtomicFile.ReadJson<Doc>(path)!.Value);
        Assert.Single(Directory.GetFiles(tmp.Path, "doc.json.corrupt-*"));
    }

    [Fact]
    public void Missing_file_reads_as_null()
    {
        using var tmp = new TempDir();
        Assert.Null(AtomicFile.ReadJson<Doc>(Path.Combine(tmp.Path, "none.json")));
    }
}

public class SettingsTests
{
    [Fact]
    public void Fresh_settings_are_normalized_defaults()
    {
        using var tmp = new TempDir();
        var store = new SettingsStore(Path.Combine(tmp.Path, "settings.json"));
        Assert.Equal(LauncherSettings.CurrentSchema, store.Current.Schema);
        Assert.Equal("auto", store.Current.Language);
    }

    [Fact]
    public void Out_of_range_values_are_clamped_on_save()
    {
        using var tmp = new TempDir();
        var store = new SettingsStore(Path.Combine(tmp.Path, "settings.json"));
        var s = store.Update(x => { x.Download.Concurrency = 999; x.Accent = "red"; x.Language = "xx"; x.Game.Memory.FixedMb = 1; });
        Assert.Equal(64, s.Download.Concurrency);
        Assert.Equal("#FF5B14", s.Accent);
        Assert.Equal("auto", s.Language);
        Assert.Equal(512, s.Game.Memory.FixedMb);
        Assert.Equal(64, new SettingsStore(Path.Combine(tmp.Path, "settings.json")).Current.Download.Concurrency);
    }

    [Fact]
    public void Schema_zero_is_migrated_and_backed_up()
    {
        using var tmp = new TempDir();
        var path = tmp.File("settings.json", """{"language":"zh-Hant","download":{"concurrency":8}}""");
        var s = SettingsStore.Load(path);
        Assert.Equal("zh-Hant", s.Language);
        Assert.Equal(8, s.Download.Concurrency);
        Assert.True(File.Exists(path + ".v0.bak"));
    }

    [Fact]
    public void Newer_schema_is_read_without_rewriting()
    {
        using var tmp = new TempDir();
        var path = tmp.File("settings.json", """{"schema":99,"language":"en","futureThing":true}""");
        var s = SettingsStore.Load(path);
        Assert.Equal("en", s.Language);
        Assert.Contains("futureThing", File.ReadAllText(path));
    }

    [Fact]
    public void Migrate_sets_current_schema()
    {
        var node = new JsonObject();
        SettingsStore.Migrate(node, 0);
        Assert.Equal(LauncherSettings.CurrentSchema, node["schema"]!.GetValue<int>());
    }
}

public class AppPathsTests
{
    [Fact]
    public void Override_wins()
    {
        using var tmp = new TempDir();
        var p = AppPaths.Resolve(tmp.Path, name => name == AppPaths.HomeEnvironmentVariable ? Path.Combine(tmp.Path, "home") : null);
        Assert.Equal(RunMode.Override, p.Mode);
        Assert.Equal(Path.Combine(tmp.Path, "home"), p.Root);
    }

    [Fact]
    public void Portable_marker_keeps_data_beside_the_executable()
    {
        using var tmp = new TempDir();
        tmp.File(AppPaths.PortableMarkerName, "");
        var p = AppPaths.Resolve(tmp.Path, _ => null);
        Assert.Equal(RunMode.Portable, p.Mode);
        Assert.Equal(Path.Combine(tmp.Path, "Data"), p.Root);
        Assert.Equal(Path.Combine(tmp.Path, "Data", "config", "settings.json"), p.SettingsFile);
    }

    [Fact]
    public void Standard_mode_uses_local_app_data()
    {
        using var tmp = new TempDir();
        var p = AppPaths.Resolve(tmp.Path, _ => null);
        Assert.Equal(RunMode.Standard, p.Mode);
        Assert.Equal(AppPaths.StandardRoot(), p.Root);
    }

    [Fact]
    public void Ownership_check_is_path_aware()
    {
        var p = AppPaths.ForRoot(@"C:\Data\Aphelion");
        Assert.True(p.Owns(@"C:\Data\Aphelion\cache\x"));
        Assert.False(p.Owns(@"C:\Data\AphelionOther\x"));
        Assert.False(p.Owns(@"C:\Data"));
    }
}

public class AccountStoreTests
{
    [Fact]
    public void Same_profile_signing_in_again_updates_instead_of_duplicating()
    {
        using var tmp = new TempDir();
        var store = new AccountStore(Path.Combine(tmp.Path, "accounts.json"), new PlainSecretProtector());
        var first = store.Upsert(new Account { Kind = AccountKind.Microsoft, Name = "A", Uuid = "u1" }, new AccountSecret { AccessToken = "t1" });
        var second = store.Upsert(new Account { Kind = AccountKind.Microsoft, Name = "A2", Uuid = "u1" }, new AccountSecret { AccessToken = "t2" });
        Assert.Equal(first.Id, second.Id);
        var only = Assert.Single(store.All);
        Assert.Equal("A2", only.Name);
        Assert.Equal("t2", store.ReadSecret(only)!.AccessToken);
    }

    [Fact]
    public void Secrets_are_not_stored_in_plain_text_with_a_real_protector()
    {
        using var tmp = new TempDir();
        var path = Path.Combine(tmp.Path, "accounts.json");
        var store = new AccountStore(path, new XorProtector());
        store.Upsert(new Account { Kind = AccountKind.Microsoft, Name = "A", Uuid = "u" }, new AccountSecret { AccessToken = "VERY-SECRET-TOKEN" });
        Assert.DoesNotContain("VERY-SECRET-TOKEN", File.ReadAllText(path));
        var reloaded = new AccountStore(path, new XorProtector());
        Assert.Equal("VERY-SECRET-TOKEN", reloaded.ReadSecret(reloaded.All[0])!.AccessToken);
    }

    [Fact]
    public void Undecryptable_secret_flags_the_account()
    {
        using var tmp = new TempDir();
        var path = Path.Combine(tmp.Path, "accounts.json");
        new AccountStore(path, new XorProtector()).Upsert(new Account { Kind = AccountKind.Microsoft, Name = "A", Uuid = "u" }, new AccountSecret { AccessToken = "x" });
        var other = new AccountStore(path, new FailingProtector());
        Assert.Null(other.ReadSecret(other.All[0]));
        Assert.True(other.All[0].NeedsSignIn);
    }

    [Fact]
    public void Removing_the_selected_account_selects_another()
    {
        using var tmp = new TempDir();
        var store = new AccountStore(Path.Combine(tmp.Path, "accounts.json"), new PlainSecretProtector());
        var a = store.Upsert(new Account { Kind = AccountKind.Offline, Name = "Aaa", Uuid = "1" }, null);
        var b = store.Upsert(new Account { Kind = AccountKind.Offline, Name = "Bbb", Uuid = "2" }, null);
        Assert.Equal(b.Id, store.SelectedId);
        store.Remove(b.Id);
        Assert.Equal(a.Id, store.SelectedId);
    }

    private sealed class XorProtector : ISecretProtector
    {
        public byte[] Protect(byte[] plain) => plain.Select(b => (byte)(b ^ 0x5A)).ToArray();
        public byte[] Unprotect(byte[] data) => Protect(data);
    }

    private sealed class FailingProtector : ISecretProtector
    {
        public byte[] Protect(byte[] plain) => plain;
        public byte[] Unprotect(byte[] data) => throw new System.Security.Cryptography.CryptographicException("wrong user");
    }
}

public class RedactionTests
{
    [Theory]
    [InlineData("\"accessToken\":\"abc123\"", "abc123")]
    [InlineData("refresh_token=xyz789&scope=a", "xyz789")]
    [InlineData("--accessToken 0f0f0f0f0f --version 1", "0f0f0f0f0f")]
    [InlineData("Authorization: Bearer secret.value.here", "secret.value.here")]
    [InlineData("token eyJhbGciOiJIUzI1NiJ9.eyJzdWIiOiIxMjM0NTY3ODkwIn0.abc", "eyJzdWIiOiIxMjM0NTY3ODkwIn0")]
    public void Secrets_are_masked(string input, string secret) => Assert.DoesNotContain(secret, Log.Redact(input));
}

public class DownloadSourcesTests
{
    [Theory]
    [InlineData("https://libraries.minecraft.net/a/b.jar", "https://bmclapi2.bangbang93.com/maven/a/b.jar")]
    [InlineData("https://resources.download.minecraft.net/ab/abcd", "https://bmclapi2.bangbang93.com/assets/ab/abcd")]
    [InlineData("https://piston-meta.mojang.com/mc/game/version_manifest_v2.json", "https://bmclapi2.bangbang93.com/mc/game/version_manifest_v2.json")]
    [InlineData("https://meta.fabricmc.net/v2/versions", "https://bmclapi2.bangbang93.com/fabric-meta/v2/versions")]
    public void Official_urls_map_to_the_mirror(string official, string mirror) => Assert.Equal(mirror, DownloadSources.ToMirror(official));

    [Fact]
    public void Unknown_hosts_have_no_mirror() => Assert.Null(DownloadSources.ToMirror("https://cdn.modrinth.com/data/x.jar"));

    [Fact]
    public void Candidate_order_follows_preference()
    {
        const string url = "https://libraries.minecraft.net/a.jar";
        Assert.Equal(url, new DownloadSources(DownloadSourcePreference.Official).Candidates(url)[0]);
        Assert.StartsWith(DownloadSources.MirrorBase, new DownloadSources(DownloadSourcePreference.Mirror).Candidates(url)[0]);
        var auto = new DownloadSources(DownloadSourcePreference.Auto);
        Assert.Equal(url, auto.Candidates(url)[0]);
        auto.OfficialUnreachable = true;
        Assert.StartsWith(DownloadSources.MirrorBase, auto.Candidates(url)[0]);
    }
}
