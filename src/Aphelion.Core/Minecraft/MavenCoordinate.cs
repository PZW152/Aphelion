namespace Aphelion.Core.Minecraft;

/// <summary>A Maven coordinate <c>group:artifact:version[:classifier][@extension]</c>.</summary>
public readonly record struct MavenCoordinate(string Group, string Artifact, string Version, string? Classifier, string Extension)
{
    public static MavenCoordinate Parse(string coordinate)
    {
        if (!TryParse(coordinate, out var c)) throw new FormatException($"Invalid maven coordinate '{coordinate}'");
        return c;
    }

    public static bool TryParse(string? coordinate, out MavenCoordinate result)
    {
        result = default;
        if (string.IsNullOrWhiteSpace(coordinate)) return false;
        var text = coordinate.Trim();
        var extension = "jar";
        var at = text.IndexOf('@', StringComparison.Ordinal);
        if (at >= 0)
        {
            extension = text[(at + 1)..];
            text = text[..at];
        }
        var parts = text.Split(':');
        if (parts.Length is < 3 or > 4 || parts.Any(p => p.Length == 0)) return false;
        if (parts.Any(p => p.Contains("..", StringComparison.Ordinal) || p.Contains('/') || p.Contains('\\'))) return false;
        if (extension.Length == 0 || extension.Contains('/') || extension.Contains('\\') || extension.Contains("..", StringComparison.Ordinal)) return false;
        result = new MavenCoordinate(parts[0], parts[1], parts[2], parts.Length == 4 ? parts[3] : null, extension);
        return true;
    }

    public string FileName => Classifier is null ? $"{Artifact}-{Version}.{Extension}" : $"{Artifact}-{Version}-{Classifier}.{Extension}";

    /// <summary>Repository-relative path with forward slashes, e.g. <c>org/ow2/asm/asm/9.6/asm-9.6.jar</c>.</summary>
    public string RelativePath => $"{Group.Replace('.', '/')}/{Artifact}/{Version}/{FileName}";

    /// <summary>Identity used to de-duplicate libraries across inherited versions (version excluded).</summary>
    public string Key => Classifier is null ? $"{Group}:{Artifact}" : $"{Group}:{Artifact}:{Classifier}";

    public MavenCoordinate WithClassifier(string? classifier) => this with { Classifier = classifier };

    public override string ToString() =>
        $"{Group}:{Artifact}:{Version}{(Classifier is null ? "" : ":" + Classifier)}{(Extension == "jar" ? "" : "@" + Extension)}";
}
