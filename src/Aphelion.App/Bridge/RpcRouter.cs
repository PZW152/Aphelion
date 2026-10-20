using System.Text.Json;
using System.Text.Json.Nodes;
using Aphelion.Core;
using Aphelion.Core.Logging;
using Aphelion.Core.Storage;
using Aphelion.Core.Tasks;

namespace Aphelion.App.Bridge;

/// <summary>
/// JSON-RPC style bridge between the page and the host.
/// Page → host: <c>{"id":1,"method":"instances.list","params":{...}}</c>.
/// Host → page: <c>{"id":1,"result":...}</c>, <c>{"id":1,"error":{"code","message","args"}}</c>, or <c>{"event":"x","data":...}</c>.
/// </summary>
internal sealed class RpcRouter
{
    private readonly Dictionary<string, Func<JsonElement, CancellationToken, Task<object?>>> _methods = new(StringComparer.Ordinal);

    public void Add(string name, Func<JsonElement, CancellationToken, Task<object?>> handler) => _methods.Add(name, handler);

    public void Add(string name, Func<object?> handler) => _methods.Add(name, (_, _) => Task.FromResult(handler()));

    public void Add<T>(string name, Func<T, object?> handler) =>
        _methods.Add(name, (p, _) => Task.FromResult(handler(Bind<T>(p))));

    public void Add<T>(string name, Func<T, CancellationToken, Task<object?>> handler) =>
        _methods.Add(name, (p, ct) => handler(Bind<T>(p), ct));

    public IEnumerable<string> Methods => _methods.Keys;

    private static T Bind<T>(JsonElement p)
    {
        if (p.ValueKind is JsonValueKind.Undefined or JsonValueKind.Null) p = JsonDocument.Parse("{}").RootElement;
        return p.Deserialize<T>(Json.App) ?? throw new JsonException("Missing parameters");
    }

    public async Task<string> HandleAsync(string message, CancellationToken ct)
    {
        long id = 0;
        string method = "";
        try
        {
            using var doc = JsonDocument.Parse(message);
            var root = doc.RootElement;
            id = root.GetProperty("id").GetInt64();
            method = root.GetProperty("method").GetString() ?? "";
            if (!_methods.TryGetValue(method, out var handler))
                return Error(id, "rpc.unknownMethod", $"Unknown method {method}", null);
            var p = root.TryGetProperty("params", out var pe) ? pe.Clone() : default;
            var result = await handler(p, ct).ConfigureAwait(false);
            return JsonSerializer.Serialize(new { id, result }, Json.App);
        }
        catch (AppException e)
        {
            Log.Info($"RPC {method}: {e.Code} {e.Message}");
            return Error(id, e.Code, e.Message, e.Args);
        }
        catch (OperationCanceledException)
        {
            return Error(id, "cancelled", "Cancelled", null);
        }
        catch (Exception e)
        {
            Log.Error($"RPC {method} failed", e);
            return Error(id, ErrorCodes.Classify(e), Log.Redact(e.Message), null);
        }
    }

    private static string Error(long id, string code, string message, IReadOnlyDictionary<string, string>? args) =>
        JsonSerializer.Serialize(new { id, error = new { code, message, args } }, Json.App);

    public static string Event(string name, object? data) => JsonSerializer.Serialize(new { @event = name, data }, Json.App);

    /// <summary>Applies a partial JSON object onto a copy of <paramref name="target"/> (objects merge, everything else replaces).</summary>
    public static T Patch<T>(T target, JsonElement patch)
    {
        var node = JsonSerializer.SerializeToNode(target, Json.App)!.AsObject();
        Merge(node, JsonNode.Parse(patch.GetRawText())!.AsObject());
        return node.Deserialize<T>(Json.App)!;
    }

    private static void Merge(JsonObject target, JsonObject patch)
    {
        foreach (var (key, value) in patch.ToList())
        {
            if (value is JsonObject childPatch && target[key] is JsonObject childTarget) Merge(childTarget, childPatch);
            else target[key] = value?.DeepClone();
        }
    }
}
