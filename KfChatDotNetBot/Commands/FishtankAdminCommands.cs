using System.Text.Json;
using System.Text.RegularExpressions;
using KfChatDotNetBot.Models;
using KfChatDotNetBot.Models.DbModels;
using KfChatDotNetBot.Services;
using KfChatDotNetBot.Settings;
using NLog;

namespace KfChatDotNetBot.Commands;

public class AddFishtankWhitelistCommand : ICommand
{
    public List<Regex> Patterns => [
        new Regex(@"^admin fishtank forward add (?<username>.+)$")
    ];

    public string? HelpText => "Add a user to the Fishtank chat forwarding whitelist";
    public UserRight RequiredRight => UserRight.TrueAndHonest;
    public TimeSpan Timeout => TimeSpan.FromSeconds(10);
    public RateLimitOptionsModel? RateLimitOptions => null;

    public bool WhisperCanInvoke => true;

    public async Task RunCommand(ChatBot botInstance, BotCommandMessageModel message, UserDbModel user, GroupCollection arguments, CancellationToken ctx)
    {
        var username = arguments["username"].Value.Trim();

        try
        {
            var setting = await SettingsProvider.GetValueAsync("fishtank_whitelist", bypassCache: true);
            var whitelist = string.IsNullOrEmpty(setting.Value)
                ? new List<string>()
                : JsonSerializer.Deserialize<List<string>>(setting.Value) ?? new List<string>();

            if (whitelist.Contains(username, StringComparer.OrdinalIgnoreCase))
            {
                await botInstance.SendChatMessageAsync($"@{message.Author.Username}, '{username}' is already whitelisted", true);
                return;
            }

            whitelist.Add(username);
            await SettingsProvider.SetValueAsJsonObjectAsync("fishtank_whitelist", whitelist);
            await botInstance.SendChatMessageAsync($"@{message.Author.Username}, added '{username}' to Fishtank whitelist", true);
        }
        catch (KeyNotFoundException)
        {
            // Setting doesn't exist, create it manually
            await using var db = new ApplicationDbContext();
            var whitelist = new List<string> { username };
            db.Settings.Add(new SettingDbModel
            {
                Key = "fishtank_whitelist",
                Value = JsonSerializer.Serialize(whitelist),
                Regex = @".+",
                Description = "Fishtank chat forwarding whitelist",
                Default = "[]",
                CacheDuration = 60
            });
            await db.SaveChangesAsync(ctx);
            await botInstance.SendChatMessageAsync($"@{message.Author.Username}, created whitelist and added '{username}'", true);
        }
    }
}

public class RemoveFishtankWhitelistCommand : ICommand
{
    public List<Regex> Patterns => [
        new Regex(@"^admin fishtank forward remove (?<username>.+)$")
    ];

    public string? HelpText => "Remove a user from the Fishtank chat forwarding whitelist";
    public UserRight RequiredRight => UserRight.TrueAndHonest;
    public TimeSpan Timeout => TimeSpan.FromSeconds(10);
    public RateLimitOptionsModel? RateLimitOptions => null;
    public bool WhisperCanInvoke => true;

    public async Task RunCommand(ChatBot botInstance, BotCommandMessageModel message, UserDbModel user, GroupCollection arguments, CancellationToken ctx)
    {
        var username = arguments["username"].Value.Trim();

        try
        {
            var setting = await SettingsProvider.GetValueAsync("fishtank_whitelist", bypassCache: true);
            var whitelist = string.IsNullOrEmpty(setting.Value)
                ? new List<string>()
                : JsonSerializer.Deserialize<List<string>>(setting.Value) ?? new List<string>();

            var removed = whitelist.RemoveAll(u => u.Equals(username, StringComparison.OrdinalIgnoreCase));

            if (removed == 0)
            {
                await botInstance.SendChatMessageAsync($"@{message.Author.Username}, '{username}' is not in the whitelist", true);
                return;
            }

            await SettingsProvider.SetValueAsJsonObjectAsync("fishtank_whitelist", whitelist);
            await botInstance.SendChatMessageAsync($"@{message.Author.Username}, removed '{username}' from Fishtank forward whitelist", true);
        }
        catch (KeyNotFoundException)
        {
            await botInstance.SendChatMessageAsync($"@{message.Author.Username}, forward whitelist doesn't exist yet", true);
        }
    }
}

public class ListFishtankWhitelistCommand : ICommand
{
    public List<Regex> Patterns => [
        new Regex(@"^admin fishtank forward list$")
    ];

    public string? HelpText => "List all users in the Fishtank chat forwarding whitelist";
    public UserRight RequiredRight => UserRight.TrueAndHonest;
    public TimeSpan Timeout => TimeSpan.FromSeconds(10);
    public RateLimitOptionsModel? RateLimitOptions => null;
    public bool WhisperCanInvoke => true;

    public async Task RunCommand(ChatBot botInstance, BotCommandMessageModel message, UserDbModel user, GroupCollection arguments, CancellationToken ctx)
    {
        try
        {
            var setting = await SettingsProvider.GetValueAsync("fishtank_whitelist", bypassCache: true);
            var whitelist = string.IsNullOrEmpty(setting.Value)
                ? new List<string>()
                : JsonSerializer.Deserialize<List<string>>(setting.Value) ?? new List<string>();

            if (whitelist.Count == 0)
            {
                await botInstance.SendChatMessageAsync($"@{message.Author.Username}, Fishtank forward whitelist is empty", true);
                return;
            }

            var userList = string.Join(", ", whitelist);
            await botInstance.SendChatMessageAsync($"@{message.Author.Username}, Fishtank forward whitelist ({whitelist.Count}): {userList}", true);
        }
        catch (KeyNotFoundException)
        {
            await botInstance.SendChatMessageAsync($"@{message.Author.Username}, whitelist doesn't exist yet", true);
        }
    }
}


public class RefreshFishtankCamerasCommand : ICommand
{
    public List<Regex> Patterns => [
        new Regex(@"^admin fishtank cameras refresh$")
    ];

    public string? HelpText => "Refresh the camera list from the Fishtank API (does not affect active buffers)";
    public UserRight RequiredRight => UserRight.TrueAndHonest;
    public TimeSpan Timeout => TimeSpan.FromSeconds(15);
    public RateLimitOptionsModel? RateLimitOptions => null;
    public bool WhisperCanInvoke => true;

    public async Task RunCommand(ChatBot botInstance, BotCommandMessageModel message, UserDbModel user, GroupCollection arguments, CancellationToken ctx)
    {
        var tokenService = botInstance.BotServices.FishtankTokenService;
        if (tokenService == null)
        {
            await botInstance.SendChatMessageAsync($"@{message.Author.Username}, Fishtank token service is not initialized", true);
            return;
        }

        try
        {
            var liveStreams = await tokenService.FetchLiveStreamsAsync();
            if (liveStreams == null || liveStreams.LiveStreams.Count == 0)
            {
                await botInstance.SendChatMessageAsync($"@{message.Author.Username}, API returned no streams", true);
                return;
            }

            var (added, removed, total) = FishtankCameras.RebuildFromApi(liveStreams);
            await botInstance.SendChatMessageAsync(
                $"@{message.Author.Username}, camera list refreshed: {total} cameras (+{added} -{removed})", true);
        }
        catch (Exception ex)
        {
            await botInstance.SendChatMessageAsync($"@{message.Author.Username}, failed to refresh cameras: {ex.Message}", true);
        }
    }
}


public static class FishtankJobs
{
    private static readonly Logger Logger = LogManager.GetCurrentClassLogger();

    private record Contestant(string Name, string? Job, long? EliminatedAt);
    private record ContestantsResponse(List<Contestant> Contestants);

    public static async Task<string> BuildJobsTable()
    {
        try
        {
            using var httpClient = new HttpClient();
            var response = await httpClient.GetAsync("https://api.fishtank.live/v1/contestants");
            if (!response.IsSuccessStatusCode)
            {
                Logger.Error($"[FishtankJobs] Failed to fetch contestants: {response.StatusCode}");
                return "";
            }

            var json = await response.Content.ReadAsStringAsync();
            var data = JsonSerializer.Deserialize<ContestantsResponse>(json,
                new JsonSerializerOptions { PropertyNameCaseInsensitive = true });
            if (data == null || data.Contestants.Count == 0)
                return "";

            var active = data.Contestants
                .Where(c => c.EliminatedAt == null && !string.IsNullOrWhiteSpace(c.Job))
                .OrderBy(c => c.Name)
                .ToList();

            if (active.Count == 0)
                return "";

            var tableStr = "[size=80][TABLE]";
            while (active.Count > 0)
            {
                var batch = active.Take(3).ToList();
                active = active.Skip(3).ToList();
                var row = string.Join("", batch.Select(c => $"[TD][B]{c.Name}[/B]: {c.Job}[/TD]"));
                tableStr += $"[TR]{row}[/TR]";
            }
            tableStr += "[/TABLE]";
            return tableStr;
        }
        catch (Exception ex)
        {
            Logger.Error($"[FishtankJobs] Failed to fetch contestants: {ex.Message}");
            return "";
        }
    }
}

