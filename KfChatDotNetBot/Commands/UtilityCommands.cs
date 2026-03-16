using System.Reflection;
using System.Text.RegularExpressions;
using Humanizer;
using KfChatDotNetBot.Extensions;
using KfChatDotNetBot.Models;
using KfChatDotNetBot.Models.DbModels;
using KfChatDotNetBot.Settings;
using KfChatDotNetWsClient.Models.Events;

namespace KfChatDotNetBot.Commands;

public class HelpCommand : ICommand
{
    public List<Regex> Patterns => [
        new Regex("^help$"),
        new Regex("^guide$"),
        new Regex("^commands$"),
    ];

    public string? HelpText => "Link to the bot guide";
    public UserRight RequiredRight => UserRight.Guest;
    public TimeSpan Timeout => TimeSpan.FromSeconds(10);
    public RateLimitOptionsModel? RateLimitOptions => new RateLimitOptionsModel
    {
        MaxInvocations = 1,
        Window = TimeSpan.FromSeconds(60),
        Flags = RateLimitFlags.Global
    };
    public async Task RunCommand(ChatBot botInstance, MessageModel message, UserDbModel user, GroupCollection arguments, CancellationToken ctx)
    {
        await botInstance.SendChatMessageAsync("Bot usage guide: https://kiwifarms.st/posts/23976561", true);
    }
}

public class TempSuppressGambaMessages : ICommand
{
    public List<Regex> Patterns => [
        new Regex("^nogamba$")
    ];

    public string? HelpText => null;
    public UserRight RequiredRight => UserRight.Guest;
    public TimeSpan Timeout => TimeSpan.FromSeconds(10);
    public RateLimitOptionsModel? RateLimitOptions => null;
    public async Task RunCommand(ChatBot botInstance, MessageModel message, UserDbModel user, GroupCollection arguments, CancellationToken ctx)
    {
        botInstance.BotServices.TemporarilySuppressGambaMessages = true;
        await botInstance.SendChatMessageAsync("No more gamba notifs", true);
    }
}

public class EnableGambaMessages : ICommand
{
    public List<Regex> Patterns => [
        new Regex("^yesgamba")
    ];

    public string? HelpText => null;
    public UserRight RequiredRight => UserRight.Guest;
    public TimeSpan Timeout => TimeSpan.FromSeconds(10);
    public RateLimitOptionsModel? RateLimitOptions => null;
    public async Task RunCommand(ChatBot botInstance, MessageModel message, UserDbModel user, GroupCollection arguments, CancellationToken ctx)
    {
        botInstance.BotServices.TemporarilySuppressGambaMessages = false;
        await botInstance.SendChatMessageAsync("Gamba notifs back on the menu", true);
    }
}

public class GetVersionCommand : ICommand
{
    public List<Regex> Patterns => [
        new Regex("^version$")
    ];

    public string? HelpText => null;
    public UserRight RequiredRight => UserRight.Loser;
    public TimeSpan Timeout => TimeSpan.FromSeconds(10);
    public RateLimitOptionsModel? RateLimitOptions => null;
    public async Task RunCommand(ChatBot botInstance, MessageModel message, UserDbModel user, GroupCollection arguments, CancellationToken ctx)
    {
        var version = Assembly.GetEntryAssembly()?
            .GetCustomAttribute<AssemblyInformationalVersionAttribute>()?.InformationalVersion;
        if (version == null)
        {
            await botInstance.SendChatMessageAsync($"Caught a null when trying to retrieve the bot's assembly version.",
                true);
            return;
        }
        await botInstance.SendChatMessageAsync($"Bot compiled against {version.Split('+')[1]}", true);
    }
}

public class SourceCommand : ICommand
{
    public List<Regex> Patterns => [
        new Regex("^source$"),
    ];

    public string? HelpText => "Get bot source code url";
    public UserRight RequiredRight => UserRight.Loser;
    public TimeSpan Timeout => TimeSpan.FromSeconds(10);
    public RateLimitOptionsModel? RateLimitOptions => null;
    public async Task RunCommand(ChatBot botInstance, MessageModel message, UserDbModel user, GroupCollection arguments, CancellationToken ctx)
    {
        await botInstance.SendChatMessageAsync("Source: https://github.com/cohlexyz/FishtankDotNet");
    }
}

public class ShareXClippingCommand : ICommand
{
    public List<Regex> Patterns => [
        new Regex("^sharex$"),
        new Regex("^clipping$")
    ];

    public string? HelpText => "Get instructions on how to clip using ShareX";
    public UserRight RequiredRight => UserRight.Loser;
    public TimeSpan Timeout => TimeSpan.FromSeconds(10);
    public RateLimitOptionsModel? RateLimitOptions => null;
    public async Task RunCommand(ChatBot botInstance, MessageModel message, UserDbModel user, GroupCollection arguments, CancellationToken ctx)
    {
        await botInstance.SendChatMessageAsync("Clipping with ShareX: https://kiwifarms.st/threads/189850");
    }
}

public class GetLastActivity : ICommand
{
    public List<Regex> Patterns => [
        new Regex("^lastactivity", RegexOptions.IgnoreCase),
        new Regex("^lastactive", RegexOptions.IgnoreCase)
    ];

    public string? HelpText => "When was Bossman last active?";
    public UserRight RequiredRight => UserRight.Loser;
    public TimeSpan Timeout => TimeSpan.FromSeconds(10);
    public RateLimitOptionsModel? RateLimitOptions => new RateLimitOptionsModel
    {
        MaxInvocations = 3,
        Window = TimeSpan.FromSeconds(10)
    };
    public async Task RunCommand(ChatBot botInstance, MessageModel message, UserDbModel user, GroupCollection arguments, CancellationToken ctx)
    {
        var lastActive = await SettingsProvider.GetValueAsync(BuiltIn.Keys.BossmanLastSighting);
        if (lastActive.Value == null)
        {
            await botInstance.SendChatMessageAsync($"{user.FormatUsername()}, I don't know.", true);
            return;
        }

        var activity = lastActive.JsonDeserialize<LastSightingModel>();
        var elapsed = DateTimeOffset.UtcNow - activity!.When;
        await botInstance.SendChatMessageAsync(
            $"{user.FormatUsername()}, BossmanJack was last seen {elapsed.Humanize(maxUnit: TimeUnit.Day, minUnit: TimeUnit.Second, precision: 2)} ago {activity.Activity}",
            true);
    }
}

public class PPVCommand : ICommand
{
    public List<Regex> Patterns => [
        new Regex("^ppv$")
    ];

    public string? HelpText => "Watch cows live";
    public UserRight RequiredRight => UserRight.Guest;
    public TimeSpan Timeout => TimeSpan.FromSeconds(10);
    public RateLimitOptionsModel? RateLimitOptions => null;
    public async Task RunCommand(ChatBot botInstance, MessageModel message, UserDbModel user, GroupCollection arguments,
        CancellationToken ctx)
    {
        var url = await SettingsProvider.GetValueAsync(BuiltIn.Keys.RestreamUrl);
        await botInstance.SendChatMessageAsync($"@{message.Author.Username}, restream URL: https://old.ppv.to/ft | MPV playlist: https://i.ddos.lgbt/u/fishtank-s5.m3u | Camera multiview: https://kiwifarms.st/posts/23970064", true);
    }
}