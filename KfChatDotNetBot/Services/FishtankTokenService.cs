using System.Text.Json;
using System.Text.Json.Serialization;
using KfChatDotNetBot.Settings;
using NLog;

namespace KfChatDotNetBot.Services;

public class FishtankTokenService
{
    private static readonly Logger Logger = LogManager.GetCurrentClassLogger();
    private static readonly TimeSpan RefreshInterval = TimeSpan.FromHours(24);

    private readonly CancellationToken _ct;
    private readonly Task _refreshLoop;

    public string? CurrentToken { get; private set; }

    /// <summary>
    /// Fired when the live stream token changes. The parameter is the new token.
    /// </summary>
    public event Action<string>? OnTokenRefreshed;

    /// <summary>
    /// Fired when the camera list should be refreshed (after a successful login/token fetch).
    /// </summary>
    public event Func<Task>? OnCamerasNeedRefresh;

    public FishtankTokenService(CancellationToken ct)
    {
        _ct = ct;
        // Fetch immediately on construction (blocking so CurrentToken is available before ClipService starts)
        try
        {
            EnsureAuthenticatedAsync().GetAwaiter().GetResult();
        }
        catch (Exception ex)
        {
            Logger.Error($"[FishtankTokenService] Initial token fetch failed: {ex.Message}");
        }

        _refreshLoop = Task.Run(RefreshLoopAsync, ct);
    }

    private async Task RefreshLoopAsync()
    {
        using var timer = new PeriodicTimer(RefreshInterval);
        while (await timer.WaitForNextTickAsync(_ct))
        {
            try
            {
                var oldToken = CurrentToken;
                await EnsureAuthenticatedAsync();
                if (CurrentToken != null && CurrentToken != oldToken)
                {
                    Logger.Info("[FishtankTokenService] Token changed, notifying subscribers");
                    OnTokenRefreshed?.Invoke(CurrentToken);
                }
            }
            catch (Exception ex)
            {
                Logger.Error($"[FishtankTokenService] Token refresh failed: {ex.Message}");
            }
        }
    }

    /// <summary>
    /// Checks if stored tokens are expired and logs in again if needed, then fetches the live stream token.
    /// </summary>
    public async Task EnsureAuthenticatedAsync()
    {
        var settings = await SettingsProvider.GetMultipleValuesAsync([
            BuiltIn.Keys.FishtankAccessToken,
            BuiltIn.Keys.FishtankRefreshToken,
            BuiltIn.Keys.FishtankTokenExpiry
        ]);

        var accessToken = settings[BuiltIn.Keys.FishtankAccessToken].Value;
        var refreshToken = settings[BuiltIn.Keys.FishtankRefreshToken].Value;
        var expiryStr = settings[BuiltIn.Keys.FishtankTokenExpiry].Value;

        var needsLogin = string.IsNullOrWhiteSpace(accessToken)
                         || string.IsNullOrWhiteSpace(refreshToken)
                         || IsTokenExpired(expiryStr);

        if (needsLogin)
        {
            Logger.Info("[FishtankTokenService] Tokens missing or expired, attempting email/password login");
            var loginSuccess = await LoginAsync();
            if (!loginSuccess)
            {
                Logger.Warn("[FishtankTokenService] Login failed, attempting Supabase cookie auth with existing tokens");
                if (!string.IsNullOrWhiteSpace(accessToken) && !string.IsNullOrWhiteSpace(refreshToken))
                    await FetchTokenViaSupabaseAsync(accessToken, refreshToken);
                return;
            }
        }
        else
        {
            await FetchTokenViaSupabaseAsync(accessToken!, refreshToken!);
        }
    }

    /// <summary>
    /// Logs in via email/password to /v1/auth/log-in and stores the resulting tokens.
    /// </summary>
    public async Task<bool> LoginAsync()
    {
        var settings = await SettingsProvider.GetMultipleValuesAsync([
            BuiltIn.Keys.FishtankEmail,
            BuiltIn.Keys.FishtankPassword
        ]);

        var email = settings[BuiltIn.Keys.FishtankEmail].Value;
        var password = settings[BuiltIn.Keys.FishtankPassword].Value;

        if (string.IsNullOrWhiteSpace(email) || string.IsNullOrWhiteSpace(password))
        {
            Logger.Warn("[FishtankTokenService] Email or password not configured, cannot login");
            return false;
        }

        using var client = new HttpClient { Timeout = TimeSpan.FromSeconds(15) };
        var payload = JsonSerializer.Serialize(new { email, password });
        using var content = new StringContent(payload, System.Text.Encoding.UTF8, "application/json");

        var response = await client.PostAsync("https://api.fishtank.live/v1/auth/log-in", content, _ct);
        if (!response.IsSuccessStatusCode)
        {
            var errorBody = await response.Content.ReadAsStringAsync(_ct);
            Logger.Error($"[FishtankTokenService] Login failed with status {response.StatusCode}: {errorBody}");
            return false;
        }

        var json = await response.Content.ReadAsStringAsync(_ct);
        using var doc = JsonDocument.Parse(json);

        if (!doc.RootElement.TryGetProperty("session", out var session))
        {
            Logger.Error("[FishtankTokenService] Login response missing 'session' property");
            return false;
        }

        var accessToken = session.GetProperty("access_token").GetString();
        var refreshToken = session.GetProperty("refresh_token").GetString();
        var expiresIn = session.GetProperty("expires_in").GetInt64();

        if (string.IsNullOrWhiteSpace(accessToken) || string.IsNullOrWhiteSpace(refreshToken))
        {
            Logger.Error("[FishtankTokenService] Login response missing access/refresh tokens");
            return false;
        }

        // Store tokens and expiry
        var expiry = DateTimeOffset.UtcNow.AddSeconds(expiresIn);
        await SettingsProvider.SetValueAsync(BuiltIn.Keys.FishtankAccessToken, accessToken!);
        await SettingsProvider.SetValueAsync(BuiltIn.Keys.FishtankRefreshToken, refreshToken!);
        await SettingsProvider.SetValueAsync(BuiltIn.Keys.FishtankTokenExpiry, expiry.ToString("O"));

        Logger.Info($"[FishtankTokenService] Login successful, tokens expire at {expiry:u}");

        // Extract live_stream_token if present
        if (session.TryGetProperty("live_stream_token", out var tokenProp))
        {
            CurrentToken = tokenProp.GetString();
            Logger.Info($"[FishtankTokenService] Got live stream token from login ({CurrentToken?.Length ?? 0} chars)");
        }

        return true;
    }

    /// <summary>
    /// Fetches the live stream token using existing Supabase access/refresh tokens via cookie auth.
    /// </summary>
    public async Task FetchTokenViaSupabaseAsync(string accessToken, string refreshToken)
    {
        var cookieValue = $"[\"{accessToken}\", \"{refreshToken}\"]";

        using var client = new HttpClient { Timeout = TimeSpan.FromSeconds(15) };
        using var request = new HttpRequestMessage(HttpMethod.Get, "https://api.fishtank.live/v1/auth");
        request.Headers.Add("Cookie", $"sb-wcsaaupukpdmqdjcgaoo-auth-token={cookieValue}");

        var response = await client.SendAsync(request, _ct);
        if (!response.IsSuccessStatusCode)
        {
            Logger.Warn($"[FishtankTokenService] Supabase auth returned {response.StatusCode}, will retry with login next cycle");
            // Clear expiry so next refresh triggers login
            await SettingsProvider.SetValueAsync(BuiltIn.Keys.FishtankTokenExpiry, "");
            return;
        }

        var json = await response.Content.ReadAsStringAsync(_ct);
        using var doc = JsonDocument.Parse(json);

        if (doc.RootElement.TryGetProperty("session", out var session))
        {
            if (session.TryGetProperty("live_stream_token", out var tokenProp))
            {
                CurrentToken = tokenProp.GetString();
                Logger.Info($"[FishtankTokenService] Fetched live stream token via Supabase ({CurrentToken?.Length ?? 0} chars)");
            }
            else
            {
                Logger.Warn("[FishtankTokenService] Supabase response did not contain session.live_stream_token");
            }

            // Update stored tokens if they were refreshed
            if (session.TryGetProperty("access_token", out var newAccess) &&
                session.TryGetProperty("refresh_token", out var newRefresh))
            {
                var newAccessStr = newAccess.GetString();
                var newRefreshStr = newRefresh.GetString();
                if (!string.IsNullOrWhiteSpace(newAccessStr) && !string.IsNullOrWhiteSpace(newRefreshStr))
                {
                    await SettingsProvider.SetValueAsync(BuiltIn.Keys.FishtankAccessToken, newAccessStr!);
                    await SettingsProvider.SetValueAsync(BuiltIn.Keys.FishtankRefreshToken, newRefreshStr!);

                    if (session.TryGetProperty("expires_in", out var expiresIn))
                    {
                        var expiry = DateTimeOffset.UtcNow.AddSeconds(expiresIn.GetInt64());
                        await SettingsProvider.SetValueAsync(BuiltIn.Keys.FishtankTokenExpiry, expiry.ToString("O"));
                    }

                    Logger.Info("[FishtankTokenService] Updated stored tokens from Supabase refresh");
                }
            }
        }
        else
        {
            Logger.Warn("[FishtankTokenService] Supabase response did not contain 'session'");
        }
    }

    /// <summary>
    /// Fetches the live-streams list from the Fishtank API and returns camera data including load balancer domains.
    /// </summary>
    public async Task<FishtankLiveStreamsResponse?> FetchLiveStreamsAsync()
    {
        using var client = new HttpClient { Timeout = TimeSpan.FromSeconds(15) };
        var response = await client.GetAsync("https://api.fishtank.live/v1/live-streams", _ct);
        response.EnsureSuccessStatusCode();

        var json = await response.Content.ReadAsStringAsync(_ct);
        return JsonSerializer.Deserialize<FishtankLiveStreamsResponse>(json);
    }

    private static bool IsTokenExpired(string? expiryStr)
    {
        if (string.IsNullOrWhiteSpace(expiryStr))
            return true;

        if (!DateTimeOffset.TryParse(expiryStr, out var expiry))
            return true;

        // Consider expired if less than 1 hour remains
        return DateTimeOffset.UtcNow >= expiry.AddHours(-1);
    }
}

public class FishtankLiveStream
{
    [JsonPropertyName("id")]
    public string Id { get; set; } = "";

    [JsonPropertyName("name")]
    public string Name { get; set; } = "";

    [JsonPropertyName("playbackId")]
    public string PlaybackId { get; set; } = "";

    [JsonPropertyName("streamId")]
    public string StreamId { get; set; } = "";

    [JsonPropertyName("season")]
    public string Season { get; set; } = "";

    [JsonPropertyName("interactive")]
    public bool Interactive { get; set; }

    [JsonPropertyName("access")]
    public string Access { get; set; } = "";

    [JsonPropertyName("hidden")]
    public bool Hidden { get; set; }

    [JsonPropertyName("order")]
    public int Order { get; set; }

    [JsonPropertyName("goesLiveAt")]
    public long? GoesLiveAt { get; set; }

    [JsonPropertyName("excludeFromGrid")]
    public bool ExcludeFromGrid { get; set; }

    [JsonPropertyName("sharesGridWith")]
    public string? SharesGridWith { get; set; }
}

public class FishtankLiveStreamsResponse
{
    [JsonPropertyName("liveStreams")]
    public List<FishtankLiveStream> LiveStreams { get; set; } = [];

    [JsonPropertyName("liveStreamStatus")]
    public Dictionary<string, string> LiveStreamStatus { get; set; } = new();

    [JsonPropertyName("loadBalancer")]
    public Dictionary<string, string> LoadBalancer { get; set; } = new();

    [JsonPropertyName("region")]
    public string Region { get; set; } = "";
}
