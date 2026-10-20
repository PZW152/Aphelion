using System.Runtime.InteropServices;
using System.Text.RegularExpressions;

namespace Aphelion.Core.Minecraft;

/// <summary>The environment rules are evaluated against. Injectable so tests can cover every OS.</summary>
public sealed record RuntimePlatform(string OsName, string OsVersion, Architecture Arch)
{
    public static RuntimePlatform Current { get; } = new(
        OperatingSystem.IsWindows() ? "windows" : OperatingSystem.IsMacOS() ? "osx" : "linux",
        Environment.OSVersion.Version.ToString(),
        RuntimeInformation.OSArchitecture);

    public bool Is64Bit => Arch is Architecture.X64 or Architecture.Arm64;

    /// <summary>Value substituted for <c>${arch}</c> in legacy native classifiers.</summary>
    public string ArchBits => Is64Bit ? "64" : "32";

    public string ClasspathSeparator => OsName == "windows" ? ";" : ":";
}

/// <summary>Evaluates Mojang library/argument rules.</summary>
public static class Rules
{
    /// <summary>
    /// No rules: allowed. Otherwise start disallowed and let every matching rule set the outcome (last match wins).
    /// Features absent from <paramref name="features"/> count as false.
    /// </summary>
    public static bool Allows(IReadOnlyList<Rule>? rules, RuntimePlatform platform, IReadOnlyDictionary<string, bool>? features = null)
    {
        if (rules is null || rules.Count == 0) return true;
        var allowed = false;
        foreach (var rule in rules)
        {
            if (Matches(rule, platform, features))
                allowed = string.Equals(rule.Action, "allow", StringComparison.OrdinalIgnoreCase);
        }
        return allowed;
    }

    private static bool Matches(Rule rule, RuntimePlatform platform, IReadOnlyDictionary<string, bool>? features)
    {
        if (rule.Os is { } os)
        {
            if (os.Name is not null && !string.Equals(os.Name, platform.OsName, StringComparison.OrdinalIgnoreCase)) return false;
            if (os.Arch is not null && !ArchMatches(os.Arch, platform.Arch)) return false;
            if (os.Version is not null)
            {
                try
                {
                    if (!Regex.IsMatch(platform.OsVersion, os.Version, RegexOptions.None, TimeSpan.FromMilliseconds(100))) return false;
                }
                catch (ArgumentException) { return false; }
                catch (RegexMatchTimeoutException) { return false; }
            }
        }

        if (rule.Features is { Count: > 0 })
        {
            foreach (var (name, expected) in rule.Features)
            {
                var actual = features is not null && features.TryGetValue(name, out var v) && v;
                if (actual != expected) return false;
            }
        }
        return true;
    }

    private static bool ArchMatches(string arch, Architecture actual) => arch.ToLowerInvariant() switch
    {
        "x86" => actual == Architecture.X86,
        "x64" or "x86_64" or "amd64" => actual == Architecture.X64,
        "arm64" or "aarch64" => actual == Architecture.Arm64,
        _ => false,
    };
}
