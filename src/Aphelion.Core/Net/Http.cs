using System.Net;
using System.Net.Http.Headers;
using System.Text;
using System.Text.Json;
using Aphelion.Core.Storage;

namespace Aphelion.Core.Net;

/// <summary>One shared, pooled HttpClient for the whole process (avoids socket exhaustion, honours system proxy).</summary>
public static class Http
{
    public static HttpClient Client { get; } = Create();

    private static HttpClient Create()
    {
        var handler = new SocketsHttpHandler
        {
            AutomaticDecompression = DecompressionMethods.All,
            ConnectTimeout = TimeSpan.FromSeconds(12),
            PooledConnectionLifetime = TimeSpan.FromMinutes(5),
            MaxConnectionsPerServer = 32,
            EnableMultipleHttp2Connections = true,
            // Choose among the CDN's edges by racing connections instead of trusting the first DNS answer.
            ConnectCallback = EdgeConnector.ConnectCallback,
        };
        var client = new HttpClient(handler) { Timeout = Timeout.InfiniteTimeSpan };
        client.DefaultRequestHeaders.UserAgent.ParseAdd(AppInfo.UserAgent);
        client.DefaultRequestVersion = HttpVersion.Version20;
        client.DefaultVersionPolicy = HttpVersionPolicy.RequestVersionOrLower;
        return client;
    }

    public static readonly TimeSpan DefaultTimeout = TimeSpan.FromSeconds(30);

    public static async Task<T> GetJsonAsync<T>(string url, CancellationToken ct, JsonSerializerOptions? options = null, TimeSpan? timeout = null)
    {
        using var cts = CancellationTokenSource.CreateLinkedTokenSource(ct);
        cts.CancelAfter(timeout ?? DefaultTimeout);
        try
        {
            using var response = await Client.GetAsync(url, HttpCompletionOption.ResponseHeadersRead, cts.Token).ConfigureAwait(false);
            await EnsureSuccessAsync(response, url).ConfigureAwait(false);
            await using var stream = await response.Content.ReadAsStreamAsync(cts.Token).ConfigureAwait(false);
            return await JsonSerializer.DeserializeAsync<T>(stream, options ?? Json.External, cts.Token).ConfigureAwait(false)
                   ?? throw new JsonException($"Empty response from {url}");
        }
        catch (OperationCanceledException) when (!ct.IsCancellationRequested)
        {
            throw new TimeoutException($"Request timed out: {Url.Describe(url)}");
        }
    }

    public static async Task<string> GetStringAsync(string url, CancellationToken ct, TimeSpan? timeout = null)
    {
        using var cts = CancellationTokenSource.CreateLinkedTokenSource(ct);
        cts.CancelAfter(timeout ?? DefaultTimeout);
        try
        {
            using var response = await Client.GetAsync(url, cts.Token).ConfigureAwait(false);
            await EnsureSuccessAsync(response, url).ConfigureAwait(false);
            return await response.Content.ReadAsStringAsync(cts.Token).ConfigureAwait(false);
        }
        catch (OperationCanceledException) when (!ct.IsCancellationRequested)
        {
            throw new TimeoutException($"Request timed out: {Url.Describe(url)}");
        }
    }

    public static async Task<TResponse> SendJsonAsync<TResponse>(HttpMethod method, string url, object? body, CancellationToken ct,
        Action<HttpRequestHeaders>? headers = null, JsonSerializerOptions? options = null)
    {
        using var request = new HttpRequestMessage(method, url);
        if (body is not null)
            request.Content = new StringContent(JsonSerializer.Serialize(body, options ?? Json.External), Encoding.UTF8, "application/json");
        request.Headers.Accept.Add(new MediaTypeWithQualityHeaderValue("application/json"));
        headers?.Invoke(request.Headers);
        using var cts = CancellationTokenSource.CreateLinkedTokenSource(ct);
        cts.CancelAfter(DefaultTimeout);
        using var response = await Client.SendAsync(request, cts.Token).ConfigureAwait(false);
        await EnsureSuccessAsync(response, url).ConfigureAwait(false);
        var text = await response.Content.ReadAsStringAsync(cts.Token).ConfigureAwait(false);
        return JsonSerializer.Deserialize<TResponse>(text, options ?? Json.External) ?? throw new JsonException($"Empty response from {url}");
    }

    public static async Task EnsureSuccessAsync(HttpResponseMessage response, string url)
    {
        if (response.IsSuccessStatusCode) return;
        string snippet = "";
        try
        {
            snippet = await response.Content.ReadAsStringAsync().ConfigureAwait(false);
            if (snippet.Length > 300) snippet = snippet[..300];
        }
        catch (HttpRequestException) { }
        throw new HttpStatusException(response.StatusCode, url, Logging.Log.Redact(snippet));
    }
}

public sealed class HttpStatusException(HttpStatusCode status, string url, string body)
    : HttpRequestException($"HTTP {(int)status} from {Aphelion.Core.Net.Url.Describe(url)}{(body.Length > 0 ? ": " + body : "")}", null, status)
{
    public string Url { get; } = url;
    public string Body { get; } = body;
}

public static class Url
{
    /// <summary>Host and path only — never query strings, which may carry tokens.</summary>
    public static string Describe(string url) =>
        Uri.TryCreate(url, UriKind.Absolute, out var u) ? $"{u.Host}{u.AbsolutePath}" : "(invalid url)";
}
