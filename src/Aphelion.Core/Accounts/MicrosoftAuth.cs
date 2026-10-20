using System.Net;
using System.Net.Http.Headers;
using System.Text.Json;
using System.Text.Json.Nodes;
using Aphelion.Core.Logging;
using Aphelion.Core.Net;

namespace Aphelion.Core.Accounts;

public sealed record DeviceCodeInfo(string UserCode, string VerificationUri, DateTimeOffset ExpiresAt, int IntervalSeconds, string DeviceCode);

public sealed record MinecraftProfile(string Id, string Name, string? SkinUrl, bool SlimSkin);

/// <summary>
/// Microsoft account sign-in using the OAuth 2.0 device code flow, then Xbox Live → XSTS → Minecraft services.
/// Requires an Azure application (client) ID with public client flows enabled; see docs/accounts.md.
/// </summary>
public sealed class MicrosoftAuth(string clientId)
{
    private const string Authority = "https://login.microsoftonline.com/consumers/oauth2/v2.0";
    private const string Scope = "XboxLive.signin offline_access";

    public async Task<DeviceCodeInfo> BeginDeviceCodeAsync(CancellationToken ct)
    {
        RequireClientId();
        var form = new Dictionary<string, string> { ["client_id"] = clientId, ["scope"] = Scope };
        var json = await PostFormAsync($"{Authority}/devicecode", form, ct).ConfigureAwait(false);
        return new DeviceCodeInfo(
            json.GetProperty("user_code").GetString()!,
            json.GetProperty("verification_uri").GetString()!,
            DateTimeOffset.UtcNow.AddSeconds(json.GetProperty("expires_in").GetInt32()),
            json.TryGetProperty("interval", out var i) ? i.GetInt32() : 5,
            json.GetProperty("device_code").GetString()!);
    }

    /// <summary>Polls until the user finishes signing in, then completes the Minecraft token chain.</summary>
    public async Task<(MinecraftProfile Profile, AccountSecret Secret)> CompleteDeviceCodeAsync(DeviceCodeInfo code, CancellationToken ct)
    {
        var interval = TimeSpan.FromSeconds(Math.Max(1, code.IntervalSeconds));
        while (true)
        {
            ct.ThrowIfCancellationRequested();
            if (DateTimeOffset.UtcNow > code.ExpiresAt) throw AppException.Of("msa.expired", "The sign-in code expired. Start again.");
            await Task.Delay(interval, ct).ConfigureAwait(false);

            var form = new Dictionary<string, string>
            {
                ["grant_type"] = "urn:ietf:params:oauth:grant-type:device_code",
                ["client_id"] = clientId,
                ["device_code"] = code.DeviceCode,
            };
            using var response = await Http.Client.PostAsync($"{Authority}/token", new FormUrlEncodedContent(form), ct).ConfigureAwait(false);
            var body = await response.Content.ReadAsStringAsync(ct).ConfigureAwait(false);
            using var doc = JsonDocument.Parse(body);
            if (response.IsSuccessStatusCode)
                return await FinishAsync(doc.RootElement.GetProperty("access_token").GetString()!, doc.RootElement.GetProperty("refresh_token").GetString(), ct).ConfigureAwait(false);

            var error = doc.RootElement.TryGetProperty("error", out var e) ? e.GetString() : null;
            switch (error)
            {
                case "authorization_pending": continue;
                case "slow_down": interval += TimeSpan.FromSeconds(5); continue;
                case "authorization_declined": throw AppException.Of("msa.declined", "Sign-in was declined.");
                case "expired_token": throw AppException.Of("msa.expired", "The sign-in code expired. Start again.");
                default: throw AppException.Of("msa.oauth", $"Microsoft sign-in failed: {error}", ("error", error));
            }
        }
    }

    /// <summary>Uses the stored Microsoft refresh token to obtain a fresh Minecraft token.</summary>
    public async Task<(MinecraftProfile Profile, AccountSecret Secret)> RefreshAsync(string refreshToken, CancellationToken ct)
    {
        RequireClientId();
        var form = new Dictionary<string, string>
        {
            ["grant_type"] = "refresh_token",
            ["client_id"] = clientId,
            ["refresh_token"] = refreshToken,
            ["scope"] = Scope,
        };
        JsonElement json;
        try
        {
            json = await PostFormAsync($"{Authority}/token", form, ct).ConfigureAwait(false);
        }
        catch (HttpStatusException e) when (e.StatusCode is HttpStatusCode.BadRequest or HttpStatusCode.Unauthorized)
        {
            throw AppException.Of("msa.relogin", "The Microsoft session has expired. Sign in again.");
        }
        return await FinishAsync(json.GetProperty("access_token").GetString()!, json.GetProperty("refresh_token").GetString() ?? refreshToken, ct).ConfigureAwait(false);
    }

    private async Task<(MinecraftProfile, AccountSecret)> FinishAsync(string msAccessToken, string? msRefreshToken, CancellationToken ct)
    {
        // Xbox Live user token.
        var xbl = await PostXboxAsync("https://user.auth.xboxlive.com/user/authenticate", new JsonObject
        {
            ["Properties"] = new JsonObject { ["AuthMethod"] = "RPS", ["SiteName"] = "user.auth.xboxlive.com", ["RpsTicket"] = "d=" + msAccessToken },
            ["RelyingParty"] = "http://auth.xboxlive.com",
            ["TokenType"] = "JWT",
        }, ct).ConfigureAwait(false);
        var xblToken = xbl.GetProperty("Token").GetString()!;

        // XSTS token for Minecraft services.
        var xsts = await PostXboxAsync("https://xsts.auth.xboxlive.com/xsts/authorize", new JsonObject
        {
            ["Properties"] = new JsonObject { ["SandboxId"] = "RETAIL", ["UserTokens"] = new JsonArray(xblToken) },
            ["RelyingParty"] = "rp://api.minecraftservices.com/",
            ["TokenType"] = "JWT",
        }, ct).ConfigureAwait(false);
        var uhs = xsts.GetProperty("DisplayClaims").GetProperty("xui")[0].GetProperty("uhs").GetString()!;
        var xstsToken = xsts.GetProperty("Token").GetString()!;

        // Minecraft access token.
        var login = await Http.SendJsonAsync<JsonElement>(HttpMethod.Post, "https://api.minecraftservices.com/authentication/login_with_xbox",
            new JsonObject { ["identityToken"] = $"XBL3.0 x={uhs};{xstsToken}" }, ct).ConfigureAwait(false);
        var mcToken = login.GetProperty("access_token").GetString()!;
        var expires = DateTimeOffset.UtcNow.AddSeconds(login.TryGetProperty("expires_in", out var ex) ? ex.GetInt32() : 86400);

        var profile = await GetProfileAsync(mcToken, ct).ConfigureAwait(false);
        Log.Info($"Microsoft sign-in completed for {profile.Name}");
        return (profile, new AccountSecret { AccessToken = mcToken, AccessExpires = expires, RefreshToken = msRefreshToken, Xuid = "" });
    }

    public static async Task<MinecraftProfile> GetProfileAsync(string mcToken, CancellationToken ct)
    {
        using var request = new HttpRequestMessage(HttpMethod.Get, "https://api.minecraftservices.com/minecraft/profile");
        request.Headers.Authorization = new AuthenticationHeaderValue("Bearer", mcToken);
        using var response = await Http.Client.SendAsync(request, ct).ConfigureAwait(false);
        if (response.StatusCode == HttpStatusCode.NotFound)
            throw AppException.Of("msa.noGame", "This Microsoft account does not own Minecraft: Java Edition.");
        await Http.EnsureSuccessAsync(response, request.RequestUri!.ToString()).ConfigureAwait(false);
        using var doc = JsonDocument.Parse(await response.Content.ReadAsStringAsync(ct).ConfigureAwait(false));
        var root = doc.RootElement;
        string? skin = null;
        var slim = false;
        if (root.TryGetProperty("skins", out var skins))
        {
            foreach (var s in skins.EnumerateArray())
            {
                if (s.TryGetProperty("state", out var st) && st.GetString() != "ACTIVE") continue;
                skin = s.GetProperty("url").GetString();
                slim = s.TryGetProperty("variant", out var v) && string.Equals(v.GetString(), "SLIM", StringComparison.OrdinalIgnoreCase);
                break;
            }
        }
        return new MinecraftProfile(root.GetProperty("id").GetString()!, root.GetProperty("name").GetString()!, skin, slim);
    }

    private static async Task<JsonElement> PostXboxAsync(string url, JsonObject body, CancellationToken ct)
    {
        using var request = new HttpRequestMessage(HttpMethod.Post, url)
        {
            Content = new StringContent(body.ToJsonString(), System.Text.Encoding.UTF8, "application/json"),
        };
        request.Headers.Accept.Add(new MediaTypeWithQualityHeaderValue("application/json"));
        request.Headers.Add("x-xbl-contract-version", "1");
        using var response = await Http.Client.SendAsync(request, ct).ConfigureAwait(false);
        var text = await response.Content.ReadAsStringAsync(ct).ConfigureAwait(false);
        if (response.StatusCode == HttpStatusCode.Unauthorized && text.Contains("XErr", StringComparison.Ordinal))
        {
            using var err = JsonDocument.Parse(text);
            var xerr = err.RootElement.TryGetProperty("XErr", out var x) ? x.GetInt64() : 0;
            throw xerr switch
            {
                2148916233 => AppException.Of("xbox.noAccount", "This Microsoft account has no Xbox profile. Create one at xbox.com first."),
                2148916235 => AppException.Of("xbox.region", "Xbox Live is not available in this account's region."),
                2148916236 or 2148916237 => AppException.Of("xbox.adult", "The account needs adult verification on xbox.com."),
                2148916238 => AppException.Of("xbox.child", "This is a child account; an adult must add it to a Microsoft family first."),
                _ => AppException.Of("xbox.error", $"Xbox Live refused sign-in (XErr {xerr})", ("xerr", xerr)),
            };
        }
        if (!response.IsSuccessStatusCode) throw new HttpStatusException(response.StatusCode, url, Log.Redact(text.Length > 300 ? text[..300] : text));
        using var doc = JsonDocument.Parse(text);
        return doc.RootElement.Clone();
    }

    private static async Task<JsonElement> PostFormAsync(string url, Dictionary<string, string> form, CancellationToken ct)
    {
        using var response = await Http.Client.PostAsync(url, new FormUrlEncodedContent(form), ct).ConfigureAwait(false);
        var text = await response.Content.ReadAsStringAsync(ct).ConfigureAwait(false);
        if (!response.IsSuccessStatusCode)
        {
            if (text.Contains("AADSTS700016", StringComparison.Ordinal) || text.Contains("unauthorized_client", StringComparison.Ordinal))
                throw AppException.Of("msa.clientId", "The configured Azure client ID is not valid for personal Microsoft accounts.");
            throw new HttpStatusException(response.StatusCode, url, Log.Redact(text.Length > 300 ? text[..300] : text));
        }
        using var doc = JsonDocument.Parse(text);
        return doc.RootElement.Clone();
    }

    private void RequireClientId()
    {
        if (string.IsNullOrWhiteSpace(clientId))
            throw AppException.Of("msa.noClientId", "Microsoft sign-in needs an Azure client ID (Settings → Accounts).");
    }
}
