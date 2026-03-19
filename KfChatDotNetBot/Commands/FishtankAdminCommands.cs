using System.Text.Json;
using System.Text.RegularExpressions;
using KfChatDotNetBot.Models;
using KfChatDotNetBot.Models.DbModels;
using KfChatDotNetBot.Services;
using KfChatDotNetBot.Settings;
using StackExchange.Redis;

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


public class JobsData
{
    public Dictionary<string, string> FishJobs { get; set; } = [];
}

public class UpdateJobsCommand : ICommand
{
    public List<Regex> Patterns => [
        new Regex(@"^admin job set(?<job>.+) (?<fish>.+)$")
    ];

    public string? HelpText => "Update Fishtank jobs for a user ";
    public UserRight RequiredRight => UserRight.TrueAndHonest;
    public TimeSpan Timeout => TimeSpan.FromSeconds(10);
    public RateLimitOptionsModel? RateLimitOptions => null;
    public bool WhisperCanInvoke => true;

    public async Task RunCommand(ChatBot botInstance, BotCommandMessageModel message, UserDbModel user, GroupCollection arguments, CancellationToken ctx)
    {
        var job = arguments["job"].Value.Trim();
        var fish = arguments["fish"].Value.Trim();

        if (string.IsNullOrEmpty(job))
        {
            await botInstance.SendChatMessageAsync($"@{message.Author.Username}, invalid command format. Use <job name> <fish name>", true, whisperTo: message.Author.Username);
            return;
        }
        var settings = await SettingsProvider.GetMultipleValuesAsync([
            BuiltIn.Keys.BotRedisConnectionString
        ]);

        if (string.IsNullOrEmpty(settings[BuiltIn.Keys.BotRedisConnectionString].Value))
        {
            await botInstance.SendChatMessageAsync($"{user.KfUsername}, predictions are not available at this time", true
                , whisperTo: user.KfUsername);
            return;
        }

        var redis = await ConnectionMultiplexer.ConnectAsync(settings[BuiltIn.Keys.BotRedisConnectionString].Value!);
        var redisDb = redis.GetDatabase();

        var jobsDataJson = await redisDb.StringGetAsync("fishtank_jobs");
        var jobsData = string.IsNullOrEmpty(jobsDataJson) ? new JobsData() : JsonSerializer.Deserialize<JobsData>((string)jobsDataJson!) ?? new JobsData();

        if (fish.Equals("none", StringComparison.OrdinalIgnoreCase))
        {
            jobsData.FishJobs.Remove(job);
        }
        else
        {
            jobsData.FishJobs[job] = fish;
        }

        await redisDb.StringSetAsync("fishtank_jobs", JsonSerializer.Serialize(jobsData));
        await botInstance.SendChatMessageAsync($"@{message.Author.Username}, updated jobs for '{fish}'", true, whisperTo: message.Author.Username);

        // update otd to reflect changes
        await botInstance.BotServices.UpdateStoxMotdAsync();
    }

    public static async Task<string> BuildJobsTable(IDatabase redisDb)
    {
        var fishJobsJson = await redisDb.StringGetAsync("fishtank_jobs");
        var fishJobsData = string.IsNullOrEmpty(fishJobsJson) ? new JobsData() : JsonSerializer.Deserialize<JobsData>((string)fishJobsJson!) ?? new JobsData();
        var fishJobs = fishJobsData.FishJobs;
        if (fishJobs.Count == 0)
        {
            return "";
        }

        var tableStr = "[size=80][TABLE]";
        var users = fishJobs.Keys.OrderBy(fish => fish).ToList();

        while (users.Count > 0)
        {
            var batch = users.Take(3).ToList();
            users = users.Skip(3).ToList();

            var row = string.Join("", batch.Select(fish =>
            {
                var job = fishJobs[fish];
                return $"[TD][B]{fish}[/B]: {job}[/TD]";
            }));

            tableStr += $"[TR]{row}[/TR]";
        }

        tableStr += "[/TABLE]";
        return tableStr;
    }
}

