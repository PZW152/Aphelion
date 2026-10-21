using System.Diagnostics;
using System.Net;
using System.Net.Http.Headers;

namespace Aphelion.Core.Net;

/// <summary>One official host as seen from this network: name resolution, first byte, and a short throughput sample.</summary>
public sealed record HostCheck(string Key, string Host, bool Ok, int Addresses, double? ResolveMs, double? FirstByteMs, double? BytesPerSecond, string? Error);

/// <summary>
/// Settings › Download › Connection check. Each official host is probed in parallel with a small ranged request
/// (at most <see cref="SampleBytes"/>), so the check costs little traffic and finishes within <see cref="Timeout"/>.
/// </summary>
public static class NetworkCheck
{
    public const int SampleBytes = 2 * 1024 * 1024;
    public static readonly TimeSpan Timeout = TimeSpan.FromSeconds(15);

    public static Task<HostCheck[]> RunAsync(CancellationToken ct) =>
        Task.WhenAll(OfficialSources.Hosts.Select(h => CheckAsync(h.Key, h.Probe, ct)));

    public static async Task<HostCheck> CheckAsync(string key, string url, CancellationToken ct)
    {
        var host = new Uri(url).Host;
        double? resolveMs = null, firstByteMs = null;
        var addresses = 0;
        using var cts = CancellationTokenSource.CreateLinkedTokenSource(ct);
        cts.CancelAfter(Timeout);
        var clock = Stopwatch.StartNew();
        try
        {
            addresses = (await Dns.GetHostAddressesAsync(host, cts.Token).ConfigureAwait(false)).Length;
            resolveMs = clock.Elapsed.TotalMilliseconds;

            clock.Restart();
            using var request = new HttpRequestMessage(HttpMethod.Get, url);
            request.Headers.Range = new RangeHeaderValue(0, SampleBytes - 1);
            request.Headers.CacheControl = new CacheControlHeaderValue { NoCache = true };
            using var response = await Http.Client.SendAsync(request, HttpCompletionOption.ResponseHeadersRead, cts.Token).ConfigureAwait(false);
            await Http.EnsureSuccessAsync(response, url).ConfigureAwait(false);
            firstByteMs = clock.Elapsed.TotalMilliseconds;

            clock.Restart();
            long total = 0;
            var buffer = new byte[65536];
            await using var body = await response.Content.ReadAsStreamAsync(cts.Token).ConfigureAwait(false);
            int read;
            while (total < SampleBytes && (read = await body.ReadAsync(buffer, cts.Token).ConfigureAwait(false)) > 0) total += read;
            var seconds = clock.Elapsed.TotalSeconds;
            // Small responses arrive in one buffered burst and say nothing about throughput.
            double? rate = total >= 1024 * 1024 && seconds > 0 ? total / seconds : null;
            return new HostCheck(key, host, true, addresses, resolveMs, firstByteMs, rate, null);
        }
        catch (Exception e) when (e is HttpRequestException or IOException or OperationCanceledException or System.Net.Sockets.SocketException && !ct.IsCancellationRequested)
        {
            var reason = e switch
            {
                OperationCanceledException => "timeout",
                HttpStatusException s => $"http {(int?)s.StatusCode}",
                System.Net.Sockets.SocketException { SocketErrorCode: System.Net.Sockets.SocketError.HostNotFound } => "dns",
                HttpRequestException { InnerException: System.Net.Sockets.SocketException { SocketErrorCode: System.Net.Sockets.SocketError.HostNotFound } } => "dns",
                HttpRequestException { InnerException: System.Security.Authentication.AuthenticationException } => "tls",
                _ => "connect",
            };
            return new HostCheck(key, host, false, addresses, resolveMs, firstByteMs, null, reason);
        }
    }
}
