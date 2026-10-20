using System.Text;
using System.Text.Json;
using System.Text.Json.Serialization;
using Aphelion.Core.Net;
using Aphelion.Core.Storage;

namespace Aphelion.Core.Content;

public enum ProjectType { Mod, Modpack, ResourcePack, Shader }

public sealed class ModrinthHit
{
    [JsonPropertyName("project_id")] public string ProjectId { get; set; } = "";
    public string Slug { get; set; } = "";
    public string Title { get; set; } = "";
    public string Description { get; set; } = "";
    public string Author { get; set; } = "";
    [JsonPropertyName("icon_url")] public string? IconUrl { get; set; }
    public long Downloads { get; set; }
    public long Follows { get; set; }
    [JsonPropertyName("date_modified")] public DateTimeOffset DateModified { get; set; }
    public List<string> Categories { get; set; } = [];
    [JsonPropertyName("display_categories")] public List<string> DisplayCategories { get; set; } = [];
    [JsonPropertyName("project_type")] public string ProjectType { get; set; } = "";
    [JsonPropertyName("client_side")] public string? ClientSide { get; set; }
    public string? License { get; set; }
    public int? Color { get; set; }
}

public sealed class ModrinthSearchResult
{
    public List<ModrinthHit> Hits { get; set; } = [];
    public int Offset { get; set; }
    public int Limit { get; set; }
    [JsonPropertyName("total_hits")] public int TotalHits { get; set; }
}

public sealed class ModrinthProject
{
    public string Id { get; set; } = "";
    public string Slug { get; set; } = "";
    public string Title { get; set; } = "";
    public string Description { get; set; } = "";
    public string Body { get; set; } = "";
    [JsonPropertyName("icon_url")] public string? IconUrl { get; set; }
    [JsonPropertyName("project_type")] public string ProjectType { get; set; } = "";
    public long Downloads { get; set; }
    public List<string> Categories { get; set; } = [];
    public List<string> Loaders { get; set; } = [];
    [JsonPropertyName("game_versions")] public List<string> GameVersions { get; set; } = [];
    public ModrinthLicense? License { get; set; }
    [JsonPropertyName("source_url")] public string? SourceUrl { get; set; }
    [JsonPropertyName("issues_url")] public string? IssuesUrl { get; set; }
    [JsonPropertyName("wiki_url")] public string? WikiUrl { get; set; }
    [JsonPropertyName("client_side")] public string? ClientSide { get; set; }
    public List<ModrinthGalleryImage> Gallery { get; set; } = [];
}

public sealed class ModrinthLicense
{
    public string Id { get; set; } = "";
    public string? Name { get; set; }
}

public sealed class ModrinthGalleryImage
{
    public string Url { get; set; } = "";
    public string? Title { get; set; }
    public bool Featured { get; set; }
}

public sealed class ModrinthVersion
{
    public string Id { get; set; } = "";
    [JsonPropertyName("project_id")] public string ProjectId { get; set; } = "";
    public string Name { get; set; } = "";
    [JsonPropertyName("version_number")] public string VersionNumber { get; set; } = "";
    [JsonPropertyName("version_type")] public string VersionType { get; set; } = "release";
    [JsonPropertyName("date_published")] public DateTimeOffset DatePublished { get; set; }
    public long Downloads { get; set; }
    public List<string> Loaders { get; set; } = [];
    [JsonPropertyName("game_versions")] public List<string> GameVersions { get; set; } = [];
    public List<ModrinthFile> Files { get; set; } = [];
    public List<ModrinthDependency> Dependencies { get; set; } = [];
    public string? Changelog { get; set; }

    public ModrinthFile? PrimaryFile => Files.FirstOrDefault(f => f.Primary) ?? Files.FirstOrDefault();
}

public sealed class ModrinthFile
{
    public Dictionary<string, string> Hashes { get; set; } = [];
    public string Url { get; set; } = "";
    public string Filename { get; set; } = "";
    public bool Primary { get; set; }
    public long Size { get; set; }
}

public sealed class ModrinthDependency
{
    [JsonPropertyName("version_id")] public string? VersionId { get; set; }
    [JsonPropertyName("project_id")] public string? ProjectId { get; set; }
    [JsonPropertyName("file_name")] public string? FileName { get; set; }
    [JsonPropertyName("dependency_type")] public string DependencyType { get; set; } = "required";
}

/// <summary>Minimal Modrinth API v2 client (public endpoints only; no API key).</summary>
public sealed class ModrinthClient
{
    public const string Api = "https://api.modrinth.com/v2";

    public static string FacetType(ProjectType type) => type switch
    {
        ProjectType.Mod => "mod",
        ProjectType.Modpack => "modpack",
        ProjectType.ResourcePack => "resourcepack",
        ProjectType.Shader => "shader",
        _ => "mod",
    };

    public async Task<ModrinthSearchResult> SearchAsync(string query, ProjectType type, string? gameVersion, string? loader, string sort,
        int offset, int limit, CancellationToken ct)
    {
        var facets = new List<string[]> { new[] { $"project_type:{FacetType(type)}" } };
        if (!string.IsNullOrEmpty(gameVersion)) facets.Add([$"versions:{gameVersion}"]);
        if (!string.IsNullOrEmpty(loader) && type is ProjectType.Mod or ProjectType.Modpack) facets.Add([$"categories:{loader}"]);
        var index = sort is "relevance" or "downloads" or "follows" or "newest" or "updated" ? sort : "relevance";
        var url = new StringBuilder($"{Api}/search?limit={Math.Clamp(limit, 1, 100)}&offset={Math.Max(0, offset)}&index={index}");
        url.Append("&facets=").Append(Uri.EscapeDataString(JsonSerializer.Serialize(facets)));
        if (!string.IsNullOrWhiteSpace(query)) url.Append("&query=").Append(Uri.EscapeDataString(query.Trim()));
        return await Http.GetJsonAsync<ModrinthSearchResult>(url.ToString(), ct).ConfigureAwait(false);
    }

    public Task<ModrinthProject> GetProjectAsync(string idOrSlug, CancellationToken ct) =>
        Http.GetJsonAsync<ModrinthProject>($"{Api}/project/{Uri.EscapeDataString(idOrSlug)}", ct);

    public Task<ModrinthVersion> GetVersionAsync(string versionId, CancellationToken ct) =>
        Http.GetJsonAsync<ModrinthVersion>($"{Api}/version/{Uri.EscapeDataString(versionId)}", ct);

    public async Task<List<ModrinthVersion>> GetVersionsAsync(string projectId, string? gameVersion, string? loader, CancellationToken ct)
    {
        var url = new StringBuilder($"{Api}/project/{Uri.EscapeDataString(projectId)}/version?include_changelog=false");
        if (!string.IsNullOrEmpty(gameVersion)) url.Append("&game_versions=").Append(Uri.EscapeDataString(JsonSerializer.Serialize(new[] { gameVersion })));
        if (!string.IsNullOrEmpty(loader)) url.Append("&loaders=").Append(Uri.EscapeDataString(JsonSerializer.Serialize(new[] { loader })));
        return await Http.GetJsonAsync<List<ModrinthVersion>>(url.ToString(), ct).ConfigureAwait(false);
    }

    /// <summary>Identifies local files by SHA-1. Unknown files are simply absent from the result.</summary>
    public async Task<Dictionary<string, ModrinthVersion>> IdentifyAsync(IReadOnlyCollection<string> sha1Hashes, CancellationToken ct)
    {
        if (sha1Hashes.Count == 0) return [];
        var result = new Dictionary<string, ModrinthVersion>(StringComparer.OrdinalIgnoreCase);
        foreach (var chunk in sha1Hashes.Chunk(500))
        {
            var map = await Http.SendJsonAsync<Dictionary<string, ModrinthVersion>>(HttpMethod.Post, $"{Api}/version_files",
                new { hashes = chunk, algorithm = "sha1" }, ct).ConfigureAwait(false);
            foreach (var (hash, version) in map) result[hash] = version;
        }
        return result;
    }

    /// <summary>Projects for many ids in one call.</summary>
    public async Task<List<ModrinthProject>> GetProjectsAsync(IReadOnlyCollection<string> ids, CancellationToken ct)
    {
        if (ids.Count == 0) return [];
        var list = new List<ModrinthProject>();
        foreach (var chunk in ids.Chunk(200))
            list.AddRange(await Http.GetJsonAsync<List<ModrinthProject>>($"{Api}/projects?ids={Uri.EscapeDataString(JsonSerializer.Serialize(chunk))}", ct).ConfigureAwait(false));
        return list;
    }

    /// <summary>Folder inside the game directory where a project type is installed.</summary>
    public static string FolderFor(ProjectType type) => type switch
    {
        ProjectType.ResourcePack => "resourcepacks",
        ProjectType.Shader => "shaderpacks",
        _ => "mods",
    };

    public static string? LoaderSlug(Instances.LoaderKind kind) => kind switch
    {
        Instances.LoaderKind.Fabric => "fabric",
        Instances.LoaderKind.Quilt => "quilt",
        Instances.LoaderKind.Forge or Instances.LoaderKind.LegacyForge => "forge",
        Instances.LoaderKind.NeoForge => "neoforge",
        _ => null,
    };
}
