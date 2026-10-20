using System.Collections.Concurrent;
using System.Text;
using System.Text.RegularExpressions;

namespace Aphelion.Core.Logging;

public enum LogLevel { Debug, Info, Warn, Error }

/// <summary>
/// Minimal process-wide logger: one file per session under logs/, the newest <see cref="KeepFiles"/> kept.
/// Every line passes through <see cref="Redact"/> so tokens and passwords never reach disk.
/// </summary>
public static partial class Log
{
    public const int KeepFiles = 10;

    private static readonly BlockingCollection<string> Queue = new(new ConcurrentQueue<string>(), 10_000);
    private static StreamWriter? _writer;
    private static Thread? _thread;

    public static string? CurrentFile { get; private set; }
    public static event Action<LogLevel, string>? Written;

    public static void Initialize(string logDirectory)
    {
        if (_writer is not null) return;
        Directory.CreateDirectory(logDirectory);
        Rotate(logDirectory);
        CurrentFile = Path.Combine(logDirectory, $"aphelion-{DateTime.Now.ToString("yyyyMMdd-HHmmss", System.Globalization.CultureInfo.InvariantCulture)}-{Environment.ProcessId}.log");
        _writer = new StreamWriter(new FileStream(CurrentFile, FileMode.Create, FileAccess.Write, FileShare.ReadWrite), new UTF8Encoding(false))
        {
            AutoFlush = false,
        };
        _thread = new Thread(Pump) { IsBackground = true, Name = "log-writer" };
        _thread.Start();
        Info($"{AppInfo.Name} {AppInfo.Version} on {Environment.OSVersion} ({System.Runtime.InteropServices.RuntimeInformation.OSArchitecture}), .NET {Environment.Version}");
    }

    public static void Debug(string message) => Write(LogLevel.Debug, message);
    public static void Info(string message) => Write(LogLevel.Info, message);
    public static void Warn(string message, Exception? e = null) => Write(LogLevel.Warn, e is null ? message : $"{message}: {e}");
    public static void Error(string message, Exception? e = null) => Write(LogLevel.Error, e is null ? message : $"{message}: {e}");

    public static void Write(LogLevel level, string message)
    {
        var line = $"{DateTime.Now:HH:mm:ss.fff} [{level.ToString().ToUpperInvariant(),-5}] [{Environment.CurrentManagedThreadId,3}] {Redact(message)}";
        if (_writer is not null) Queue.TryAdd(line);
        System.Diagnostics.Debug.WriteLine(line);
        Written?.Invoke(level, line);
    }

    public static void Shutdown()
    {
        Queue.CompleteAdding();
        _thread?.Join(TimeSpan.FromSeconds(2));
        _writer?.Dispose();
    }

    private static void Pump()
    {
        foreach (var line in Queue.GetConsumingEnumerable())
        {
            try
            {
                _writer!.WriteLine(line);
                if (Queue.Count == 0) _writer.Flush();
            }
            catch (IOException) { }
        }
    }

    private static void Rotate(string dir)
    {
        try
        {
            foreach (var old in new DirectoryInfo(dir).GetFiles("aphelion-*.log").OrderByDescending(f => f.CreationTimeUtc).Skip(KeepFiles - 1))
                old.Delete();
        }
        catch (IOException) { }
        catch (UnauthorizedAccessException) { }
    }

    /// <summary>Masks JWTs, bearer tokens, and secret-looking key/value pairs.</summary>
    public static string Redact(string text)
    {
        if (string.IsNullOrEmpty(text)) return text;
        text = JwtPattern().Replace(text, "eyJ***");
        text = SecretKvPattern().Replace(text, m => $"{m.Groups[1].Value}{m.Groups[2].Value}***");
        text = AccessTokenArgPattern().Replace(text, "$1***");
        return text;
    }

    [GeneratedRegex(@"eyJ[A-Za-z0-9_\-]{8,}\.[A-Za-z0-9_\-]{8,}\.[A-Za-z0-9_\-]*")]
    private static partial Regex JwtPattern();

    [GeneratedRegex(@"(""?(?:access_?token|refresh_?token|password|client_?token|accessToken|refreshToken|clientToken|RpsTicket|Authorization)""?)(\s*[:=]\s*""?(?:Bearer\s+|XBL3\.0\s+)?)[^""\s,}&]+", RegexOptions.IgnoreCase)]
    private static partial Regex SecretKvPattern();

    [GeneratedRegex(@"(--accessToken\s+)\S+")]
    private static partial Regex AccessTokenArgPattern();
}
