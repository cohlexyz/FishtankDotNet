using System.Net;
using System.Text.Json;
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

    public FishtankTokenService(CancellationToken ct)
    {
        _ct = ct;
        // Fetch immediately on construction (blocking so CurrentToken is available before ClipService starts)
        try
        {
            FetchTokenAsync().GetAwaiter().GetResult();
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
                await FetchTokenAsync();
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

    public async Task FetchTokenAsync()
    {
        var settings = await SettingsProvider.GetMultipleValuesAsync([
            BuiltIn.Keys.FishtankAccessToken,
            BuiltIn.Keys.FishtankRefreshToken
        ]);

        var accessToken = settings[BuiltIn.Keys.FishtankAccessToken].Value;
        var refreshToken = settings[BuiltIn.Keys.FishtankRefreshToken].Value;

        if (string.IsNullOrWhiteSpace(accessToken) || string.IsNullOrWhiteSpace(refreshToken))
        {
            Logger.Warn("[FishtankTokenService] Access or refresh token not configured, skipping fetch");
            return;
        }

        var cookieContainer = new CookieContainer();
        var cookieValue = $"[\"{accessToken}\", \"{refreshToken}\"]";
        cookieContainer.Add(new Uri("https://api.fishtank.live"),
            new Cookie("sb-wcsaaupukpdmqdjcgaoo-auth-token", cookieValue));

        using var handler = new HttpClientHandler { CookieContainer = cookieContainer };
        using var client = new HttpClient(handler) { Timeout = TimeSpan.FromSeconds(15) };

        var response = await client.GetAsync("https://api.fishtank.live/v1/auth", _ct);
        response.EnsureSuccessStatusCode();

        var json = await response.Content.ReadAsStringAsync(_ct);
        using var doc = JsonDocument.Parse(json);

        if (doc.RootElement.TryGetProperty("session", out var session) &&
            session.TryGetProperty("live_stream_token", out var tokenProp))
        {
            CurrentToken = tokenProp.GetString();
            Logger.Info($"[FishtankTokenService] Fetched live stream token ({CurrentToken?.Length ?? 0} chars)");
        }
        else
        {
            Logger.Warn("[FishtankTokenService] Response did not contain session.live_stream_token");
        }
    }
}
