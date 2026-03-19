using System.Text.RegularExpressions;
using KfChatDotNetBot.Models;
using KfChatDotNetBot.Models.DbModels;
using KfChatDotNetBot.Services;
using KfChatDotNetWsClient.Models.Events;
using Microsoft.EntityFrameworkCore;

namespace KfChatDotNetBot.Commands;

public class CounterAddCommand : ICommand
{
    public List<Regex> Patterns => [new Regex(@"^counter add (?<name>\w{1,64})$")];
    public string? HelpText => "counter add <name> - Create a new counter";
    public UserRight RequiredRight => UserRight.TrueAndHonest;
    public TimeSpan Timeout => TimeSpan.FromSeconds(10);
    public RateLimitOptionsModel? RateLimitOptions => null;

    public bool WhisperCanInvoke => true;
    public async Task RunCommand(ChatBot botInstance, BotCommandMessageModel message, UserDbModel user, GroupCollection arguments, CancellationToken ctx)
    {
        var name = arguments["name"].Value.ToLower();
        await using var db = new ApplicationDbContext();
        if (await db.Counters.AnyAsync(c => c.Name == name, ctx))
        {
            await botInstance.SendChatMessageAsync($"Counter '{name}' already exists", true);
            return;
        }
        await db.Counters.AddAsync(new CounterDbModel { Name = name, Value = 0 }, ctx);
        await db.SaveChangesAsync(ctx);
        await botInstance.SendChatMessageAsync($"Counter '{name}' created", true);
    }
}

public class CounterRemoveCommand : ICommand
{
    public List<Regex> Patterns => [new Regex(@"^counter remove (?<name>\w{1,64})$")];
    public string? HelpText => "counter remove <name> - Delete a counter";
    public UserRight RequiredRight => UserRight.TrueAndHonest;
    public TimeSpan Timeout => TimeSpan.FromSeconds(10);
    public RateLimitOptionsModel? RateLimitOptions => null;
    public bool WhisperCanInvoke => true;

    public async Task RunCommand(ChatBot botInstance, BotCommandMessageModel message, UserDbModel user, GroupCollection arguments, CancellationToken ctx)
    {
        var name = arguments["name"].Value.ToLower();
        await using var db = new ApplicationDbContext();
        var counter = await db.Counters.FirstOrDefaultAsync(c => c.Name == name, ctx);
        if (counter == null)
        {
            await botInstance.SendChatMessageAsync($"Counter '{name}' does not exist", true);
            return;
        }
        db.Counters.Remove(counter);
        await db.SaveChangesAsync(ctx);
        await botInstance.SendChatMessageAsync($"Counter '{name}' removed", true);
    }
}

[DontDeleteInvocationMessage]
public class CounterIncrementCommand : ICommand
{
    public List<Regex> Patterns => [new Regex(@"^(?<name>\w{1,64})itup$")];
    public string? HelpText => "<name>itup - Increment a counter";
    public UserRight RequiredRight => UserRight.Guest;
    public TimeSpan Timeout => TimeSpan.FromSeconds(60);
    public RateLimitOptionsModel? RateLimitOptions => new RateLimitOptionsModel
    {
        MaxInvocations = 2,
        Window = TimeSpan.FromMinutes(1),
        Flags = RateLimitFlags.Global
    };

    public bool WhisperCanInvoke => true;

    public async Task RunCommand(ChatBot botInstance, BotCommandMessageModel message, UserDbModel user, GroupCollection arguments, CancellationToken ctx)
    {
        var name = arguments["name"].Value.ToLower();
        await using var db = new ApplicationDbContext();
        var counter = await db.Counters.FirstOrDefaultAsync(c => c.Name == name, ctx);
        if (counter == null) return;
        counter.Value++;
        await db.SaveChangesAsync(ctx);
        await botInstance.SendChatMessageAsync($"{name} counter at {counter.Value:N0}", true);
    }
}

[DontDeleteInvocationMessage]
public class CounterShowCommand : ICommand
{
    public List<Regex> Patterns => [new Regex(@"^(?<name>\w{1,64})count$")];
    public string? HelpText => "<name>count - Show the current value of a counter";
    public UserRight RequiredRight => UserRight.Guest;
    public TimeSpan Timeout => TimeSpan.FromSeconds(60);
    public RateLimitOptionsModel? RateLimitOptions => new RateLimitOptionsModel
    {
        MaxInvocations = 2,
        Window = TimeSpan.FromMinutes(1),
        Flags = RateLimitFlags.Global
    };

    public bool WhisperCanInvoke => true;

    public async Task RunCommand(ChatBot botInstance, BotCommandMessageModel message, UserDbModel user, GroupCollection arguments, CancellationToken ctx)
    {
        var name = arguments["name"].Value.ToLower();
        await using var db = new ApplicationDbContext();
        var counter = await db.Counters.FirstOrDefaultAsync(c => c.Name == name, ctx);
        if (counter == null) return;
        await botInstance.SendChatMessageAsync($"{name} counter at {counter.Value:N0}", true);
    }
}
