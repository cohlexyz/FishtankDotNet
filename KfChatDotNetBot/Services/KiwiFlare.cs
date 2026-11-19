using System.Collections;
using System.Net;
using System.Net.Http.Json;
using System.Net.WebSockets;
using System.Security.Cryptography;
using System.Text;
using System.Text.Json;
using HtmlAgilityPack;
using NLog;

namespace KfChatDotNetBot.Services;

// Shoutout y a t s for making the original Go implementation I adapted for this
// https://github.com/y-a-t-s/firebird
public class KiwiFlare(string kfDomain, string? proxy = null, CancellationToken? cancellationToken = null)
{
    private readonly Logger _logger = LogManager.GetCurrentClassLogger();
    private readonly CancellationToken _ctx = cancellationToken ?? CancellationToken.None;
    private readonly Random _random = new();

    private HttpClientHandler GetHttpClientHandler()
    {
        var handler = new HttpClientHandler { AutomaticDecompression = DecompressionMethods.All };
        if (string.IsNullOrEmpty(proxy) == false)
        {
            handler.Proxy = new WebProxy(proxy);
            handler.UseProxy = true;
        }

        return handler;
    }

    /// <param name="cookies">Cookie jar to use. Pass the jar the rest of the bot uses so anything TTRS sets during
    /// the challenge (not just the final clearance) carries over, the same way it would in a browser</param>
    public async Task<KiwiFlareChallengeModel?> GetChallenge(CookieContainer? cookies = null)
    {
        var challengeHandler = GetHttpClientHandler();
        if (cookies != null) challengeHandler.CookieContainer = cookies;
        using var client = new HttpClient(challengeHandler);
        client.Timeout = TimeSpan.FromSeconds(10);
        var response = await client.GetAsync($"https://{kfDomain}/", _ctx);
        var document = new HtmlDocument();
        document.Load(await response.Content.ReadAsStreamAsync(_ctx));
        var pow = "sssg";
        var challengeData = document.DocumentNode.SelectSingleNode($"//html[@id=\"{pow}\"]");
        if (challengeData == null)
        {
            _logger.Info("challengeData was null. Couldn't find html element with id = sssg, trying ttrs");
            pow = "ttrs";
            challengeData = document.DocumentNode.SelectSingleNode($"//html[@id=\"{pow}\"]");
            if (challengeData == null)
            {
                _logger.Info("challengeData was still null even looking for ttrs");
                return null;
            }
        }

        if (!challengeData.Attributes.Contains($"data-{pow}-challenge")) throw new Exception($"data-{pow}-challenge attribute missing");
        if (!challengeData.Attributes.Contains($"data-{pow}-difficulty")) throw new Exception($"data-{pow}-difficulty attribute missing");
        var patience = TimeSpan.FromMinutes(5);
        // ttrs has no patience value
        if (challengeData.Attributes.Contains("data-sssg-patience"))
        {
            patience = TimeSpan.FromMinutes(Convert.ToDouble(challengeData.Attributes["data-sssg-patience"].Value));
        }
        var salt = challengeData.Attributes[$"data-{pow}-challenge"].Value;
        var difficulty = Convert.ToInt32(challengeData.Attributes[$"data-{pow}-difficulty"].Value);
        _logger.Info($"Got {pow} challenge parameters. IsTtrs = {pow == "ttrs"}, Salt = {salt}, Difficulty = {difficulty}, Patience = {patience.TotalMinutes} minutes");
        return new KiwiFlareChallengeModel
        {
            Salt = salt,
            Difficulty = difficulty,
            Patience = patience,
            IsTtrs = pow == "ttrs"
        };
    }
    
    private bool TestHash(byte[] hash, int difficulty)
    {
        for (var i = 0; i < difficulty; i++)
        {
            var byteIndex = i / 8;
            var bitIndex = 7 - (i % 8); // MSB first within each byte
            if ((hash[byteIndex] & (1 << bitIndex)) != 0)
            {
                return false;
            }
        }
        return true;
    }

    private Task<KiwiFlareChallengeSolutionModel> ChallengeWorker(KiwiFlareChallengeModel challenge)
    {
        var nonce = _random.NextInt64();
        while (true)
        {
            nonce++;
            var input = Encoding.UTF8.GetBytes($"{challenge.Salt}{nonce}");
            if (!TestHash(SHA256.HashData(input), challenge.Difficulty)) continue;
            _logger.Info($"Hash passed the test, nonce: {nonce}");
            return Task.FromResult(new KiwiFlareChallengeSolutionModel
            {
                Nonce = nonce,
                Salt = challenge.Salt
            });
        }
    }

    public async Task<KiwiFlareChallengeSolutionModel> SolveChallenge(KiwiFlareChallengeModel challenge)
    {
        var start = DateTime.UtcNow;
        var worker = Task.Run(() => ChallengeWorker(challenge), _ctx);
        try
        {
            await worker.WaitAsync(challenge.Patience, _ctx);
        }
        catch (Exception e)
        {
            _logger.Error("Caught an exception while trying to solve the challenge. Probably timed out.");
            _logger.Error(e);
            throw;
        }

        if (worker.IsFaulted)
        {
            _logger.Error("Challenge worker faulted");
            _logger.Error(worker.Exception);
            throw new Exception("Challenge worker faulted");
        }

        _logger.Debug($"Worker solved the challenge after {(DateTime.UtcNow - start).TotalMilliseconds} ms");
        return worker.Result;
    }

    public async Task<string> SubmitAnswer(KiwiFlareChallengeSolutionModel solution)
    {
        using var client = new HttpClient(GetHttpClientHandler());
        client.Timeout = TimeSpan.FromSeconds(10);
        var formData = new FormUrlEncodedContent(new List<KeyValuePair<string, string>>
        {
            new("a", solution.Salt),
            new("b", solution.Nonce.ToString())
        });

        var response = await client.PostAsync($"https://{kfDomain}/.sssg/api/answer", formData, _ctx);
        var json = await response.Content.ReadFromJsonAsync<JsonElement>(_ctx);
        if (json.TryGetProperty("error", out var error))
        {
            _logger.Error($"Received error when submitting the answer: {error.GetString()}");
            throw new Exception($"sssg returned an error when submitting the answer: {error.GetString()}");
        }

        if (json.TryGetProperty("auth", out var auth))
        {
            return auth.GetString() ?? throw new InvalidOperationException("Caught null when retrieving auth property");
        }

        _logger.Error("Auth property was missing from sssg response");
        _logger.Error(json.GetRawText());
        throw new Exception($"Auth property was missing from sssg response: {json.GetRawText()}");
    }

    /// <param name="cookies">Cookie jar to use, see GetChallenge. A throwaway one is used if null</param>
    public async Task<string> SubmitAnswerTtrs(KiwiFlareChallengeSolutionModel solution, CookieContainer? cookies = null)
    {
        var handler = GetHttpClientHandler();
        var container = cookies ?? new CookieContainer();
        handler.CookieContainer = container;
        handler.AllowAutoRedirect = false;
        using var client = new HttpClient(handler);
        client.Timeout = TimeSpan.FromSeconds(10);

        var (response, body) = await PostTtrsAnswer(client, solution, null);
        var token = GetTtrsClearance(container, response);
        if (!string.IsNullOrEmpty(token)) return token;

        // TTRS can ask for a second stage where we prove we pulled down a chunk of data over a websocket. The
        // server hands back a receipt for it which goes along with the PoW answer a second time
        if (ParseJson(body) is { ValueKind: JsonValueKind.Object } offer && offer.TryGetProperty("next", out var next) &&
            next.GetString() == "bandwidth")
        {
            var offerToken = offer.GetProperty("token").GetString() ?? throw new Exception("Bandwidth offer had a null token");
            var size = offer.TryGetProperty("bandwidth_size", out var sizeEl) ? sizeEl.GetInt64() : 0;
            var timeout = offer.TryGetProperty("bandwidth_timeout_secs", out var timeoutEl)
                ? TimeSpan.FromSeconds(timeoutEl.GetInt32())
                : TimeSpan.FromMinutes(2);
            _logger.Info($"ttrs wants a bandwidth challenge of {size} bytes, timeout {timeout.TotalSeconds} seconds");
            var receipt = await RunBandwidthChallenge(offerToken, container, timeout);
            (response, body) = await PostTtrsAnswer(client, solution, receipt);
            token = GetTtrsClearance(container, response);
            if (!string.IsNullOrEmpty(token)) return token;
        }

        string? reason = null;
        if (ParseJson(body) is { ValueKind: JsonValueKind.Object } json)
        {
            foreach (var key in (string[])["reason", "error", "message"])
            {
                if (json.TryGetProperty(key, out var value)) { reason = value.ToString(); break; }
            }
        }

        _logger.Error($"ttrs didn't give us a clearance cookie. Status {(int)response.StatusCode}, reason: {reason ?? "none given"}, body: {body}");
        throw new Exception($"ttrs didn't accept our solution (HTTP {(int)response.StatusCode}): {reason ?? body}");
    }

    private async Task<(HttpResponseMessage Response, string Body)> PostTtrsAnswer(HttpClient client,
        KiwiFlareChallengeSolutionModel solution, string? bandwidthReceipt)
    {
        var fields = new List<KeyValuePair<string, string>>
        {
            new("salt", solution.Salt),
            new("nonce", solution.Nonce.ToString())
        };
        if (bandwidthReceipt != null) fields.Add(new("bandwidth_receipt", bandwidthReceipt));
        var response = await client.PostAsync($"https://{kfDomain}/.ttrs/challenge", new FormUrlEncodedContent(fields), _ctx);
        var body = await response.Content.ReadAsStringAsync(_ctx);
        _logger.Debug($"ttrs answer response (with receipt: {bandwidthReceipt != null}): {(int)response.StatusCode} {body}");
        return (response, body);
    }

    // The clearance cookie is what actually matters. The JSON body's shape has changed before (no more "success"
    // property) so don't depend on it
    private string? GetTtrsClearance(CookieContainer container, HttpResponseMessage response)
    {
        var token = container.GetCookies(new Uri($"https://{kfDomain}/"))["ttrs_clearance"]?.Value;
        if (string.IsNullOrEmpty(token) && response.Headers.TryGetValues("Set-Cookie", out var setCookies))
        {
            var header = setCookies.FirstOrDefault(h => h.Contains("ttrs_clearance="));
            token = header?.Split("ttrs_clearance=")[1].Split(';')[0];
        }

        if (!string.IsNullOrEmpty(token)) _logger.Debug($"Parsed ttrs_clearance token: {token}");
        return token;
    }

    private static JsonElement? ParseJson(string body)
    {
        try
        {
            return JsonSerializer.Deserialize<JsonElement>(body);
        }
        catch (JsonException)
        {
            return null;
        }
    }

    /// <summary>
    /// Mirrors /.ttrs/static/challenge-bandwidth.js: the server streams binary frames over a websocket, we SHA-256
    /// each frame, and when it says verify we send back SHA-256 of all the frame hashes concatenated (as hex).
    /// It answers with a receipt that gets submitted alongside the PoW answer
    /// </summary>
    private async Task<string> RunBandwidthChallenge(string offerToken, CookieContainer cookies, TimeSpan timeout)
    {
        using var ws = new ClientWebSocket();
        ws.Options.Cookies = cookies;
        if (!string.IsNullOrEmpty(proxy))
        {
            ws.Options.Proxy = new WebProxy(proxy);
        }

        using var cts = CancellationTokenSource.CreateLinkedTokenSource(_ctx);
        cts.CancelAfter(timeout);
        var uri = new Uri($"wss://{kfDomain}/.ttrs/captcha/bandwidth?token={Uri.EscapeDataString(offerToken)}&proof=sha256");
        await ws.ConnectAsync(uri, cts.Token);

        var frameHashes = new MemoryStream();
        var message = new MemoryStream();
        var buffer = new byte[64 * 1024];
        long received = 0;
        var start = DateTime.UtcNow;
        while (true)
        {
            message.SetLength(0);
            WebSocketReceiveResult result;
            do
            {
                result = await ws.ReceiveAsync(buffer, cts.Token);
                if (result.MessageType == WebSocketMessageType.Close)
                {
                    throw new Exception($"Bandwidth websocket closed before completion: {result.CloseStatus} {result.CloseStatusDescription}");
                }
                message.Write(buffer, 0, result.Count);
            } while (!result.EndOfMessage);

            if (result.MessageType == WebSocketMessageType.Binary)
            {
                // Each websocket message is hashed on its own, same as the browser does per ArrayBuffer
                frameHashes.Write(SHA256.HashData(message.GetBuffer().AsSpan(0, (int)message.Length)));
                received += message.Length;
                continue;
            }

            var text = Encoding.UTF8.GetString(message.GetBuffer(), 0, (int)message.Length);
            _logger.Debug($"Bandwidth websocket said: {text}");
            if (ParseJson(text) is not { ValueKind: JsonValueKind.Object } json || !json.TryGetProperty("status", out var statusEl))
            {
                continue;
            }

            var status = statusEl.GetString();
            if (status == "verify")
            {
                var proof = Convert.ToHexStringLower(SHA256.HashData(frameHashes.ToArray()));
                _logger.Info($"Received {received} bytes in {(DateTime.UtcNow - start).TotalSeconds:N1}s, sending bandwidth proof");
                var payload = Encoding.UTF8.GetBytes(JsonSerializer.Serialize(new { proof }));
                await ws.SendAsync(payload, WebSocketMessageType.Text, true, cts.Token);
            }
            else if (status == "ok")
            {
                var nextStage = json.TryGetProperty("next", out var nextEl) ? nextEl.GetString() : null;
                var receipt = json.TryGetProperty("token", out var tokenEl) ? tokenEl.GetString() : null;
                try
                {
                    await ws.CloseAsync(WebSocketCloseStatus.NormalClosure, null, CancellationToken.None);
                }
                catch (Exception e)
                {
                    _logger.Debug($"Ignoring error closing the bandwidth websocket: {e.Message}");
                }

                if (nextStage != "challenge" || string.IsNullOrEmpty(receipt))
                {
                    throw new Exception($"Bandwidth challenge did not return a receipt: {text}");
                }

                _logger.Info("Bandwidth challenge passed");
                return receipt;
            }
            else if (status == "fail")
            {
                var reason = json.TryGetProperty("message", out var msgEl) ? msgEl.GetString() : null;
                throw new Exception($"Bandwidth challenge failed: {reason ?? text}");
            }
        }
    }
}

public class KiwiFlareChallengeModel
{
    public required string Salt { get; set; }
    public required int Difficulty { get; set; }
    public required TimeSpan Patience { get; set; }
    public required bool IsTtrs { get; set; }
}

public class KiwiFlareChallengeSolutionModel
{
    public required string Salt { get; set; }
    public required long Nonce { get; set; }
}