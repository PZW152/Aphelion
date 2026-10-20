using System.Buffers;
using System.Diagnostics;
using System.Security.Cryptography;
using Aphelion.Core.Logging;

namespace Aphelion.Core.Net;

/// <summary>One file to fetch. <see cref="Sha1"/> and <see cref="Size"/> come from trusted metadata when known.</summary>
public sealed record DownloadItem(string Url, string Path, string? Sha1 = null, long Size = 0)
{
    /// <summary>Extra URLs to try after the mirror-expanded candidates of <see cref="Url"/>.</summary>
    public IReadOnlyList<string>? Fallbacks { get; init; }
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
/// Parallel, verified downloader. Files land atomically: bytes stream into <c>*.part</c>, are hashed while
/// written, and only a verified file is moved into place. A stalled connection is abandoned after
/// <see cref="StallTimeout"/> and the next candidate URL is tried.
/// </summary>
public sealed class Downloader(DownloadSources sources, int concurrency)
{
    public static readonly TimeSpan StallTimeout = TimeSpan.FromSeconds(20);
    public const int AttemptsPerCandidate = 2;

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
        using var reporter = new Timer(_ => progress?.Report(new(Volatile.Read(ref filesDone), total, Interlocked.Read(ref bytesDone), bytesTotal, meter.Rate)),
            null, 0, 200);

        await Parallel.ForEachAsync(unique, new ParallelOptions { MaxDegreeOfParallelism = Math.Max(1, concurrency), CancellationToken = ct },
            async (item, token) =>
            {
                try
                {
                    if (!await IsValidAsync(item, verify, token).ConfigureAwait(false))
                    {
                        await FetchAsync(item, n => { Interlocked.Add(ref bytesDone, n); meter.Add(n); }, token).ConfigureAwait(false);
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

        progress?.Report(new(filesDone, total, Interlocked.Read(ref bytesDone), bytesTotal, meter.Rate));
        if (!failures.IsEmpty) throw new DownloadFailedException(failures.ToList());
    }

    public Task DownloadAsync(DownloadItem item, CancellationToken ct) => RunAsync([item], null, ct);

    public static async Task<bool> IsValidAsync(DownloadItem item, VerifyMode mode, CancellationToken ct)
    {
        var info = new FileInfo(item.Path);
        if (!info.Exists) return false;
        if (item.Size > 0 && info.Length != item.Size) return false;
        if (item.Sha1 is null) return info.Length > 0;
        if (mode == VerifyMode.Size && item.Size > 0) return true;
        return string.Equals(await Hashing.Sha1FileAsync(item.Path, ct).ConfigureAwait(false), item.Sha1, StringComparison.OrdinalIgnoreCase);
    }

    private async Task FetchAsync(DownloadItem item, Action<long> onBytes, CancellationToken ct)
    {
        var candidates = sources.Candidates(item.Url).Concat(item.Fallbacks ?? []).Distinct().ToList();
        Exception? last = null;
        for (var c = 0; c < candidates.Count; c++)
        {
            for (var attempt = 0; attempt < AttemptsPerCandidate; attempt++)
            {
                ct.ThrowIfCancellationRequested();
                long counted = 0;
                try
                {
                    await FetchOnceAsync(candidates[c], item, n => { counted += n; onBytes(n); }, ct).ConfigureAwait(false);
                    return;
                }
                catch (OperationCanceledException) when (ct.IsCancellationRequested)
                {
                    throw;
                }
                catch (Exception e) when (e is HttpRequestException or IOException or TimeoutException or OperationCanceledException or IntegrityException)
                {
                    last = e;
                    onBytes(-counted); // roll back progress of the failed attempt
                    if (e is HttpStatusException { StatusCode: System.Net.HttpStatusCode.NotFound or System.Net.HttpStatusCode.Forbidden })
                        break; // a missing file will not appear on retry; try the next candidate
                    await Task.Delay(TimeSpan.FromMilliseconds(400 * (attempt + 1)), ct).ConfigureAwait(false);
                }
            }
            if (c == 0 && sources.Preference == Settings.DownloadSourcePreference.Auto && candidates.Count > 1 && last is not HttpStatusException)
                sources.OfficialUnreachable = true;
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
            long written = 0;
            await using (var input = await response.Content.ReadAsStreamAsync(stall.Token).ConfigureAwait(false))
            await using (var output = new FileStream(part, FileMode.Create, FileAccess.Write, FileShare.None, 81920, useAsync: true))
            {
                int read;
                while ((read = await input.ReadAsync(buffer.AsMemory(), stall.Token).ConfigureAwait(false)) > 0)
                {
                    stall.CancelAfter(StallTimeout);
                    await output.WriteAsync(buffer.AsMemory(0, read), ct).ConfigureAwait(false);
                    sha1.AppendData(buffer, 0, read);
                    written += read;
                    onBytes(read);
                }
            }

            if (item.Size > 0 && written != item.Size)
                throw new IntegrityException($"size {written} != expected {item.Size}");
            var actual = Convert.ToHexStringLower(sha1.GetHashAndReset());
            if (item.Sha1 is not null && !string.Equals(actual, item.Sha1, StringComparison.OrdinalIgnoreCase))
                throw new IntegrityException($"sha1 {actual} != expected {item.Sha1}");

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
    }

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

    public static async Task<string> Sha512FileAsync(string path, CancellationToken ct = default)
    {
        await using var fs = new FileStream(path, FileMode.Open, FileAccess.Read, FileShare.ReadWrite, 81920, useAsync: true);
        return Convert.ToHexStringLower(await SHA512.HashDataAsync(fs, ct).ConfigureAwait(false));
    }

    public static string Sha1(ReadOnlySpan<byte> data) => Convert.ToHexStringLower(SHA1.HashData(data));
}
