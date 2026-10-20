using System.Text.RegularExpressions;

namespace Aphelion.Core.Diagnostics;

/// <summary>A diagnosis. <see cref="Code"/> is translated by the UI; <see cref="Args"/> fill its placeholders.</summary>
public sealed record CrashFinding(string Code, int Priority, IReadOnlyDictionary<string, string> Args, string Evidence);

public sealed record CrashAnalysis(IReadOnlyList<CrashFinding> Findings, string? Description, string? CrashReportPath, string? JvmCrashLogPath, int ExitCode);

public sealed record CrashSource(string Name, string Text, string? Path = null);

/// <summary>Rule-based diagnosis of a failed game session from its output, crash report and JVM error log.</summary>
public static partial class CrashAnalyzer
{
    private delegate CrashFinding? RuleFn(string text);

    private static readonly RuleFn[] RuleSet =
    [
        t => MatchAny(t, "crash.memory", 100, OutOfMemoryPattern()),
        t => MatchAny(t, "crash.badHeap", 99, BadHeapPattern()),
        UnsupportedClassVersion,
        t => MatchAny(t, "crash.javaTooNew", 95, JavaTooNewPattern()),
        t => Capture(t, "crash.badJvmArg", 94, UnrecognizedOptionPattern(), "option"),
        FabricDependencies,
        ForgeDependencies,
        t => Capture(t, "crash.duplicateMods", 85, DuplicateModsPattern(), "mod"),
        t => Capture(t, "crash.mixin", 80, MixinPattern(), "mod"),
        t => Capture(t, "crash.modFailed", 78, ModFailedPattern(), "mod"),
        t => Capture(t, "crash.driverNative", 76, DriverFramePattern(), "dll"),
        t => MatchAny(t, "crash.graphicsDriver", 75, GraphicsPattern()),
        t => MatchAny(t, "crash.corruptFile", 70, CorruptZipPattern()),
        t => MatchAny(t, "crash.mainClass", 68, MainClassPattern()),
        t => Capture(t, "crash.suspectedMods", 60, SuspectedModsPattern(), "mods"),
        t => Capture(t, "crash.missingClass", 50, NoClassDefPattern(), "class"),
    ];

    public static CrashAnalysis Analyze(IReadOnlyList<CrashSource> sources, int exitCode)
    {
        var findings = new Dictionary<string, CrashFinding>();
        foreach (var source in sources)
        {
            foreach (var rule in RuleSet)
            {
                CrashFinding? f;
                try { f = rule(source.Text); }
                catch (RegexMatchTimeoutException) { continue; }
                if (f is not null && !findings.ContainsKey(f.Code)) findings[f.Code] = f;
            }
        }

        string? description = null;
        string? crashReport = null;
        string? jvmLog = null;
        foreach (var s in sources)
        {
            if (s.Name == "crash-report")
            {
                crashReport = s.Path;
                var m = DescriptionPattern().Match(s.Text);
                if (m.Success) description = m.Groups[1].Value.Trim();
            }
            if (s.Name == "hs_err") jvmLog = s.Path;
        }

        // Specific diagnoses make the generic "missing class" one redundant.
        if (findings.Count > 1) findings.Remove("crash.missingClass");
        if (findings.Count == 0)
            findings["crash.unknown"] = new CrashFinding("crash.unknown", 0, new Dictionary<string, string> { ["code"] = exitCode.ToString(System.Globalization.CultureInfo.InvariantCulture) }, "");

        return new CrashAnalysis(findings.Values.OrderByDescending(f => f.Priority).ToList(), description, crashReport, jvmLog, exitCode);
    }

    /// <summary>Collects the newest crash report and JVM error log written after <paramref name="since"/>.</summary>
    public static List<CrashSource> CollectFiles(string gameDirectory, DateTime since)
    {
        var result = new List<CrashSource>();
        static string Tail(string path, int maxChars = 200_000)
        {
            try
            {
                using var fs = new FileStream(path, FileMode.Open, FileAccess.Read, FileShare.ReadWrite);
                if (fs.Length > maxChars) fs.Seek(-maxChars, SeekOrigin.End);
                using var reader = new StreamReader(fs);
                return reader.ReadToEnd();
            }
            catch (IOException) { return ""; }
            catch (UnauthorizedAccessException) { return ""; }
        }

        var reports = Path.Combine(gameDirectory, "crash-reports");
        if (Directory.Exists(reports))
        {
            var newest = new DirectoryInfo(reports).GetFiles("crash-*.txt").Where(f => f.LastWriteTime >= since).MaxBy(f => f.LastWriteTime);
            if (newest is not null) result.Add(new CrashSource("crash-report", Tail(newest.FullName), newest.FullName));
        }
        if (Directory.Exists(gameDirectory))
        {
            var hs = new DirectoryInfo(gameDirectory).GetFiles("hs_err_pid*.log").Where(f => f.LastWriteTime >= since).MaxBy(f => f.LastWriteTime);
            if (hs is not null) result.Add(new CrashSource("hs_err", Tail(hs.FullName), hs.FullName));
        }
        var latest = Path.Combine(gameDirectory, "logs", "latest.log");
        if (File.Exists(latest) && File.GetLastWriteTime(latest) >= since) result.Add(new CrashSource("latest.log", Tail(latest), latest));
        return result;
    }

    private static CrashFinding? MatchAny(string text, string code, int priority, Regex pattern)
    {
        var m = pattern.Match(text);
        return m.Success ? new CrashFinding(code, priority, new Dictionary<string, string>(), Line(text, m.Index)) : null;
    }

    private static CrashFinding? Capture(string text, string code, int priority, Regex pattern, string arg)
    {
        var first = pattern.Match(text);
        if (!first.Success) return null;
        // The first non-empty capture group of each match (patterns may offer alternative groups).
        var values = pattern.Matches(text)
            .Select(m => m.Groups.Cast<Group>().Skip(1).FirstOrDefault(g => g.Success && g.Value.Trim().Length > 0)?.Value.Trim())
            .OfType<string>().Distinct().Take(6).ToList();
        return new CrashFinding(code, priority, new Dictionary<string, string> { [arg] = string.Join(", ", values) }, Line(text, first.Index));
    }

    private static CrashFinding? UnsupportedClassVersion(string text)
    {
        var m = ClassVersionPattern().Match(text);
        if (!m.Success) return null;
        var classVersion = int.Parse(m.Groups[1].Value, System.Globalization.CultureInfo.InvariantCulture);
        return new CrashFinding("crash.javaTooOld", 97,
            new Dictionary<string, string> { ["required"] = (classVersion - 44).ToString(System.Globalization.CultureInfo.InvariantCulture) }, Line(text, m.Index));
    }

    private static CrashFinding? FabricDependencies(string text)
    {
        var m = FabricIncompatiblePattern().Match(text);
        if (!m.Success) return null;
        var details = FabricDetailPattern().Matches(text, m.Index).Take(8).Select(d => d.Groups[1].Value.Trim()).Distinct().ToList();
        return new CrashFinding("crash.fabricDeps", 90, new Dictionary<string, string> { ["details"] = string.Join("\n", details) }, Line(text, m.Index));
    }

    private static CrashFinding? ForgeDependencies(string text)
    {
        var entries = ForgeMissingPattern().Matches(text)
            .Select(m => $"{m.Groups[1].Value} ← {m.Groups[2].Value} ({m.Groups[3].Value}, {m.Groups[4].Value})").Distinct().Take(8).ToList();
        if (entries.Count == 0) return null;
        return new CrashFinding("crash.forgeDeps", 90, new Dictionary<string, string> { ["details"] = string.Join("\n", entries) }, entries[0]);
    }

    private static string Line(string text, int index)
    {
        var start = text.LastIndexOf('\n', Math.Max(0, index - 1)) + 1;
        var end = text.IndexOf('\n', index);
        var line = text[start..(end < 0 ? text.Length : end)].Trim();
        return line.Length > 300 ? line[..300] : line;
    }

    private const int Timeout = 2000;

    [GeneratedRegex(@"java\.lang\.OutOfMemoryError|Could not reserve enough space for|There is insufficient memory for the Java Runtime", RegexOptions.None, Timeout)]
    private static partial Regex OutOfMemoryPattern();

    [GeneratedRegex(@"Invalid maximum heap size|Invalid initial heap size", RegexOptions.None, Timeout)]
    private static partial Regex BadHeapPattern();

    [GeneratedRegex(@"UnsupportedClassVersionError.*?class file version (\d+)", RegexOptions.None, Timeout)]
    private static partial Regex ClassVersionPattern();

    [GeneratedRegex(@"AppClassLoader cannot be cast to (?:class )?java\.net\.URLClassLoader|NoSuchFieldException: ucp|Unable to make (?:field|protected|private).*?accessible.*?(?:launchwrapper|java\.lang\.ClassLoader)", RegexOptions.None, Timeout)]
    private static partial Regex JavaTooNewPattern();

    [GeneratedRegex(@"Unrecognized VM option '([^']+)'", RegexOptions.None, Timeout)]
    private static partial Regex UnrecognizedOptionPattern();

    [GeneratedRegex(@"Incompatible mods? (?:found|set)|Mod resolution encountered an incompatible mod set", RegexOptions.None, Timeout)]
    private static partial Regex FabricIncompatiblePattern();

    [GeneratedRegex(@"^\s*-\s+(.*?(?:requires|is incompatible with|conflicts with|which is missing|Replace|Remove|Install).*?)\s*$", RegexOptions.Multiline, Timeout)]
    private static partial Regex FabricDetailPattern();

    [GeneratedRegex(@"Mod ID: '([^']+)', Requested by: '([^']+)', Expected range: '([^']+)', Actual version: '([^']+)'", RegexOptions.None, Timeout)]
    private static partial Regex ForgeMissingPattern();

    [GeneratedRegex(@"(?:Found duplicate mods?|DuplicateModsFoundException|Mod (?:ID )?'?([\w\-]+)'? (?:is present multiple times|has been loaded twice))", RegexOptions.None, Timeout)]
    private static partial Regex DuplicateModsPattern();

    [GeneratedRegex(@"Mixin apply (?:for mod|failed for) ([\w\-]+)|InvalidInjectionException.*?from mod ([\w\-]+)|MixinApplyError|InvalidMixinException", RegexOptions.None, Timeout)]
    private static partial Regex MixinPattern();

    [GeneratedRegex(@"(?:Failed to create mod instance\. ModID: |Mod )([\w\-]+)(?:,| has failed to load correctly)", RegexOptions.None, Timeout)]
    private static partial Regex ModFailedPattern();

    [GeneratedRegex(@"Problematic frame:\s*\n#\s*C\s+\[((?:ig\w+icd\d+|nvoglv\d+|atio6axx|atig6pxx|amdxc64|nvwgf2umx|igxelpicd\d+)\.dll)", RegexOptions.IgnoreCase, Timeout)]
    private static partial Regex DriverFramePattern();

    [GeneratedRegex(@"Pixel format not accelerated|WGL: The driver does not appear to support OpenGL|GLFW error 65542|No OpenGL context found in the current thread|Couldn't set pixel format", RegexOptions.None, Timeout)]
    private static partial Regex GraphicsPattern();

    [GeneratedRegex(@"java\.util\.zip\.ZipException|invalid LOC header|zip END header not found|java\.util\.jar\.JarException", RegexOptions.None, Timeout)]
    private static partial Regex CorruptZipPattern();

    [GeneratedRegex(@"Could not find or load main class|ClassNotFoundException: net\.minecraft\.client\.main\.Main", RegexOptions.None, Timeout)]
    private static partial Regex MainClassPattern();

    [GeneratedRegex(@"Suspected Mods?(?:\(s\))?:\s*(?!Unknown|NONE)(.+)", RegexOptions.None, Timeout)]
    private static partial Regex SuspectedModsPattern();

    [GeneratedRegex(@"java\.lang\.NoClassDefFoundError: ([\w/$.]+)", RegexOptions.None, Timeout)]
    private static partial Regex NoClassDefPattern();

    [GeneratedRegex(@"^Description: (.+)$", RegexOptions.Multiline, Timeout)]
    private static partial Regex DescriptionPattern();
}
