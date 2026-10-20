using System.Diagnostics;
using System.Text;
using Aphelion.Core.Diagnostics;
using Aphelion.Core.Logging;

namespace Aphelion.Core.Launch;

public enum SessionState { Starting, Running, Exited }

public sealed record GameExit(int ExitCode, bool KilledByUser, TimeSpan Duration, CrashAnalysis? Crash);

/// <summary>A running game process: captures and parses its output, and reports how it ended.</summary>
public sealed class GameSession : IDisposable
{
    public const int MaxBufferedLines = 20_000;

    private readonly Process _process;
    private readonly GameLogParser _parser = new();
    private readonly LinkedList<GameLogLine> _lines = new();
    private readonly Lock _gate = new();
    private readonly TaskCompletionSource<GameExit> _exit = new(TaskCreationOptions.RunContinuationsAsynchronously);
    private readonly Action<GameSession, GameExit> _onExit;
    private bool _killed;

    public string Id { get; } = Guid.NewGuid().ToString("N")[..12];
    public string InstanceId { get; }
    public string InstanceName { get; }
    public string RootPath { get; }
    public string GameDirectory { get; }
    public DateTime StartedAt { get; } = DateTime.Now;
    public SessionState State { get; private set; } = SessionState.Starting;
    public int ProcessId => _process.Id;
    public int Warnings { get; private set; }
    public int Errors { get; private set; }
    public Task<GameExit> Completion => _exit.Task;

    public event Action<GameSession, GameLogLine>? LineReceived;
    public event Action<GameSession>? StateChanged;

    internal GameSession(Process process, string instanceId, string instanceName, string rootPath, string gameDirectory, Action<GameSession, GameExit> onExit)
    {
        _process = process;
        RootPath = rootPath;
        InstanceId = instanceId;
        InstanceName = instanceName;
        GameDirectory = gameDirectory;
        _onExit = onExit;
    }

    internal void Start()
    {
        _process.EnableRaisingEvents = true;
        _process.OutputDataReceived += (_, e) => OnRaw(e.Data);
        _process.ErrorDataReceived += (_, e) => OnRaw(e.Data);
        _process.Exited += (_, _) => _ = Task.Run(FinishAsync);
        _process.BeginOutputReadLine();
        _process.BeginErrorReadLine();
        _ = Task.Run(WatchForWindowAsync);
    }

    public IReadOnlyList<GameLogLine> Snapshot(long afterSequence = 0)
    {
        lock (_gate) return _lines.Where(l => l.Sequence > afterSequence).ToList();
    }

    public long MemoryBytes
    {
        get
        {
            try { _process.Refresh(); return _process.HasExited ? 0 : _process.WorkingSet64; }
            catch (InvalidOperationException) { return 0; }
        }
    }

    public void Kill()
    {
        _killed = true;
        try { _process.Kill(entireProcessTree: true); }
        catch (InvalidOperationException) { }
        catch (System.ComponentModel.Win32Exception e) { Log.Warn("Could not stop the game process", e); }
    }

    private void OnRaw(string? data)
    {
        if (data is null) return;
        foreach (var line in _parser.Feed(data))
        {
            lock (_gate)
            {
                _lines.AddLast(line);
                if (_lines.Count > MaxBufferedLines) _lines.RemoveFirst();
                if (line.Level == GameLogLevel.Warn) Warnings++;
                else if (line.Level >= GameLogLevel.Error) Errors++;
            }
            if (State == SessionState.Starting && IsReadySignal(line.Message)) MarkRunning();
            LineReceived?.Invoke(this, line);
        }
    }

    private static bool IsReadySignal(string message) =>
        message.StartsWith("Setting user:", StringComparison.Ordinal)
        || message.Contains("Sound engine started", StringComparison.Ordinal)
        || message.StartsWith("Backend library:", StringComparison.Ordinal)
        || message.StartsWith("LWJGL Version:", StringComparison.Ordinal);

    private void MarkRunning()
    {
        if (State != SessionState.Starting) return;
        State = SessionState.Running;
        StateChanged?.Invoke(this);
    }

    private async Task WatchForWindowAsync()
    {
        // The game window appearing is the most reliable "running" signal; log lines are the fallback.
        for (var i = 0; i < 600 && State == SessionState.Starting; i++)
        {
            await Task.Delay(500).ConfigureAwait(false);
            try
            {
                _process.Refresh();
                if (_process.HasExited) return;
                if (_process.MainWindowHandle != IntPtr.Zero) { MarkRunning(); return; }
            }
            catch (InvalidOperationException) { return; }
        }
        MarkRunning();
    }

    private async Task FinishAsync()
    {
        try { await _process.WaitForExitAsync().ConfigureAwait(false); } catch (InvalidOperationException) { }
        int code;
        try { code = _process.ExitCode; } catch (InvalidOperationException) { code = -1; }
        var duration = DateTime.Now - StartedAt;
        State = SessionState.Exited;

        CrashAnalysis? crash = null;
        // 0 is a normal exit; Minecraft also exits with 130/-1073741510 when the console window is closed (Ctrl+C).
        if (!_killed && code != 0 && code != -1073741510)
        {
            try
            {
                var sources = new List<CrashSource>();
                var output = new StringBuilder();
                lock (_gate) foreach (var l in _lines.TakeLast(4000)) output.AppendLine(l.Message);
                sources.Add(new CrashSource("output", output.ToString()));
                sources.AddRange(CrashAnalyzer.CollectFiles(GameDirectory, StartedAt.AddSeconds(-5)));
                crash = CrashAnalyzer.Analyze(sources, code);
                Log.Info($"Crash analysis for {InstanceId}: {string.Join(", ", crash.Findings.Select(f => f.Code))}");
            }
            catch (Exception e)
            {
                Log.Error("Crash analysis failed", e);
            }
        }

        var exit = new GameExit(code, _killed, duration, crash);
        Log.Info($"Game {InstanceId} exited with {code} after {duration:hh\\:mm\\:ss}");
        try { _onExit(this, exit); }
        catch (Exception e) { Log.Error("Post-exit handling failed", e); }
        StateChanged?.Invoke(this);
        _exit.TrySetResult(exit);
    }

    public void Dispose() => _process.Dispose();
}
