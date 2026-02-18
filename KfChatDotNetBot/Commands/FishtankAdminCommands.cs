using System.Text.Json;
using System.Text.RegularExpressions;
using KfChatDotNetBot.Models;
using KfChatDotNetBot.Models.DbModels;
using KfChatDotNetBot.Settings;
using KfChatDotNetWsClient.Models.Events;

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

    public async Task RunCommand(ChatBot botInstance, MessageModel message, UserDbModel user, GroupCollection arguments, CancellationToken ctx)
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

    public async Task RunCommand(ChatBot botInstance, MessageModel message, UserDbModel user, GroupCollection arguments, CancellationToken ctx)
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

    public async Task RunCommand(ChatBot botInstance, MessageModel message, UserDbModel user, GroupCollection arguments, CancellationToken ctx)
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
