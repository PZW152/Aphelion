using System.Net;
using System.Net.Sockets;
using System.Security.Cryptography;
using System.Text.Json.Nodes;
using Aphelion.Core.Net;
using Aphelion.Core.Settings;

namespace Aphelion.Core.Tests;

/// <summary>A local HTTP server for one file, with switches for the behaviours real CDNs show.</summary>
public sealed class FileServer : IDisposable
{
    private readonly HttpListener _listener = new();
    private readonly Dictionary<string, byte[]> _files;
    private int _requests;
    private int _drops;

    public string Url { get; }
    public string Base { get; }
    public bool Ranges { get; init; } = true;
    /// <summary>The first N responses stop after half their bytes, like a dropped connection.</summary>
    public int DropFirst { get; init; }
    public int Requests => Volatile.Read(ref _requests);
    private int _rangeRequests;
    public int RangeRequests => Volatile.Read(ref _rangeRequests);

    public FileServer(byte[] body) : this(new Dictionary<string, byte[]> { ["/file.bin"] = body }) { }

    /// <summary>Serves several paths; <see cref="Url"/> is then the base address.</summary>
    public FileServer(Dictionary<string, byte[]> files)
    {
        _files = files;
        var port = FreePort();
        Base = $"http://localhost:{port}";
        Url = files.Count == 1 ? Base + files.Keys.First() : Base;
        _listener.Prefixes.Add($"http://localhost:{port}/");
        _listener.Start();
        _ = Task.Run(LoopAsync);
    }

    private static int FreePort()
    {
        var l = new TcpListener(IPAddress.Loopback, 0);
        l.Start();
        var port = ((IPEndPoint)l.LocalEndpoint).Port;
        l.Stop();
        return port;
    }

    private async Task LoopAsync()
    {
        while (_listener.IsListening)
        {
            HttpListenerContext ctx;
            try { ctx = await _listener.GetContextAsync(); }
            catch (Exception e) when (e is HttpListenerException or ObjectDisposedException) { return; }
            _ = Task.Run(() => Serve(ctx));
        }
    }

    private async Task Serve(HttpListenerContext ctx)
    {
        Interlocked.Increment(ref _requests);
        if (!_files.TryGetValue(ctx.Request.Url!.AbsolutePath, out var body))
        {
            ctx.Response.StatusCode = 404;
            ctx.Response.Close();
            return;
        }
        long start = 0, end = body.Length - 1;
        var range = ctx.Request.Headers["Range"];
        var partial = Ranges && range is not null && range.StartsWith("bytes=", StringComparison.Ordinal);
        if (partial)
        {
            Interlocked.Increment(ref _rangeRequests);
            var parts = range![6..].Split('-');
            start = long.Parse(parts[0]);
            if (parts[1].Length > 0) end = Math.Min(end, long.Parse(parts[1]));
            ctx.Response.StatusCode = 206;
            ctx.Response.AddHeader("Content-Range", $"bytes {start}-{end}/{body.Length}");
        }
        var length = end - start + 1;
        ctx.Response.ContentLength64 = length;
        try
        {
            if (Interlocked.Increment(ref _drops) <= DropFirst)
            {
                await ctx.Response.OutputStream.WriteAsync(body.AsMemory((int)start, (int)(length / 2)));
                await ctx.Response.OutputStream.FlushAsync();
                ctx.Response.Abort();
                return;
            }
            await ctx.Response.OutputStream.WriteAsync(body.AsMemory((int)start, (int)length));
            ctx.Response.Close();
        }
        catch (Exception e) when (e is HttpListenerException or IOException or ObjectDisposedException) { }
    }

    public void Dispose()
    {
        _listener.Stop();
        _listener.Close();
    }
}

public class DownloaderTests
{
    private static byte[] Random(int size)
    {
        var data = new byte[size];
        new System.Random(size).NextBytes(data);
        return data;
    }

    private static string Sha1(byte[] data) => Hashing.Sha1(data);

    [Fact]
    public async Task Large_files_arrive_as_parallel_ranges_and_are_verified()
    {
        using var tmp = new TempDir();
        var body = Random(20 * 1024 * 1024);
        using var server = new FileServer(body);
        var target = Path.Combine(tmp.Path, "big.jar");

        await new Downloader(4).RunAsync([new DownloadItem(server.Url, target, Sha1(body), body.Length)], null, default);

        Assert.Equal(body, await File.ReadAllBytesAsync(target));
        Assert.Equal(Downloader.SegmentCount(body.Length), server.RangeRequests);
        Assert.False(File.Exists(target + ".part"));
    }

    [Fact]
    public async Task A_dropped_range_resumes_from_where_it_stopped()
    {
        using var tmp = new TempDir();
        var body = Random(12 * 1024 * 1024);
        using var server = new FileServer(body) { DropFirst = 2 };
        var target = Path.Combine(tmp.Path, "big.jar");

        await new Downloader(4).RunAsync([new DownloadItem(server.Url, target, Sha1(body), body.Length)], null, default);

        Assert.Equal(body, await File.ReadAllBytesAsync(target));
        // Two drops, each resumed by one more range request; nothing restarted from zero.
        Assert.Equal(Downloader.SegmentCount(body.Length) + 2, server.RangeRequests);
    }

    [Fact]
    public async Task A_host_without_ranges_is_streamed_whole()
    {
        using var tmp = new TempDir();
        var body = Random(9 * 1024 * 1024);
        using var server = new FileServer(body) { Ranges = false };
        var target = Path.Combine(tmp.Path, "big.jar");

        await new Downloader(4).RunAsync([new DownloadItem(server.Url, target, Sha1(body), body.Length)], null, default);

        Assert.Equal(body, await File.ReadAllBytesAsync(target));
    }

    [Fact]
    public async Task A_wrong_hash_is_never_moved_into_place()
    {
        using var tmp = new TempDir();
        var body = Random(64 * 1024);
        using var server = new FileServer(body);
        var target = Path.Combine(tmp.Path, "small.jar");

        await Assert.ThrowsAsync<DownloadFailedException>(() =>
            new Downloader(1).RunAsync([new DownloadItem(server.Url, target, new string('0', 40), body.Length)], null, default));
        Assert.False(File.Exists(target));
        Assert.False(File.Exists(target + ".part"));
    }

    [Fact]
    public async Task Sha256_is_checked_when_given()
    {
        using var tmp = new TempDir();
        var body = Random(64 * 1024);
        using var server = new FileServer(body);
        var target = Path.Combine(tmp.Path, "update.exe");
        var good = Convert.ToHexStringLower(SHA256.HashData(body));

        await new Downloader(1).RunAsync([new DownloadItem(server.Url, target, Size: body.Length) { Sha256 = good }], null, default);
        Assert.Equal(body, await File.ReadAllBytesAsync(target));

        File.Delete(target);
        await Assert.ThrowsAsync<DownloadFailedException>(() =>
            new Downloader(1).RunAsync([new DownloadItem(server.Url, target, Size: body.Length) { Sha256 = new string('a', 64) }], null, default));
        Assert.False(File.Exists(target));
    }

    [Fact]
    public async Task A_matching_file_from_another_game_folder_is_copied_instead_of_downloaded()
    {
        using var tmp = new TempDir();
        var body = Random(32 * 1024);
        var hash = Sha1(body);
        using var server = new FileServer(body);
        var mine = Path.Combine(tmp.Path, "Aphelion");
        var other = Path.Combine(tmp.Path, "OtherLauncher");
        var donorFile = Path.Combine(other, "assets", "objects", hash[..2], hash);
        Directory.CreateDirectory(Path.GetDirectoryName(donorFile)!);
        await File.WriteAllBytesAsync(donorFile, body);
        var donors = new LocalDonors(() => [mine, other]);
        var target = Path.Combine(mine, "assets", "objects", hash[..2], hash);

        await new Downloader(2, donors.For).RunAsync([new DownloadItem(server.Url, target, hash, body.Length)], null, default);

        Assert.Equal(body, await File.ReadAllBytesAsync(target));
        Assert.Equal(0, server.Requests);
    }

    [Fact]
    public async Task A_different_file_in_another_game_folder_is_not_trusted()
    {
        using var tmp = new TempDir();
        var body = Random(32 * 1024);
        var hash = Sha1(body);
        using var server = new FileServer(body);
        var mine = Path.Combine(tmp.Path, "Aphelion");
        var other = Path.Combine(tmp.Path, "OtherLauncher");
        var donorFile = Path.Combine(other, "libraries", "a", "b.jar");
        Directory.CreateDirectory(Path.GetDirectoryName(donorFile)!);
        await File.WriteAllBytesAsync(donorFile, body.Reverse().ToArray()); // same size, different content
        var donors = new LocalDonors(() => [mine, other]);
        var target = Path.Combine(mine, "libraries", "a", "b.jar");

        await new Downloader(2, donors.For).RunAsync([new DownloadItem(server.Url, target, hash, body.Length)], null, default);

        Assert.Equal(body, await File.ReadAllBytesAsync(target));
        Assert.Equal(1, server.Requests);
    }

    [Fact]
    public void Donors_only_cover_shared_folders_inside_known_roots()
    {
        var donors = new LocalDonors(() => [@"C:\A", @"D:\B\", @"C:\A"]);
        Assert.Equal([@"D:\B\assets\objects\ab\abcd"], donors.For(new DownloadItem("u", @"C:\A\assets\objects\ab\abcd")));
        Assert.Empty(donors.For(new DownloadItem("u", @"C:\A\versions\x\x.jar")));
        Assert.Empty(donors.For(new DownloadItem("u", @"E:\elsewhere\libraries\a.jar")));
    }
}

public class OfficialSourcesTests
{
    [Fact]
    public void Only_the_publishers_own_hosts_are_ever_tried()
    {
        Assert.Equal(
            ["https://piston-meta.mojang.com/mc/game/version_manifest_v2.json", "https://launchermeta.mojang.com/mc/game/version_manifest_v2.json"],
            OfficialSources.Candidates("https://piston-meta.mojang.com/mc/game/version_manifest_v2.json"));
        Assert.Equal(["https://libraries.minecraft.net/a/b.jar"], OfficialSources.Candidates("https://libraries.minecraft.net/a/b.jar"));
        Assert.Equal("https://meta2.fabricmc.net/v2/versions", OfficialSources.Candidates("https://meta.fabricmc.net/v2/versions")[1]);
        Assert.All(OfficialSources.Hosts, h => Assert.DoesNotContain("bmclapi", h.Probe, StringComparison.OrdinalIgnoreCase));
    }

    [Fact]
    public void The_old_mirror_preference_is_dropped_from_settings()
    {
        var node = JsonNode.Parse("""{"schema":1,"download":{"source":"mirror","concurrency":12}}""")!.AsObject();
        SettingsStore.Migrate(node, 1);
        Assert.Equal(2, node["schema"]!.GetValue<int>());
        Assert.Null(node["download"]!["source"]);
        Assert.Equal(12, node["download"]!["concurrency"]!.GetValue<int>());
    }
}

public class EdgeConnectorTests
{
    [Fact]
    public void Addresses_alternate_families_and_the_last_winner_goes_first()
    {
        EdgeConnector.Forget();
        IPAddress[] addresses = [IPAddress.Parse("2001:db8::1"), IPAddress.Parse("2001:db8::2"), IPAddress.Parse("192.0.2.1"), IPAddress.Parse("192.0.2.2")];
        Assert.Equal(["2001:db8::1", "192.0.2.1", "2001:db8::2", "192.0.2.2"], EdgeConnector.Order("cdn.test", addresses).Select(a => a.ToString()));
    }

    [Fact]
    public async Task An_unreachable_address_does_not_hold_up_a_working_one()
    {
        EdgeConnector.Forget();
        // "localhost" resolves to ::1 and 127.0.0.1; only IPv4 listens, so the IPv6 attempt must lose the race.
        var listener = new TcpListener(IPAddress.Loopback, 0);
        listener.Start();
        try
        {
            var port = ((IPEndPoint)listener.LocalEndpoint).Port;
            var accept = listener.AcceptSocketAsync();
            using var socket = await EdgeConnector.ConnectAsync("localhost", port, default);
            Assert.True(socket.Connected);
            Assert.Equal(IPAddress.Loopback, ((IPEndPoint)socket.RemoteEndPoint!).Address);
            using var server = await accept;
            Assert.Equal(IPAddress.Loopback, EdgeConnector.Order("localhost", [IPAddress.IPv6Loopback, IPAddress.Loopback]).First());
        }
        finally
        {
            listener.Stop();
        }
    }

    [Fact]
    public async Task A_silent_edge_is_overtaken_after_the_stagger_not_after_a_timeout()
    {
        EdgeConnector.Forget();
        var listener = new TcpListener(IPAddress.Loopback, 0);
        listener.Start();
        try
        {
            var port = ((IPEndPoint)listener.LocalEndpoint).Port;
            // 192.0.2.1 (TEST-NET-1) is never routed: a connect to it just waits, like a blackholed CDN edge.
            var clock = System.Diagnostics.Stopwatch.StartNew();
            using var socket = await EdgeConnector.ConnectAsync("edge.test", [IPAddress.Parse("192.0.2.1"), IPAddress.Loopback], port, default);
            Assert.Equal(IPAddress.Loopback, ((IPEndPoint)socket.RemoteEndPoint!).Address);
            Assert.True(clock.Elapsed < TimeSpan.FromSeconds(3), $"took {clock.Elapsed}");
            Assert.Equal(IPAddress.Loopback, EdgeConnector.Order("edge.test", [IPAddress.Parse("192.0.2.1"), IPAddress.Loopback]).First());
        }
        finally
        {
            listener.Stop();
        }
    }

    [Fact]
    public async Task Nothing_listening_fails_instead_of_hanging()
    {
        var port = ((Func<int>)(() => { var l = new TcpListener(IPAddress.Loopback, 0); l.Start(); var p = ((IPEndPoint)l.LocalEndpoint).Port; l.Stop(); return p; }))();
        await Assert.ThrowsAnyAsync<SocketException>(() => EdgeConnector.ConnectAsync("127.0.0.1", port, default));
    }
}
