using System.Collections.Concurrent;
using Aphelion.Core.Install;
using Aphelion.Core.Logging;

namespace Aphelion.Core.Tasks;

public enum TaskStatus { Running, Succeeded, Failed, Cancelled }

/// <summary>Snapshot of a background operation, as shown in the UI's task list.</summary>
public sealed record TaskSnapshot(
    string Id, string Kind, string Title, TaskStatus Status, string? Stage, double? Fraction, string? Detail,
    long BytesDone, long BytesTotal, double BytesPerSecond, int FilesDone, int FilesTotal,
    string? ErrorCode, string? ErrorMessage, IReadOnlyDictionary<string, string>? ErrorArgs,
    DateTimeOffset Started, DateTimeOffset? Finished, object? Result);

/// <summary>Runs long operations in the background with progress, cancellation and a short history.</summary>
public sealed class TaskHub
{
    public const int HistoryLimit = 30;

    private readonly ConcurrentDictionary<string, Entry> _tasks = new();

    public event Action<TaskSnapshot>? Changed;

    public IReadOnlyList<TaskSnapshot> All => _tasks.Values.Select(e => e.Snapshot).OrderByDescending(t => t.Started).ToList();

    public TaskSnapshot? Get(string id) => _tasks.TryGetValue(id, out var e) ? e.Snapshot : null;

    public string Start(string kind, string title, Func<IProgress<StageProgress>, CancellationToken, Task<object?>> work)
    {
        var entry = new Entry(Guid.NewGuid().ToString("N")[..12], kind, title);
        _tasks[entry.Id] = entry;
        Prune();
        Publish(entry);
        var progress = new InlineProgress<StageProgress>(p =>
        {
            entry.Apply(p);
            Publish(entry);
        });
        _ = Task.Run(async () =>
        {
            try
            {
                var result = await work(progress, entry.Cancellation.Token).ConfigureAwait(false);
                entry.Finish(TaskStatus.Succeeded, null, result);
            }
            catch (OperationCanceledException) when (entry.Cancellation.IsCancellationRequested)
            {
                entry.Finish(TaskStatus.Cancelled, null, null);
            }
            catch (Exception e)
            {
                Log.Error($"Task '{title}' failed", e);
                entry.Finish(TaskStatus.Failed, e, null);
            }
            Publish(entry);
        });
        return entry.Id;
    }

    public bool Cancel(string id)
    {
        if (!_tasks.TryGetValue(id, out var e) || e.Snapshot.Status != TaskStatus.Running) return false;
        e.Cancellation.Cancel();
        return true;
    }

    public void Dismiss(string id)
    {
        if (_tasks.TryGetValue(id, out var e) && e.Snapshot.Status != TaskStatus.Running) _tasks.TryRemove(id, out _);
    }

    public Task<TaskSnapshot> WaitAsync(string id, CancellationToken ct = default)
    {
        if (!_tasks.TryGetValue(id, out var e)) throw new KeyNotFoundException(id);
        return e.Done.Task.WaitAsync(ct);
    }

    private void Publish(Entry e) => Changed?.Invoke(e.Snapshot);

    private void Prune()
    {
        var finished = _tasks.Values.Where(e => e.Snapshot.Status != TaskStatus.Running).OrderByDescending(e => e.Snapshot.Started).Skip(HistoryLimit).ToList();
        foreach (var e in finished) _tasks.TryRemove(e.Id, out _);
    }

    private sealed class Entry(string id, string kind, string title)
    {
        private readonly Lock _gate = new();
        public string Id { get; } = id;
        public CancellationTokenSource Cancellation { get; } = new();
        public TaskCompletionSource<TaskSnapshot> Done { get; } = new(TaskCreationOptions.RunContinuationsAsynchronously);

        private TaskSnapshot _snapshot = new(id, kind, title, TaskStatus.Running, null, null, null, 0, 0, 0, 0, 0, null, null, null,
            DateTimeOffset.Now, null, null);

        public TaskSnapshot Snapshot
        {
            get { lock (_gate) return _snapshot; }
        }

        public void Apply(StageProgress p)
        {
            lock (_gate)
            {
                _snapshot = _snapshot with
                {
                    Stage = p.Stage,
                    Fraction = p.Fraction,
                    Detail = p.Detail ?? (p.Stage == _snapshot.Stage ? _snapshot.Detail : null),
                    BytesDone = p.Download?.BytesDone ?? 0,
                    BytesTotal = p.Download?.BytesTotal ?? 0,
                    BytesPerSecond = p.Download?.BytesPerSecond ?? 0,
                    FilesDone = p.Download?.FilesDone ?? 0,
                    FilesTotal = p.Download?.FilesTotal ?? 0,
                };
            }
        }

        public void Finish(TaskStatus status, Exception? error, object? result)
        {
            lock (_gate)
            {
                var app = error as AppException ?? error?.InnerException as AppException;
                _snapshot = _snapshot with
                {
                    Status = status,
                    Fraction = status == TaskStatus.Succeeded ? 1 : _snapshot.Fraction,
                    ErrorCode = error is null ? null : app?.Code ?? ErrorCodes.Classify(error),
                    ErrorMessage = error is null ? null : Log.Redact(error.Message),
                    ErrorArgs = app?.Args,
                    Finished = DateTimeOffset.Now,
                    Result = result,
                };
                Done.TrySetResult(_snapshot);
            }
            // The token source has no timer, so it holds no resources; it is not disposed so a late Cancel() stays harmless.
        }
    }
}

/// <summary>Maps unexpected exceptions to stable error codes for the UI.</summary>
public static class ErrorCodes
{
    public static string Classify(Exception e) => e switch
    {
        AppException a => a.Code,
        Net.DownloadFailedException => "network.download",
        Net.IntegrityException => "network.integrity",
        HttpRequestException => "network.http",
        TimeoutException => "network.timeout",
        UnauthorizedAccessException => "io.denied",
        IOException io when io.HResult is unchecked((int)0x80070070) or unchecked((int)0x80070027) => "io.diskFull",
        IOException => "io.error",
        System.Text.Json.JsonException => "data.invalid",
        _ => "unexpected",
    };
}
