using System.Text.RegularExpressions;
using KfChatDotNetBot.Models;
using KfChatDotNetBot.Models.DbModels;
using KfChatDotNetWsClient.Models.Events;
namespace KfChatDotNetBot.Commands;

public class CastCommand : ICommand
{
    public List<Regex> Patterns => [
        new Regex(@"^cast$"),
    ];
    public string? HelpText => "It's a good cast";
    public UserRight RequiredRight => UserRight.Loser;
    public TimeSpan Timeout => TimeSpan.FromSeconds(10);
    public RateLimitOptionsModel? RateLimitOptions => null;
    public async Task RunCommand(ChatBot botInstance, MessageModel message, UserDbModel user, GroupCollection arguments,
        CancellationToken ctx)
    {
        await botInstance.SendChatMessageAsync(
            $"[img]https://kiwifarms.st/attachments/cast-webp.7517979/[/img]", true, autoDeleteAfter: TimeSpan.FromSeconds(60));
    }
}

public class NiggaCommand : ICommand
{
    public List<Regex> Patterns => [
        new Regex(@"^nigga$"),
    ];
    public string? HelpText => "The fuck is he doing?";
    public UserRight RequiredRight => UserRight.Loser;
    public TimeSpan Timeout => TimeSpan.FromSeconds(10);
    public RateLimitOptionsModel? RateLimitOptions => null;
    public async Task RunCommand(ChatBot botInstance, MessageModel message, UserDbModel user, GroupCollection arguments,
        CancellationToken ctx)
    {
        await botInstance.SendChatMessageAsync(
            $"[img]https://kiwifarms.st/attachments/tf-webp.7516489/[/img]", true, autoDeleteAfter: TimeSpan.FromSeconds(60));
    }
}

public class ControlCommand : ICommand
{
    public List<Regex> Patterns => [
        new Regex(@"^control$"),
    ];
    public string? HelpText => "Jet's got this under control";
    public UserRight RequiredRight => UserRight.Loser;
    public TimeSpan Timeout => TimeSpan.FromSeconds(10);
    public RateLimitOptionsModel? RateLimitOptions => null;
    public async Task RunCommand(ChatBot botInstance, MessageModel message, UserDbModel user, GroupCollection arguments,
        CancellationToken ctx)
    {
        await botInstance.SendChatMessageAsync(
            $"[img]https://kiwifarms.st/attachments/control-webp.7516484/[/img]", true, autoDeleteAfter: TimeSpan.FromSeconds(60));
    }
}

public class PlanCommand : ICommand
{
    public List<Regex> Patterns => [
        new Regex(@"^plan$"),
    ];
    public string? HelpText => "We have a plan";
    public UserRight RequiredRight => UserRight.Loser;
    public TimeSpan Timeout => TimeSpan.FromSeconds(10);
    public RateLimitOptionsModel? RateLimitOptions => null;
    public async Task RunCommand(ChatBot botInstance, MessageModel message, UserDbModel user, GroupCollection arguments,
        CancellationToken ctx)
    {
        await botInstance.SendChatMessageAsync(
            $"[img]https://kiwifarms.st/attachments/plan-webp.7517772/[/img]", true, autoDeleteAfter: TimeSpan.FromSeconds(60));
    }
}