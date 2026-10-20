using System.Net;
using System.Text;
using System.Text.RegularExpressions;

namespace Aphelion.Core.Launch;

public enum GameLogLevel { Trace, Debug, Info, Warn, Error, Fatal }

public sealed record GameLogLine(long Sequence, DateTimeOffset Time, GameLogLevel Level, string Thread, string Logger, string Message);

/// <summary>
/// Turns the game's console output into log lines. With Mojang's log configuration the game writes log4j XML
/// events; without it (or for output that bypasses log4j) plain text lines are classified heuristically.
/// </summary>
public sealed partial class GameLogParser
{
    private readonly StringBuilder _event = new();
    private long _sequence;

    public IEnumerable<GameLogLine> Feed(string line)
    {
        if (_event.Length > 0 || line.TrimStart().StartsWith("<log4j:Event", StringComparison.Ordinal))
        {
            _event.AppendLine(line);
            if (!line.Contains("</log4j:Event>", StringComparison.Ordinal))
            {
                if (_event.Length > 1_000_000) // a broken stream must not grow without bound
                {
                    var raw = _event.ToString();
                    _event.Clear();
                    return [Plain(raw)];
                }
                return [];
            }
            var block = _event.ToString();
            _event.Clear();
            return [ParseEvent(block)];
        }
        return [Plain(line)];
    }

    private GameLogLine ParseEvent(string block)
    {
        string Attr(string name)
        {
            var m = Regex.Match(block, $"{name}=\"([^\"]*)\"");
            return m.Success ? WebUtility.HtmlDecode(m.Groups[1].Value) : "";
        }
        var message = CData(block, "log4j:Message");
        var throwable = CData(block, "log4j:Throwable");
        if (throwable.Length > 0) message = message.Length > 0 ? message + "\n" + throwable.TrimEnd() : throwable.TrimEnd();
        var time = long.TryParse(Attr("timestamp"), out var ms) ? DateTimeOffset.FromUnixTimeMilliseconds(ms).ToLocalTime() : DateTimeOffset.Now;
        return new GameLogLine(++_sequence, time, ParseLevel(Attr("level")), Attr("thread"), Attr("logger"), message);
    }

    private static string CData(string block, string tag)
    {
        var m = Regex.Match(block, $@"<{tag}><!\[CDATA\[(.*?)\]\]></{tag}>", RegexOptions.Singleline);
        if (m.Success) return m.Groups[1].Value;
        m = Regex.Match(block, $"<{tag}>(.*?)</{tag}>", RegexOptions.Singleline);
        return m.Success ? WebUtility.HtmlDecode(m.Groups[1].Value) : "";
    }

    private GameLogLine Plain(string line)
    {
        var m = PlainPattern().Match(line);
        if (m.Success)
            return new GameLogLine(++_sequence, DateTimeOffset.Now, ParseLevel(m.Groups["level"].Value), m.Groups["thread"].Value, "", m.Groups["msg"].Value);
        var level = line.Contains("Exception", StringComparison.Ordinal) || line.StartsWith("\tat ", StringComparison.Ordinal) || line.StartsWith("Caused by:", StringComparison.Ordinal)
            ? GameLogLevel.Error : GameLogLevel.Info;
        return new GameLogLine(++_sequence, DateTimeOffset.Now, level, "", "", line);
    }

    public static GameLogLevel ParseLevel(string level) => level.ToUpperInvariant() switch
    {
        "TRACE" => GameLogLevel.Trace,
        "DEBUG" => GameLogLevel.Debug,
        "WARN" or "WARNING" => GameLogLevel.Warn,
        "ERROR" or "SEVERE" => GameLogLevel.Error,
        "FATAL" => GameLogLevel.Fatal,
        _ => GameLogLevel.Info,
    };

    [GeneratedRegex(@"^\[(?<time>[\d:.]+)\] \[(?<thread>[^\]/]*)/(?<level>[A-Z]+)\](?: \[[^\]]*\])?: ?(?<msg>.*)$")]
    private static partial Regex PlainPattern();
}
