using System.Runtime.Caching;
using System.Text.Json;
using System.Text.RegularExpressions;
using KfChatDotNetBot.Models;
using KfChatDotNetBot.Models.DbModels;
using KfChatDotNetWsClient.Models.Events;
using NLog;

namespace KfChatDotNetBot.Commands;

public class PrayerTimerCommand : ICommand
{
    public List<Regex> Patterns => [
        new Regex(@"^prayertimer?$", RegexOptions.IgnoreCase),
        new Regex(@"^praytime$", RegexOptions.IgnoreCase),
        new Regex(@"^nextprayer$", RegexOptions.IgnoreCase)
    ];
    public string? HelpText => "Get time until next Islam prayer in Georgia (US) time";
    public UserRight RequiredRight => UserRight.Guest;
    public TimeSpan Timeout => TimeSpan.FromSeconds(15);
    public RateLimitOptionsModel? RateLimitOptions => new()
    {
        MaxInvocations = 1,
        Window = TimeSpan.FromSeconds(30)
    };

    private static readonly Logger Logger = LogManager.GetCurrentClassLogger();
    private const string CacheKey = "PrayerTimings:Atlanta";
    private static readonly string[] MainPrayers = ["Fajr", "Dhuhr", "Asr", "Maghrib", "Isha"];

    public bool WhisperCanInvoke => true;

    public async Task RunCommand(ChatBot botInstance, BotCommandMessageModel message, UserDbModel user, GroupCollection arguments, CancellationToken ctx)
    {
        var timings = await GetPrayerTimingsAsync(ctx);
        if (timings == null)
        {
            await botInstance.SendChatMessageAsync($"@{message.Author.Username}, failed to fetch prayer times :(", true, autoDeleteAfter: TimeSpan.FromSeconds(10), whisperTo: user.KfUsername);
            return;
        }

        var georgiaZone = TimeZoneInfo.FindSystemTimeZoneById("Eastern Standard Time");
        var nowGeorgia = TimeZoneInfo.ConvertTimeFromUtc(DateTime.UtcNow, georgiaZone);

        string? nextPrayer = null;
        TimeSpan? timeUntilNext = null;
        foreach (var prayer in MainPrayers)
        {
            if (!timings.TryGetValue(prayer, out var timeStr) || !TryParseTime(timeStr, out var prayerTime))
                continue;
            var prayerDateTime = nowGeorgia.Date.Add(prayerTime.ToTimeSpan());
            if (prayerDateTime > nowGeorgia)
            {
                nextPrayer = prayer;
                timeUntilNext = prayerDateTime - nowGeorgia;
                break;
            }
        }

        // All prayers passed today — next is Fajr tomorrow
        if (nextPrayer == null)
        {
            nextPrayer = "Fajr (tomorrow)";
            if (timings.TryGetValue("Fajr", out var fajrStr) && TryParseTime(fajrStr, out var fajrTime))
                timeUntilNext = nowGeorgia.Date.AddDays(1).Add(fajrTime.ToTimeSpan()) - nowGeorgia;
        }

        var parts = new List<string>();
        foreach (var prayer in MainPrayers)
        {
            if (timings.TryGetValue(prayer, out var ts) && TryParseTime(ts, out var t))
                parts.Add($"{prayer}: {t.ToString("h:mm tt")}");
        }

        var countdownStr = timeUntilNext.HasValue
            ? $"{(int)timeUntilNext.Value.TotalHours}h {timeUntilNext.Value.Minutes}m {timeUntilNext.Value.Seconds}s"
            : "unknown";

        await botInstance.SendChatMessageAsync(
            $"Next: {nextPrayer} in {countdownStr} | {string.Join(" | ", parts)}", true);
    }

    private static bool TryParseTime(string timeStr, out TimeOnly result)
    {
        // API returns "HH:mm" but may include trailing timezone info like "HH:mm (EST)"
        var clean = timeStr.Split(' ')[0].Trim();
        return TimeOnly.TryParseExact(clean, "HH:mm", out result);
    }

    private static async Task<Dictionary<string, string>?> GetPrayerTimingsAsync(CancellationToken ctx)
    {
        var cache = MemoryCache.Default;
        if (cache.Get(CacheKey) is Dictionary<string, string> cached)
            return cached;

        try
        {
            var georgiaZone = TimeZoneInfo.FindSystemTimeZoneById("Eastern Standard Time");
            var nowGeorgia = TimeZoneInfo.ConvertTimeFromUtc(DateTime.UtcNow, georgiaZone);
            var dateStr = nowGeorgia.ToString("dd-MM-yyyy");

            using var client = new HttpClient();
            client.Timeout = TimeSpan.FromSeconds(10);
            var url = $"https://api.aladhan.com/v1/timingsByCity/{dateStr}?city=Atlanta&country=US&method=2";

            var response = await client.GetAsync(url, ctx);
            response.EnsureSuccessStatusCode();

            var json = await response.Content.ReadAsStringAsync(ctx);
            using var doc = JsonDocument.Parse(json);
            var timingsElement = doc.RootElement.GetProperty("data").GetProperty("timings");
            var timings = JsonSerializer.Deserialize<Dictionary<string, string>>(timingsElement.GetRawText());

            if (timings != null)
            {
                // Cache until midnight Georgia time so times refresh each day
                var midnight = new DateTimeOffset(nowGeorgia.Date.AddDays(1), georgiaZone.GetUtcOffset(nowGeorgia));
                cache.Set(CacheKey, timings, new CacheItemPolicy { AbsoluteExpiration = midnight });
            }

            return timings;
        }
        catch (Exception e)
        {
            Logger.Error(e, "Failed to fetch prayer times from Al Adhan API");
            return null;
        }
    }
}
