using System.Net;
using System.Net.WebSockets;
using System.Numerics;
using System.Security.Cryptography;
using System.Text;
using System.Text.Json;
using Isopoh.Cryptography.Argon2;
using Isopoh.Cryptography.Blake2b;
using Isopoh.Cryptography.SecureArray;
using NLog;
using Websocket.Client;

namespace KfChatDotNetBot.Services;

public class TartarusCaptcha(string kfDomain, CookieContainer cookies, string? proxy = null, CancellationToken? cancellationToken = null)
{
    private readonly Logger _logger = LogManager.GetCurrentClassLogger();
    private readonly CancellationToken _ctx = cancellationToken ?? CancellationToken.None;

    public const string DefaultSiteKey = "09a19d2b-a47f-4917-af9a-d0f007a1deef";

    // The argon2 salt is a fixed literal, NOT the per-round `salt` field. The per-round
    // salt is concatenated with the decimal nonce to form the argon2 password.
    private const string PowSaltLiteral = "tartarus-pow-v1!";

    // The whole ceremony must finish inside the session_token's 120s life.
    private const double CeremonyBudgetSeconds = 110.0;

    private const string UserAgent =
        "Mozilla/5.0 (X11; Linux x86_64) AppleWebKit/537.36 (KHTML, like Gecko) Chrome/150.0.0.0 Safari/537.36";

    private HttpClient CreateClient()
    {
        var handler = new HttpClientHandler { AutomaticDecompression = DecompressionMethods.All };
        handler.CookieContainer = cookies;
        if (string.IsNullOrEmpty(proxy) == false)
        {
            handler.Proxy = new WebProxy(proxy);
            handler.UseProxy = true;
        }

        var client = new HttpClient(handler);
        client.DefaultRequestHeaders.UserAgent.ParseAdd(UserAgent);
        client.DefaultRequestHeaders.TryAddWithoutValidation("Origin", $"https://{kfDomain}");
        return client;
    }

    public async Task<string> RunCeremonyAsync(string siteKey)
    {
        var deadline = DateTime.UtcNow.AddSeconds(CeremonyBudgetSeconds);
        using var client = CreateClient();

        _logger.Info("[tartarus] starting captcha ceremony");
        var startResponse = await client.GetAsync(
            $"https://{kfDomain}/.ttrs/captcha/start?key={Uri.EscapeDataString(siteKey)}", _ctx);
        if (!startResponse.IsSuccessStatusCode)
        {
            var body = await startResponse.Content.ReadAsStringAsync(_ctx);
            throw new Exception($"/start -> HTTP {(int)startResponse.StatusCode}: {body[..Math.Min(200, body.Length)]}");
        }

        using var startJson = JsonDocument.Parse(await startResponse.Content.ReadAsStringAsync(_ctx));
        var root = startJson.RootElement;
        var status = root.GetProperty("status").GetString();
        if (status != "challenge")
            throw new Exception($"/start unexpected status: {status}");

        var totalRounds = root.TryGetProperty("total_rounds", out var tr) ? tr.GetInt32() : 0;
        var token = root.GetProperty("session_token").GetString()!;
        var salt = root.GetProperty("salt").GetString()!;
        var difficulty = root.GetProperty("difficulty").GetInt32();
        var algorithm = root.TryGetProperty("algorithm", out var alg) ? alg.GetString() ?? "argon2id" : "argon2id";
        var mCost = root.TryGetProperty("argon2_m_cost", out var m) ? m.GetInt32() : 256;
        var tCost = root.TryGetProperty("argon2_t_cost", out var t) ? t.GetInt32() : 1;
        var pCost = root.TryGetProperty("argon2_p_cost", out var p) ? p.GetInt32() : 1;

        if (root.TryGetProperty("monocle_enabled", out var monocleEnabled) && monocleEnabled.GetBoolean())
            _logger.Warn("[tartarus] monocle_enabled is true; a Spur assessment may be required");

        _logger.Info($"[tartarus] {totalRounds} rounds, {algorithm} difficulty {difficulty} (m={mCost} t={tCost} p={pCost})");

        var round = 0;
        while (true)
        {
            var nonce = Solve(salt, difficulty, algorithm, mCost, tCost, pCost, deadline);
            _logger.Debug($"[tartarus] round {round} solved nonce={nonce}");

            var formData = new FormUrlEncodedContent(new List<KeyValuePair<string, string>>
            {
                new("session_token", token),
                new("salt", salt),
                new("nonce", nonce.ToString()),
                new("difficulty", difficulty.ToString())
            });
            var verifyResponse = await client.PostAsync($"https://{kfDomain}/.ttrs/captcha/verify", formData, _ctx);
            if (!verifyResponse.IsSuccessStatusCode)
            {
                var body = await verifyResponse.Content.ReadAsStringAsync(_ctx);
                throw new Exception($"/verify -> HTTP {(int)verifyResponse.StatusCode}: {body[..Math.Min(200, body.Length)]}");
            }

            using var verifyJson = JsonDocument.Parse(await verifyResponse.Content.ReadAsStringAsync(_ctx));
            var res = verifyJson.RootElement;
            var state = res.GetProperty("status").GetString();

            switch (state)
            {
                case "next_round":
                    token = res.GetProperty("session_token").GetString()!;
                    salt = res.GetProperty("salt").GetString()!;
                    difficulty = res.GetProperty("difficulty").GetInt32();
                    algorithm = res.TryGetProperty("algorithm", out var a) ? a.GetString() ?? algorithm : algorithm;
                    mCost = res.TryGetProperty("argon2_m_cost", out var mc) ? mc.GetInt32() : mCost;
                    tCost = res.TryGetProperty("argon2_t_cost", out var tc) ? tc.GetInt32() : tCost;
                    pCost = res.TryGetProperty("argon2_p_cost", out var pc) ? pc.GetInt32() : pCost;
                    round++;
                    continue;

                case "complete":
                case "verified":
                    return res.GetProperty("verification_token").GetString()!;

                case "bandwidth":
                    var size = res.TryGetProperty("bandwidth_size", out var bs) ? bs.GetInt32() : 0;
                    var bandwidthToken = res.GetProperty("session_token").GetString()!;
                    _logger.Info($"[tartarus] entering bandwidth stage, {size} bytes");
                    var result = await RunBandwidthAsync(bandwidthToken, deadline);
                    if (result.next == "complete")
                        return result.token;
                    throw new NotSupportedException(
                        $"bandwidth completed but next stage is {result.next}; not implemented (likely Monocle)");

                case "monocle":
                    throw new NotSupportedException(
                        "server requires a Monocle assessment (status:'monocle'); not implemented");

                case "blocked":
                    var blockedMessage = res.TryGetProperty("message", out var bm) ? bm.GetString() : null;
                    throw new Exception($"ceremony blocked: {blockedMessage} (likely IP reputation / proxy detection)");

                default:
                    throw new Exception($"unexpected /verify status: {state} ({res.GetRawText()})");
            }
        }
    }

    private long Solve(string salt, int difficulty, string algorithm, int mCost, int tCost, int pCost, DateTime deadline)
    {
        return algorithm switch
        {
            "argon2id" => SolveArgon2Id(salt, difficulty, mCost, tCost, pCost, deadline),
            "sha256" => SolveSha256(salt, difficulty, deadline),
            _ => throw new Exception($"unknown PoW algorithm {algorithm}")
        };
    }

    private static long SolveArgon2Id(string salt, int difficulty, int mCost, int tCost, int pCost, DateTime deadline)
    {
        var passwordPrefix = Encoding.UTF8.GetBytes(salt);
        var argon2Salt = Encoding.UTF8.GetBytes(PowSaltLiteral);
        long nonce = 0;
        while (true)
        {
            var nonceBytes = Encoding.UTF8.GetBytes(nonce.ToString());
            var password = new byte[passwordPrefix.Length + nonceBytes.Length];
            Buffer.BlockCopy(passwordPrefix, 0, password, 0, passwordPrefix.Length);
            Buffer.BlockCopy(nonceBytes, 0, password, passwordPrefix.Length, nonceBytes.Length);

            var config = new Argon2Config
            {
                Type = Argon2Type.HybridAddressing,
                Version = Argon2Version.Nineteen,
                MemoryCost = mCost,
                TimeCost = tCost,
                Lanes = pCost,
                Threads = pCost,
                Password = password,
                Salt = argon2Salt,
                HashLength = 32
            };

            int leadingZeros;
            using (var hash = new Argon2(config).Hash())
            {
                var buffer = hash.Buffer;
                var v = (uint)(buffer[0] << 24) | (uint)(buffer[1] << 16) | (uint)(buffer[2] << 8) | buffer[3];
                leadingZeros = BitOperations.LeadingZeroCount(v);
            }

            if (leadingZeros >= difficulty)
                return nonce;

            nonce++;
            if (nonce % 256 == 0 && DateTime.UtcNow >= deadline)
                throw new TimeoutException($"argon2id difficulty {difficulty} unsolved within budget ({nonce} nonces tried)");
        }
    }

    private static long SolveSha256(string salt, int difficulty, DateTime deadline)
    {
        long nonce = 0;
        while (true)
        {
            var input = Encoding.UTF8.GetBytes($"{salt}{nonce}");
            var hash = SHA256.HashData(input);
            var v = (uint)(hash[0] << 24) | (uint)(hash[1] << 16) | (uint)(hash[2] << 8) | hash[3];
            if (BitOperations.LeadingZeroCount(v) >= difficulty)
                return nonce;

            nonce++;
            if (nonce % 8192 == 0 && DateTime.UtcNow >= deadline)
                throw new TimeoutException($"sha256 difficulty {difficulty} unsolved within budget");
        }
    }

    private async Task<(string token, string? next, int? expiresIn)> RunBandwidthAsync(string token, DateTime deadline)
    {
        var uri = new Uri($"wss://{kfDomain}/.ttrs/captcha/bandwidth?token={Uri.EscapeDataString(token)}");
        var cookieHeader = string.Join("; ", cookies.GetAllCookies().Select(c => $"{c.Name}={c.Value}"));

        var factory = new Func<ClientWebSocket>(() =>
        {
            var clientWs = new ClientWebSocket();
            clientWs.Options.SetRequestHeader("Origin", $"https://{kfDomain}");
            clientWs.Options.SetRequestHeader("User-Agent", UserAgent);
            if (!string.IsNullOrEmpty(cookieHeader))
                clientWs.Options.SetRequestHeader("Cookie", cookieHeader);
            if (!string.IsNullOrEmpty(proxy))
                clientWs.Options.Proxy = new WebProxy(proxy);
            return clientWs;
        });

        var client = new WebsocketClient(uri, factory)
        {
            IsReconnectionEnabled = false
        };

        var tcs = new TaskCompletionSource<(string token, string? next, int? expiresIn)>(
            TaskCreationOptions.RunContinuationsAsynchronously);
        using var hasher = Blake2B.Create(new Blake2BConfig { OutputSizeInBytes = 32 }, (SecureArrayCall)null!);
        long received = 0;

        client.MessageReceived.Subscribe(msg =>
        {
            try
            {
                if (msg.MessageType == WebSocketMessageType.Binary && msg.Binary != null)
                {
                    hasher.Update(msg.Binary);
                    received += msg.Binary.Length;
                    return;
                }

                if (msg.MessageType != WebSocketMessageType.Text || msg.Text == null)
                    return;

                using var doc = JsonDocument.Parse(msg.Text);
                var status = doc.RootElement.GetProperty("status").GetString();
                switch (status)
                {
                    case "verify":
                        _logger.Debug($"[tartarus] bandwidth received {received} bytes; sending proof");
                        var proof = Convert.ToHexString(hasher.Finish()).ToLowerInvariant();
                        client.Send($"{{\"proof\":\"{proof}\"}}");
                        break;

                    case "ok":
                        var nextToken = doc.RootElement.TryGetProperty("token", out var t) ? t.GetString() : null;
                        var next = doc.RootElement.TryGetProperty("next", out var n) ? n.GetString() : null;
                        var expiresIn = doc.RootElement.TryGetProperty("expires_in", out var e) ? e.GetInt32() : (int?)null;
                        tcs.TrySetResult((nextToken!, next, expiresIn));
                        break;

                    case "fail":
                        var failMessage = doc.RootElement.TryGetProperty("message", out var fm) ? fm.GetString() : null;
                        tcs.TrySetException(new Exception($"bandwidth challenge failed: {failMessage}"));
                        break;
                }
            }
            catch (Exception e)
            {
                tcs.TrySetException(e);
            }
        });

        client.DisconnectionHappened.Subscribe(info =>
        {
            if (!tcs.Task.IsCompleted)
                tcs.TrySetException(new Exception(
                    $"bandwidth WS disconnected: {info.Type} {info.CloseStatus} {info.Exception?.Message}"));
        });

        await client.Start();
        try
        {
            using var timeoutCts = CancellationTokenSource.CreateLinkedTokenSource(_ctx);
            var remaining = deadline - DateTime.UtcNow;
            if (remaining < TimeSpan.Zero) remaining = TimeSpan.Zero;
            timeoutCts.CancelAfter(remaining);
            return await tcs.Task.WaitAsync(timeoutCts.Token);
        }
        finally
        {
            client.Dispose();
        }
    }
}
