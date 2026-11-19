using System.Collections;
using System.Net;
using System.Net.Http.Json;
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

    public async Task<KiwiFlareChallengeModel?> GetChallenge()
    {
        using var client = new HttpClient(GetHttpClientHandler());
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

    public async Task<string> SubmitAnswerTtrs(KiwiFlareChallengeSolutionModel solution)
    {
        var handler = GetHttpClientHandler();
        var container = new CookieContainer();
        handler.CookieContainer = container;
        handler.AllowAutoRedirect = false;
        using var client = new HttpClient(handler);
        client.Timeout = TimeSpan.FromSeconds(10);
        var formData = new FormUrlEncodedContent(new List<KeyValuePair<string, string>>
        {
            new("salt", solution.Salt),
            new("nonce", solution.Nonce.ToString())
        });

        var response = await client.PostAsync($"https://{kfDomain}/.ttrs/challenge", formData, _ctx);
        var body = await response.Content.ReadAsStringAsync(_ctx);
        _logger.Debug($"ttrs answer response: {(int)response.StatusCode} {body}");

        // The clearance cookie is what actually matters. The JSON body's shape has changed before (no more
        // "success" property) so don't depend on it, only use it to explain a failure
        var token = container.GetCookies(new Uri($"https://{kfDomain}/"))["ttrs_clearance"]?.Value;
        if (string.IsNullOrEmpty(token) && response.Headers.TryGetValues("Set-Cookie", out var setCookies))
        {
            _logger.Debug($"Set-Cookie header -> {JsonSerializer.Serialize(setCookies)}");
            var header = setCookies.FirstOrDefault(h => h.Contains("ttrs_clearance="));
            token = header?.Split("ttrs_clearance=")[1].Split(';')[0];
        }

        if (!string.IsNullOrEmpty(token))
        {
            _logger.Debug($"Parsed ttrs_clearance token: {token}");
            return token;
        }

        string? reason = null;
        try
        {
            var json = JsonSerializer.Deserialize<JsonElement>(body);
            if (json.ValueKind == JsonValueKind.Object)
            {
                foreach (var key in (string[])["reason", "error", "message"])
                {
                    if (json.TryGetProperty(key, out var value)) { reason = value.ToString(); break; }
                }
            }
        }
        catch (JsonException)
        {
            // Not JSON, the raw body goes in the exception below
        }

        _logger.Error($"ttrs didn't give us a clearance cookie. Status {(int)response.StatusCode}, reason: {reason ?? "none given"}, body: {body}");
        throw new Exception($"ttrs didn't accept our solution (HTTP {(int)response.StatusCode}): {reason ?? body}");
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