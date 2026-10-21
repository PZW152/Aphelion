using System.Security.Cryptography;
using System.Text;
using Aphelion.Core.Net;
using Aphelion.Core.Update;

namespace Aphelion.Core.Tests;

public class AppVersionTests
{
    [Theory]
    [InlineData("0.2.0", "0.2.1", -1)]
    [InlineData("0.10.0", "0.9.9", 1)]
    [InlineData("1.0.0-beta.1", "1.0.0", -1)]
    [InlineData("1.0.0-beta.2", "1.0.0-beta.1", 1)]
    [InlineData("v1.2.3", "1.2.3", 0)]
    [InlineData("0.2.0+a989f81", "0.2.0", 0)]
    [InlineData("2", "1.9.9", 1)]
    public void Versions_order_as_releases_do(string a, string b, int expected)
    {
        Assert.True(AppVersion.TryParse(a, out var x));
        Assert.True(AppVersion.TryParse(b, out var y));
        Assert.Equal(expected, Math.Sign(x.CompareTo(y)));
    }

    [Theory]
    [InlineData("")]
    [InlineData("latest")]
    [InlineData("1.2.3.4")]
    [InlineData("1.-2.3")]
    public void Nonsense_is_not_a_version(string text) => Assert.False(AppVersion.TryParse(text, out _));
}

public class UpdateServiceTests
{
    private static readonly ECDsa ReleaseKey = ECDsa.Create(ECCurve.NamedCurves.nistP256);

    private static UpdateManifest Manifest(string version, byte[] exe, string? url = null) => new()
    {
        Product = AppInfo.AppId,
        Version = version,
        Published = DateTimeOffset.Parse("2026-10-21T12:00:00Z", System.Globalization.CultureInfo.InvariantCulture),
        File = new UpdateFile { Name = "Aphelion.exe", Size = exe.Length, Sha256 = Convert.ToHexStringLower(SHA256.HashData(exe)), Urls = [url ?? "https://example.invalid/Aphelion.exe"] },
        Notes = new() { ["en"] = "Notes" },
    };

    private static (byte[] Manifest, byte[] Signature) Signed(UpdateManifest m, ECDsa? key = null)
    {
        var bytes = UpdateService.Serialize(m);
        return (bytes, Encoding.ASCII.GetBytes(UpdateService.Sign(bytes, key ?? ReleaseKey)));
    }

    [Fact]
    public void A_signed_manifest_verifies()
    {
        var (m, sig) = Signed(Manifest("9.0.0", [1, 2, 3]));
        Assert.Equal("9.0.0", UpdateService.Verify(m, sig, ReleaseKey).Version);
    }

    [Fact]
    public void Any_change_to_the_manifest_breaks_the_signature()
    {
        var (m, sig) = Signed(Manifest("9.0.0", [1, 2, 3]));
        var tampered = Encoding.UTF8.GetBytes(Encoding.UTF8.GetString(m).Replace("example.invalid", "evil.invalid", StringComparison.Ordinal));
        Assert.Equal("update.signature", Assert.Throws<AppException>(() => UpdateService.Verify(tampered, sig, ReleaseKey)).Code);
    }

    [Fact]
    public void A_manifest_signed_with_another_key_is_refused()
    {
        using var other = ECDsa.Create(ECCurve.NamedCurves.nistP256);
        var (m, sig) = Signed(Manifest("9.0.0", [1, 2, 3]), other);
        Assert.Equal("update.signature", Assert.Throws<AppException>(() => UpdateService.Verify(m, sig, ReleaseKey)).Code);
        Assert.Equal("update.signature", Assert.Throws<AppException>(() => UpdateService.Verify(m, "not base64!"u8.ToArray(), ReleaseKey)).Code);
    }

    [Fact]
    public void A_correctly_signed_manifest_for_another_product_or_with_plain_http_is_refused()
    {
        var foreign = Manifest("9.0.0", [1]);
        foreign.Product = Guid.NewGuid().ToString();
        var (a, sa) = Signed(foreign);
        Assert.Equal("update.manifest", Assert.Throws<AppException>(() => UpdateService.Verify(a, sa, ReleaseKey)).Code);

        var http = Manifest("9.0.0", [1], "http://example.invalid/Aphelion.exe");
        var (b, sb) = Signed(http);
        Assert.Equal("update.manifest", Assert.Throws<AppException>(() => UpdateService.Verify(b, sb, ReleaseKey)).Code);
    }

    [Theory]
    [InlineData("https://github.com/PZW152/Aphelion/releases/download/v1/Aphelion.exe", true)]
    [InlineData("http://localhost:8080/Aphelion.exe", true)]
    [InlineData("http://127.0.0.1/Aphelion.exe", true)]
    [InlineData("http://example.com/Aphelion.exe", false)]
    [InlineData("http://localhost.example.com/Aphelion.exe", false)]
    [InlineData("file:///C:/Aphelion.exe", false)]
    public void Only_https_or_this_machines_loopback(string url, bool allowed) => Assert.Equal(allowed, UpdateService.IsAllowedUrl(url));

    [Fact]
    public void The_compiled_in_release_key_is_a_valid_P256_public_key()
    {
        var parameters = UpdateKeys.Release.ExportParameters(includePrivateParameters: false);
        Assert.Equal(32, parameters.Q.X!.Length);
        Assert.Null(parameters.D);
    }

    [Fact]
    public async Task Check_offers_only_a_newer_version_and_download_verifies_the_executable()
    {
        using var tmp = new TempDir();
        var exe = new byte[300_000];
        new Random(7).NextBytes(exe);
        exe[0] = (byte)'M'; exe[1] = (byte)'Z';

        var files = new Dictionary<string, byte[]> { ["/Aphelion.exe"] = exe };
        using var server = new FileServer(files);
        var (m, sig) = Signed(Manifest("9.1.0", exe));
        files["/update.json"] = m;
        files["/update.json.sig"] = sig;

        var service = new UpdateService([server.Base], tmp.Path, () => new Downloader(2), ReleaseKey);
        var offered = await service.CheckAsync("0.2.0", default);
        Assert.Equal("9.1.0", offered!.Version);
        Assert.Null(await service.CheckAsync("9.1.0", default));
        Assert.Null(await service.CheckAsync("10.0.0", default));

        // Signed manifests must name https URLs; the local test server is http, so the verified manifest is redirected here.
        offered.File.Urls = [server.Base + "/Aphelion.exe"];
        var path = await service.DownloadAsync(offered, null, default);
        Assert.Equal(exe, await File.ReadAllBytesAsync(path));
        Assert.Equal(Path.Combine(tmp.Path, "update-9.1.0", "Aphelion.exe"), path);

        // A different file under the same name is rejected by its hash.
        files["/Aphelion.exe"] = exe.Reverse().ToArray();
        File.Delete(path);
        await Assert.ThrowsAsync<DownloadFailedException>(() => service.DownloadAsync(offered, null, default));
        Assert.False(File.Exists(path));
    }

    [Fact]
    public async Task A_channel_serving_a_forged_manifest_is_reported_not_trusted()
    {
        using var tmp = new TempDir();
        using var forger = ECDsa.Create(ECCurve.NamedCurves.nistP256);
        var (m, sig) = Signed(Manifest("9.9.9", [1, 2, 3]), forger);
        using var server = new FileServer(new Dictionary<string, byte[]> { ["/update.json"] = m, ["/update.json.sig"] = sig });
        var service = new UpdateService([server.Base], tmp.Path, () => new Downloader(1), ReleaseKey);
        Assert.Equal("update.signature", (await Assert.ThrowsAsync<AppException>(() => service.CheckAsync("0.2.0", default))).Code);
    }
}
