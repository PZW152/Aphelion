using System.Collections.Concurrent;
using System.Net;
using System.Net.Sockets;

namespace Aphelion.Core.Net;

/// <summary>
/// The publishers' CDNs answer DNS with several edge addresses. The first one is not always the best, and from some
/// networks some edges (or IPv6 as a whole) are unreachable or very slow; a plain connect would then wait for its
/// full timeout. Connections race instead, in the spirit of RFC 8305 ("Happy Eyeballs"): the address that won last
/// time for this host goes first, the others follow at a short stagger alternating IPv6 and IPv4, and the first
/// connection to succeed is used and remembered. The host name, certificate and content are unchanged — only the
/// choice among the publisher's own edges is.
/// </summary>
public static class EdgeConnector
{
    public static readonly TimeSpan Stagger = TimeSpan.FromMilliseconds(250);
    public const int MaxAttempts = 6;
    private static readonly TimeSpan Remember = TimeSpan.FromMinutes(10);
    private static readonly ConcurrentDictionary<string, (IPAddress Address, DateTime At)> Winners = new(StringComparer.OrdinalIgnoreCase);

    /// <summary>For <see cref="SocketsHttpHandler.ConnectCallback"/>.</summary>
    public static async ValueTask<Stream> ConnectCallback(SocketsHttpConnectionContext context, CancellationToken ct)
    {
        var socket = await ConnectAsync(context.DnsEndPoint.Host, context.DnsEndPoint.Port, ct).ConfigureAwait(false);
        return new NetworkStream(socket, ownsSocket: true);
    }

    public static async Task<Socket> ConnectAsync(string host, int port, CancellationToken ct)
    {
        IPAddress[] addresses = IPAddress.TryParse(host, out var literal)
            ? [literal]
            : await Dns.GetHostAddressesAsync(host, ct).ConfigureAwait(false);
        return await ConnectAsync(host, addresses, port, ct).ConfigureAwait(false);
    }

    internal static async Task<Socket> ConnectAsync(string host, IReadOnlyList<IPAddress> addresses, int port, CancellationToken ct)
    {
        if (addresses.Count == 0) throw new SocketException((int)SocketError.HostNotFound);

        var queue = new Queue<IPAddress>(Order(host, addresses).Take(MaxAttempts));
        using var race = CancellationTokenSource.CreateLinkedTokenSource(ct);
        var pending = new List<Task<Socket>>();
        Exception? last = null;
        try
        {
            while (queue.Count > 0 || pending.Count > 0)
            {
                ct.ThrowIfCancellationRequested();
                if (queue.Count > 0) pending.Add(ConnectOneAsync(queue.Dequeue(), port, race.Token));
                // Wait for a result, but start the next address after the stagger even if nothing finished yet.
                var stagger = queue.Count > 0 ? Task.Delay(Stagger, ct) : null;
                while (pending.Count > 0)
                {
                    var finished = await Task.WhenAny(stagger is null ? pending : pending.Append(stagger)).ConfigureAwait(false);
                    if (finished == stagger) break;
                    var attempt = (Task<Socket>)finished;
                    pending.Remove(attempt);
                    if (attempt.IsCompletedSuccessfully)
                    {
                        var socket = attempt.Result;
                        if (socket.RemoteEndPoint is IPEndPoint won) Winners[host] = (won.Address, DateTime.UtcNow);
                        return socket;
                    }
                    last = attempt.Exception?.InnerException ?? last;
                    if (queue.Count > 0) break; // failed fast: try the next address now
                }
            }
        }
        finally
        {
            race.Cancel();
            // Losers that still connect are closed.
            foreach (var t in pending)
                _ = t.ContinueWith(x => { if (x.IsCompletedSuccessfully) x.Result.Dispose(); }, TaskScheduler.Default);
        }
        ct.ThrowIfCancellationRequested();
        throw last ?? new SocketException((int)SocketError.HostUnreachable);
    }

    /// <summary>Last winner first, then the rest alternating address families, starting with the resolver's first family.</summary>
    internal static IEnumerable<IPAddress> Order(string host, IReadOnlyList<IPAddress> addresses)
    {
        var rest = addresses.ToList();
        if (Winners.TryGetValue(host, out var w) && DateTime.UtcNow - w.At < Remember && rest.Remove(w.Address))
            yield return w.Address;
        var first = rest.FirstOrDefault()?.AddressFamily;
        var a = new Queue<IPAddress>(rest.Where(x => x.AddressFamily == first));
        var b = new Queue<IPAddress>(rest.Where(x => x.AddressFamily != first));
        while (a.Count > 0 || b.Count > 0)
        {
            if (a.Count > 0) yield return a.Dequeue();
            if (b.Count > 0) yield return b.Dequeue();
        }
    }

    internal static void Forget() => Winners.Clear();

    private static async Task<Socket> ConnectOneAsync(IPAddress address, int port, CancellationToken ct)
    {
        var socket = new Socket(address.AddressFamily, SocketType.Stream, ProtocolType.Tcp) { NoDelay = true };
        try
        {
            await socket.ConnectAsync(address, port, ct).ConfigureAwait(false);
            return socket;
        }
        catch
        {
            socket.Dispose();
            throw;
        }
    }
}
