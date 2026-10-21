using System.Buffers;
using System.Diagnostics;
using System.Net;
using System.Net.Http.Headers;
using System.Security.Cryptography;
using Aphelion.Core.Logging;
using Microsoft.Win32.SafeHandles;

namespace Aphelion.Core.Net;

/// <summary>
/// One file to fetch. <see cref="Sha1"/> (or <see cref="Sha256"/>) and <see cref="Size"/> come from trusted metadata when known.
/// </summary>
public sealed record DownloadItem(string Url, string Path, string? Sha1 = null, long Size = 0)
{
    /// <summary>Extra URLs to try after the official alternates of <see cref="Url"/>.</summary>
    public IReadOnlyList<string>? Fallbacks { get; init; }

    /// <summary>Used where the publisher signs SHA-256 instead of SHA-1 (Aphelion's own updates).</summary>
    public string? Sha256 { get; init; }
}

public readonly record struct DownloadProgress(int FilesDone, int FilesTotal, long BytesDone, long BytesTotal, double BytesPerSecond);

public sealed class DownloadFailedException(IReadOnlyList<(DownloadItem Item, Exception Error)> failures)
    : Exception($"{failures.Count} file(s) failed to download; first: {System.IO.Path.GetFileName(failures[0].Item.Path)} — {failures[0].Error.Message}")
{
    public IReadOnlyList<(DownloadItem Item, Exception Error)> Failures { get; } = failures;
}

public enum VerifyMode
{
    /// <summary>Existing files are trusted when their size matches (fast pre-launch check).</summary>
    Size,

    /// <summary>Existing files are hashed (repair / install).</summary>
    Hash,
}

/// <summary>
/// Parallel, verified downloader that only uses the publishers' own servers. For each file, in order:
/// <list type="number">
/// <item>An identical file already on this PC (another game folder, the official launcher's folder) is copied,
///   when <c>donors</c> names one and its hash matches — no network at all.</item>
/// <item>Large files are fetched in parallel byte ranges over several connections; a dropped range resumes where it
///   stopped. This is what keeps long-distance links (high latency, packet loss) usable.</item>
/// <item>Otherwise one stream. A stalled connection is abandoned after <see cref="StallTimeout"/> and retried, then
///   the publisher's alternate hosts are tried (<see cref="OfficialSources"/>).</item>
/// </list>
/// Files land atomically: bytes go to <c>*.part</c>, are verified, and only then moved into place.
/// </summary>
public sealed class Downloader(int concurrency, Func<DownloadItem, IEnumerable<string>>? donors = null)
{
    public static readonly TimeSpan StallTimeout = TimeSpan.FromSeconds(20);
    public const int AttemptsPerCandidate = 2;

    /// <summary>Files at least this large are fetched in ranges.</summary>
    public const long SegmentThreshold = 8L * 1024 * 1024;

    public async Task RunAsync(IReadOnlyCollection<DownloadItem> items, IProgress<DownloadProgress>? progress, CancellationToken ct,
        VerifyMode verify = VerifyMode.Hash)
    {
        var unique = items.GroupBy(i => System.IO.Path.GetFullPath(i.Path), StringComparer.OrdinalIgnoreCase).Select(g => g.First()).ToList();
        var total = unique.Count;
        long bytesTotal = unique.Sum(i => Math.Max(0, i.Size));
        long bytesDone = 0;
        var filesDone = 0;
        var failures = new System.Collections.Concurrent.ConcurrentBag<(DownloadItem, Exception)>();
        var meter = new SpeedMeter();
        // Files without a published size add bytes the total did not know about; the total never reads below what arrived.
        DownloadProgress Snapshot()
        {
            var done = Interlocked.Read(ref bytesDone);
            return new(Volatile.Read(ref filesDone), total, done, Math.Max(bytesTotal, done), meter.Rate);
        }
        using var reporter = new Timer(_ => progress?.Report(Snapshot()), null, 0, 200);

        await Parallel.ForEachAsync(unique, new ParallelOptions { MaxDegreeOfParallelism = Math.Max(1, concurrency), CancellationToken = ct },
            async (item, token) =>
            {
                try
                {
                    if (!await IsValidAsync(item, verify, token).ConfigureAwait(false))
                    {
                        if (!await TryCopyFromDonorAsync(item, token).ConfigureAwait(false))
                            await FetchAsync(item, n => { Interlocked.Add(ref bytesDone, n); meter.Add(n); }, token).ConfigureAwait(false);
                        else if (item.Size > 0)
                            Interlocked.Add(ref bytesDone, item.Size);
                    }
                    else if (item.Size > 0)
                    {
                        Interlocked.Add(ref bytesDone, item.Size);
                    }
                }
                catch (OperationCanceledException) when (token.IsCancellationRequested)
                {
                    throw;
                }
                catch (Exception e)
                {
                    failures.Add((item, e));
                    Log.Warn($"Download failed: {Url.Describe(item.Url)} -> {item.Path}: {e.Message}");
                }
                finally
                {
                    Interlocked.Increment(ref filesDone);
                }
            }).ConfigureAwait(false);

        progress?.Report(Snapshot());
        if (!failures.IsEmpty) throw new DownloadFailedException(failures.ToList());
    }

    public Task DownloadAsync(DownloadItem item, CancellationToken ct) => RunAsync([item], null, ct);

    public static async Task<bool> IsValidAsync(DownloadItem item, VerifyMode mode, CancellationToken ct)
    {
        var info = new FileInfo(item.Path);
        if (!info.Exists) return false;
        if (item.Size > 0 && info.Length != item.Size) return false;
        if (item.Sha1 is null && item.Sha256 is null) return info.Length > 0;
        if (mode == VerifyMode.Size && item.Size > 0) return true;
        return await MatchesAsync(item.Path, item, ct).ConfigureAwait(false);
    }

    private static async Task<bool> MatchesAsync(string path, DownloadItem item, CancellationToken ct)
    {
        if (item.Sha256 is not null && !string.Equals(await Hashing.Sha256FileAsync(path, ct).ConfigureAwait(false), item.Sha256, StringComparison.OrdinalIgnoreCase))
            return false;
        if (item.Sha1 is not null && !string.Equals(await Hashing.Sha1FileAsync(path, ct).ConfigureAwait(false), item.Sha1, StringComparison.OrdinalIgnoreCase))
            return false;
        return true;
    }

    /// <summary>Copies a verified identical file from elsewhere on this PC. Only files with a published hash qualify.</summary>
    private async Task<bool> TryCopyFromDonorAsync(DownloadItem item, CancellationToken ct)
    {
        if (donors is null || (item.Sha1 is null && item.Sha256 is null)) return false;
        foreach (var source in donors(item))
        {
            try
            {
                var info = new FileInfo(source);
                if (!info.Exists || (item.Size > 0 && info.Length != item.Size)) continue;
                if (string.Equals(info.FullName, System.IO.Path.GetFullPath(item.Path), StringComparison.OrdinalIgnoreCase)) continue;
                if (!await MatchesAsync(source, item, ct).ConfigureAwait(false)) continue;
                Directory.CreateDirectory(System.IO.Path.GetDirectoryName(item.Path)!);
                var part = item.Path + ".part";
                File.Copy(source, part, overwrite: true);
                // The copy is checked again: the donor may have changed between the check and the copy.
                if (!await MatchesAsync(part, item, ct).ConfigureAwait(false)) { Storage.AtomicFile.TryDelete(part); continue; }
                File.Move(part, item.Path, overwrite: true);
                return true;
            }
            catch (Exception e) when (e is IOException or UnauthorizedAccessException)
            {
                Log.Warn($"Could not reuse {source}: {e.Message}");
            }
        }
        return false;
    }

    private static async Task FetchAsync(DownloadItem item, Action<long> onBytes, CancellationToken ct)
    {
        var candidates = OfficialSources.Candidates(item.Url).Concat(item.Fallbacks ?? []).Distinct().ToList();
        Exception? last = null;
        foreach (var candidate in candidates)
        {
            var ranged = item.Size >= SegmentThreshold;
            for (var attempt = 0; attempt < AttemptsPerCandidate; attempt++)
            {
                ct.ThrowIfCancellationRequested();
                long counted = 0;
                try
                {
                    void Count(long n) { Interlocked.Add(ref counted, n); onBytes(n); }
                    if (ranged) await FetchRangesAsync(candidate, item, Count, ct).ConfigureAwait(false);
                    else await FetchOnceAsync(candidate, item, Count, ct).ConfigureAwait(false);
                    return;
                }
                catch (OperationCanceledException) when (ct.IsCancellationRequested)
                {
                    throw;
                }
                catch (RangeUnsupportedException)
                {
                    onBytes(-Interlocked.Read(ref counted));
                    ranged = false; // this host sends whole files only; stream it, without spending a retry
                    attempt--;
                }
                catch (Exception e) when (e is HttpRequestException or IOException or TimeoutException or OperationCanceledException or IntegrityException)
                {
                    last = e;
                    onBytes(-Interlocked.Read(ref counted)); // roll back progress of the failed attempt
                    if (e is HttpStatusException { StatusCode: HttpStatusCode.NotFound or HttpStatusCode.Forbidden })
                        break; // a missing file will not appear on retry; try the next host
                    await Task.Delay(TimeSpan.FromMilliseconds(400 * (attempt + 1)), ct).ConfigureAwait(false);
                }
            }
        }
        throw last ?? new IOException("No download candidates");
    }

    private static async Task FetchOnceAsync(string url, DownloadItem item, Action<long> onBytes, CancellationToken ct)
    {
        Directory.CreateDirectory(System.IO.Path.GetDirectoryName(item.Path)!);
        var part = item.Path + ".part";
        using var stall = CancellationTokenSource.CreateLinkedTokenSource(ct);
        stall.CancelAfter(StallTimeout);

        using var response = await Http.Client.GetAsync(url, HttpCompletionOption.ResponseHeadersRead, stall.Token).ConfigureAwait(false);
        await Http.EnsureSuccessAsync(response, url).ConfigureAwait(false);

        var buffer = ArrayPool<byte>.Shared.Rent(81920);
        try
        {
            using var sha1 = IncrementalHash.CreateHash(HashAlgorithmName.SHA1);
            using var sha256 = IncrementalHash.CreateHash(HashAlgorithmName.SHA256);
            long written = 0;
            await using (var input = await response.Content.ReadAsStreamAsync(stall.Token).ConfigureAwait(false))
            await using (var output = new FileStream(part, FileMode.Create, FileAccess.Write, FileShare.None, 81920, useAsync: true))
            {
                int read;
                while ((read = await input.ReadAsync(buffer.AsMemory(), stall.Token).ConfigureAwait(false)) > 0)
                {
                    stall.CancelAfter(StallTimeout);
                    await output.WriteAsync(buffer.AsMemory(0, read), ct).ConfigureAwait(false);
                    if (item.Sha1 is not null) sha1.AppendData(buffer, 0, read);
                    if (item.Sha256 is not null) sha256.AppendData(buffer, 0, read);
                    written += read;
                    onBytes(read);
                }
            }

            if (item.Size > 0 && written != item.Size)
                throw new IntegrityException($"size {written} != expected {item.Size}");
            Check("sha1", item.Sha1, sha1);
            Check("sha256", item.Sha256, sha256);
            File.Move(part, item.Path, overwrite: true);
        }
        catch (OperationCanceledException) when (!ct.IsCancellationRequested)
        {
            throw new TimeoutException($"Connection stalled: {Url.Describe(url)}");
        }
        finally
        {
            ArrayPool<byte>.Shared.Return(buffer);
            Storage.AtomicFile.TryDelete(part);
        }

        static void Check(string name, string? expected, IncrementalHash hash)
        {
            if (expected is null) return;
            var actual = Convert.ToHexStringLower(hash.GetHashAndReset());
            if (!string.Equals(actual, expected, StringComparison.OrdinalIgnoreCase))
                throw new IntegrityException($"{name} {actual} != expected {expected}");
        }
    }

    /// <summary>4–8 ranges depending on size, each at least 2 MB.</summary>
    internal static int SegmentCount(long size) => (int)Math.Clamp(size / (8L * 1024 * 1024) + 3, 4, 8);

    /// <summary>
    /// Fetches the file as parallel byte ranges into one preallocated <c>.part</c> file. Each range retries on its own
    /// and resumes from the last byte it wrote, so one bad connection does not restart the whole file.
    /// </summary>
    private static async Task FetchRangesAsync(string url, DownloadItem item, Action<long> onBytes, CancellationToken ct)
    {
        Directory.CreateDirectory(System.IO.Path.GetDirectoryName(item.Path)!);
        var part = item.Path + ".part";
        try
        {
            using (var handle = File.OpenHandle(part, FileMode.Create, FileAccess.Write, FileShare.None, FileOptions.Asynchronous, item.Size))
            {
                var count = SegmentCount(item.Size);
                var step = item.Size / count;
                var ranges = Enumerable.Range(0, count).Select(i => (Start: i * step, End: i == count - 1 ? item.Size - 1 : (i + 1) * step - 1));
                await Task.WhenAll(ranges.Select(r => FetchRangeAsync(url, handle, r.Start, r.End, onBytes, ct))).ConfigureAwait(false);
            }
            if (new FileInfo(part).Length != item.Size) throw new IntegrityException("ranged download has the wrong size");
            if (!await MatchesAsync(part, item, ct).ConfigureAwait(false))
                throw new IntegrityException($"hash mismatch after ranged download of {Url.Describe(url)}");
            File.Move(part, item.Path, overwrite: true);
        }
        finally
        {
            Storage.AtomicFile.TryDelete(part);
        }
    }

    private static async Task FetchRangeAsync(string url, SafeFileHandle file, long start, long end, Action<long> onBytes, CancellationToken ct)
    {
        var position = start;
        var buffer = ArrayPool<byte>.Shared.Rent(81920);
        try
        {
            for (var attempt = 0; ; attempt++)
            {
                using var stall = CancellationTokenSource.CreateLinkedTokenSource(ct);
                stall.CancelAfter(StallTimeout);
                try
                {
                    using var request = new HttpRequestMessage(HttpMethod.Get, url);
                    request.Headers.Range = new RangeHeaderValue(position, end);
                    using var response = await Http.Client.SendAsync(request, HttpCompletionOption.ResponseHeadersRead, stall.Token).ConfigureAwait(false);
                    if (response.StatusCode == HttpStatusCode.OK) throw new RangeUnsupportedException();
                    await Http.EnsureSuccessAsync(response, url).ConfigureAwait(false);
                    if (response.Content.Headers.ContentRange?.From != position) throw new RangeUnsupportedException();

                    await using var input = await response.Content.ReadAsStreamAsync(stall.Token).ConfigureAwait(false);
                    int read;
                    while (position <= end && (read = await input.ReadAsync(buffer.AsMemory(0, (int)Math.Min(buffer.Length, end - position + 1)), stall.Token).ConfigureAwait(false)) > 0)
                    {
                        stall.CancelAfter(StallTimeout);
                        await RandomAccess.WriteAsync(file, buffer.AsMemory(0, read), position, ct).ConfigureAwait(false);
                        position += read;
                        onBytes(read);
                    }
                    if (position > end) return;
                    throw new IOException("range ended early");
                }
                catch (Exception e) when (e is HttpRequestException or IOException or OperationCanceledException && !ct.IsCancellationRequested
                                          && e is not HttpStatusException && e is not RangeUnsupportedException && attempt < 3)
                {
                    // Resume this range from the last byte written.
                    await Task.Delay(TimeSpan.FromMilliseconds(300 * (attempt + 1)), ct).ConfigureAwait(false);
                }
                catch (OperationCanceledException) when (!ct.IsCancellationRequested)
                {
                    throw new TimeoutException($"Connection stalled: {Url.Describe(url)}");
                }
            }
        }
        finally
        {
            ArrayPool<byte>.Shared.Return(buffer);
        }
    }

    private sealed class RangeUnsupportedException() : IOException("The server does not honour byte ranges");

    private sealed class SpeedMeter
    {
        private readonly Stopwatch _clock = Stopwatch.StartNew();
        private readonly Queue<(long Ms, long Bytes)> _window = new();
        private readonly Lock _gate = new();

        public void Add(long n)
        {
            lock (_gate)
            {
                _window.Enqueue((_clock.ElapsedMilliseconds, n));
                while (_window.Count > 0 && _clock.ElapsedMilliseconds - _window.Peek().Ms > 3000) _window.Dequeue();
            }
        }

        public double Rate
        {
            get
            {
                lock (_gate)
                {
                    if (_window.Count == 0) return 0;
                    var span = Math.Max(500, _clock.ElapsedMilliseconds - _window.Peek().Ms);
                    return Math.Max(0, _window.Sum(w => w.Bytes)) * 1000.0 / span;
                }
            }
        }
    }
}

public sealed class IntegrityException(string message) : IOException($"Integrity check failed: {message}");

/// <summary>SHA-1 is what Mojang, Forge and Modrinth publish for integrity checks; transport security is HTTPS.</summary>
[System.Diagnostics.CodeAnalysis.SuppressMessage("Security", "CA5350", Justification = "Integrity checksum mandated by upstream metadata, not a security boundary.")]
public static class Hashing
{
    public static async Task<string> Sha1FileAsync(string path, CancellationToken ct = default)
    {
        await using var fs = new FileStream(path, FileMode.Open, FileAccess.Read, FileShare.ReadWrite, 81920, useAsync: true);
        return Convert.ToHexStringLower(await SHA1.HashDataAsync(fs, ct).ConfigureAwait(false));
    }

    public static async Task<string> Sha256FileAsync(string path, CancellationToken ct = default)
    {
        await using var fs = new FileStream(path, FileMode.Open, FileAccess.Read, FileShare.ReadWrite, 81920, useAsync: true);
        return Convert.ToHexStringLower(await SHA256.HashDataAsync(fs, ct).ConfigureAwait(false));
    }

    public static async Task<string> Sha512FileAsync(string path, CancellationToken ct = default)
    {
        await using var fs = new FileStream(path, FileMode.Open, FileAccess.Read, FileShare.ReadWrite, 81920, useAsync: true);
        return Convert.ToHexStringLower(await SHA512.HashDataAsync(fs, ct).ConfigureAwait(false));
    }

    public static string Sha1(ReadOnlySpan<byte> data) => Convert.ToHexStringLower(SHA1.HashData(data));
}
