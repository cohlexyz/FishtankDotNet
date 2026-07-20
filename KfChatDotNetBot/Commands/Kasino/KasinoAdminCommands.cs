using System.Text.RegularExpressions;
using Humanizer;
using KfChatDotNetBot.Extensions;
using KfChatDotNetBot.Models;
using KfChatDotNetBot.Models.DbModels;
using KfChatDotNetBot.Services;
using KfChatDotNetBot.Settings;
using KfChatDotNetWsClient.Models.Events;
using Microsoft.EntityFrameworkCore;

namespace KfChatDotNetBot.Commands.Kasino;

[KasinoCommand]
public class TempExcludeCommand : ICommand
{
    public List<Regex> Patterns => [
        new Regex("^admin kasino exclude$", RegexOptions.IgnoreCase),
        new Regex(@"^admin kasino exclude (?<user_id>\d+)$", RegexOptions.IgnoreCase),
        new Regex(@"^admin kasino exclude (?<user_id>\d+) (?<seconds>\d+)$", RegexOptions.IgnoreCase),
    ];
    public string? HelpText => "Exclude somebody";
    public UserRight RequiredRight => UserRight.TrueAndHonest;
    public TimeSpan Timeout => TimeSpan.FromSeconds(10);
    public RateLimitOptionsModel? RateLimitOptions => null;
    public bool WhisperCanInvoke => false;

    public async Task RunCommand(ChatBot botInstance, BotCommandMessageModel message, UserDbModel user, GroupCollection arguments,
        CancellationToken ctx)
    {
        await using var db = new ApplicationDbContext();
        if (!arguments.TryGetValue("user_id", out var userId))
        {
            await botInstance.SendChatMessageAsync($"{user.FormatUsername()}, not enough arguments. !admin kasino exclude user_id seconds", true);
            return;
        }

        var targetUser = await db.Users.FirstOrDefaultAsync(x => x.KfId == Convert.ToInt32(userId.Value), cancellationToken: ctx);
        if (targetUser == null)
        {
            await botInstance.SendChatMessageAsync($"{user.FormatUsername()}, couldn't find user with that ID", true);
            return;
        }

        var exclusionTime = TimeSpan.FromSeconds(600);
        if (arguments.TryGetValue("seconds", out var seconds))
        {
            exclusionTime = TimeSpan.FromSeconds(Convert.ToInt32(seconds.Value));
        }

        var targetGambler = await Money.GetGamblerEntityAsync(user.Id, ct: ctx);

        if (targetGambler == null)
        {
            await botInstance.SendChatMessageAsync(
                $"{user.FormatUsername()}, {targetUser.KfUsername} can't be excluded as he's banned.", true);
            return;
        }
        targetGambler = await db.Gamblers.FirstOrDefaultAsync(x => x.Id == targetGambler.Id, cancellationToken: ctx);

        var activeExclusion = await Money.GetActiveExclusionAsync(targetGambler!.Id, ctx);
        if (activeExclusion != null)
        {
            var length = DateTimeOffset.UtcNow - activeExclusion.Expires;
            await botInstance.SendChatMessageAsync(
                $"{user.FormatUsername()}, {targetUser.KfUsername} is already excluded for another {length.Humanize()}",
                true);
            return;
        }

        await db.Exclusions.AddAsync(new GamblerExclusionDbModel
        {
            Gambler = targetGambler,
            Expires = DateTimeOffset.UtcNow.Add(exclusionTime),
            Created = DateTimeOffset.UtcNow,
            Source = ExclusionSource.Administrative
        }, ctx);
        await db.SaveChangesAsync(ctx);
        await botInstance.SendChatMessageAsync($"{user.FormatUsername()}, excluded {targetUser.KfUsername} for {exclusionTime.Humanize()}", true);
    }
}

[KasinoCommand]
public class KasinoGameToggleCommand : ICommand
{
    public List<Regex> Patterns => [
        new Regex(@"^admin kasino (?<game>\w+) (?<action>enable|disable)$", RegexOptions.IgnoreCase),
        new Regex("^kasino (?<action>open|close)$", RegexOptions.IgnoreCase)
    ];

    public string? HelpText => "Enable or disable a Kasino game (use 'all' to toggle all games)";
    public UserRight RequiredRight => UserRight.TrueAndHonest;
    public TimeSpan Timeout => TimeSpan.FromSeconds(10);
    public RateLimitOptionsModel? RateLimitOptions => null;
    public bool WhisperCanInvoke => false;

    public async Task RunCommand(ChatBot botInstance, BotCommandMessageModel message, UserDbModel user, GroupCollection arguments,
        CancellationToken ctx)
    {
        if (!arguments.TryGetValue("action", out var actionArg))
        {
            await botInstance.SendChatMessageAsync(
                $"{user.FormatUsername()}, usage: !admin kasino <game|all> enable|disable", true);
            return;
        }

        string gameName;

        if (!arguments.TryGetValue("game", out var gameArg))
        {
            gameName = "all";
        }
        else
        {
            gameName = gameArg.Value;
        }

        var action = actionArg.Value.ToLower();
        var shouldEnable = action is "enable" or "open";
        var status = shouldEnable ? "enabled" : "disabled";

        await using var db = new ApplicationDbContext();
        var gameSettingPattern = new Regex(@"^Kasino\.(?<game>\w+)\.Enabled$", RegexOptions.IgnoreCase);

        var allGameSettings = await db.Settings
            .Where(s => s.Key.StartsWith("Kasino.") && s.Key.EndsWith(".Enabled") && !s.Key.Contains("DailyDollar"))
            .ToListAsync(ctx);

        // Handle "all" games
        if (gameName.Equals("all", StringComparison.OrdinalIgnoreCase))
        {
            foreach (var setting in allGameSettings)
            {
                await SettingsProvider.SetValueAsBooleanAsync(setting.Key, shouldEnable);
            }

            await botInstance.SendChatMessageAsync(
                $"{user.FormatUsername()}, all {allGameSettings.Count} Kasino games have been {status}.", true);
            return;
        }

        // Handle individual game - find matching setting key
        var matchedSetting = allGameSettings.FirstOrDefault(s =>
        {
            var match = gameSettingPattern.Match(s.Key);
            return match.Success && match.Groups["game"].Value.Equals(gameName, StringComparison.OrdinalIgnoreCase);
        });

        if (matchedSetting is null)
        {
            await botInstance.SendChatMessageAsync(
                $"{user.FormatUsername()}, unknown game '{gameName}'. Use '!admin kasino games' to see available games, or use 'all' to toggle all games.",
                true);
            return;
        }

        await SettingsProvider.SetValueAsBooleanAsync(matchedSetting.Key, shouldEnable);

        var match = gameSettingPattern.Match(matchedSetting.Key);
        var gameDisplayName = match.Groups["game"].Value.Humanize();

        await botInstance.SendChatMessageAsync(
            $"{user.FormatUsername()}, {gameDisplayName} has been {status}.", true);
    }
}

[KasinoCommand]
public class KasinoGameListCommand : ICommand
{
    public List<Regex> Patterns => [
        new Regex(@"^admin kasino games$", RegexOptions.IgnoreCase)
    ];

    public string? HelpText => "List all kasino games and their status";
    public UserRight RequiredRight => UserRight.TrueAndHonest;
    public TimeSpan Timeout => TimeSpan.FromSeconds(10);
    public RateLimitOptionsModel? RateLimitOptions => null;
    public bool WhisperCanInvoke => false;

    public async Task RunCommand(ChatBot botInstance, BotCommandMessageModel message, UserDbModel user, GroupCollection arguments,
        CancellationToken ctx)
    {
        var response = $"{user.FormatUsername()}, Kasino games:[br]";
        var colors = await SettingsProvider.GetMultipleValuesAsync([
            BuiltIn.Keys.KiwiFarmsGreenColor,
            BuiltIn.Keys.KiwiFarmsRedColor
        ]);

        await using var db = new ApplicationDbContext();
        var gameSettingPattern = new Regex(@"^Kasino\.(?<game>\w+)\.Enabled$", RegexOptions.IgnoreCase);

        var allGameSettings = await db.Settings
            .Where(s => s.Key.StartsWith("Kasino.") && s.Key.EndsWith(".Enabled"))
            .ToListAsync(ctx);

        var orderedSettings = allGameSettings.OrderBy(s =>
        {
            var match = gameSettingPattern.Match(s.Key);
            return match.Groups["game"].Value;
        });

        foreach (var setting in orderedSettings)
        {
            var isEnabled = setting.Value?.Equals("true", StringComparison.OrdinalIgnoreCase) ?? false;
            var match = gameSettingPattern.Match(setting.Key);
            var gameName = match.Groups["game"].Value.Humanize();

            var status = isEnabled
                ? $"[B][COLOR={colors[BuiltIn.Keys.KiwiFarmsGreenColor].Value}]ENABLED[/COLOR][/B]"
                : $"[B][COLOR={colors[BuiltIn.Keys.KiwiFarmsRedColor].Value}]DISABLED[/COLOR][/B]";

            response += $"{gameName}: {status}[br]";
        }

        await botInstance.SendChatMessageAsync(response, true);
    }
}

public class KasinoTopStoxCommand : ICommand
{
    public List<Regex> Patterns => [
        new Regex(@"^admin kasino top stox$", RegexOptions.IgnoreCase)
    ];

    public string? HelpText => "Show top 5 users by net Stox profit";
    public UserRight RequiredRight => UserRight.TrueAndHonest;
    public TimeSpan Timeout => TimeSpan.FromSeconds(15);
    public RateLimitOptionsModel? RateLimitOptions => null;
    public bool WhisperCanInvoke => false;

    private static readonly TransactionSourceEventType[] StoxTypes =
    [
        TransactionSourceEventType.StoxSale,
        TransactionSourceEventType.StoxPurchase,
        TransactionSourceEventType.StoxShort,
        TransactionSourceEventType.StoxCover,
        TransactionSourceEventType.StoxLeveragedPurchase,
        TransactionSourceEventType.StoxLeveragedSale,
        TransactionSourceEventType.StoxLeveragedShort,
        TransactionSourceEventType.StoxLeveragedCover,
    ];

    public async Task RunCommand(ChatBot botInstance, BotCommandMessageModel message, UserDbModel user, GroupCollection arguments,
        CancellationToken ctx)
    {
        await using var db = new ApplicationDbContext();

        var transactions = await db.Transactions
            .Where(t => StoxTypes.Contains(t.EventSource))
            .Include(t => t.Gambler).ThenInclude(g => g.User)
            .ToListAsync(ctx);

        if (transactions.Count == 0)
        {
            await botInstance.SendChatMessageAsync($"{user.FormatUsername()}, no Stox transactions found.", true, whisperTo: user.KfId);
            return;
        }

        var top5 = transactions
            .GroupBy(t => t.Gambler.Id)
            .Select(g => new
            {
                Username = g.First().Gambler.User.KfUsername,
                NetProfit = g.Sum(t => t.Effect)
            })
            .OrderByDescending(x => x.NetProfit)
            .Take(5)
            .ToList();

        var response = $"{user.FormatUsername()}, top 5 Stox gainers:[br]";
        for (var i = 0; i < top5.Count; i++)
        {
            var entry = top5[i];
            response += $"#{i + 1} {entry.Username}: {await entry.NetProfit.FormatKasinoCurrencyAsync(wrapInPlainBbCode: false)}[br]";
        }

        await botInstance.SendChatMessageAsync(response, true, whisperTo: user.KfId);
    }
}

public class KasinoTopGamblingCommand : ICommand
{
    public List<Regex> Patterns => [
        new Regex(@"^admin kasino top gambling$", RegexOptions.IgnoreCase)
    ];

    public string? HelpText => "Show top 5 users by net gambling profit";
    public UserRight RequiredRight => UserRight.TrueAndHonest;
    public TimeSpan Timeout => TimeSpan.FromSeconds(15);
    public RateLimitOptionsModel? RateLimitOptions => null;
    public bool WhisperCanInvoke => false;

    public async Task RunCommand(ChatBot botInstance, BotCommandMessageModel message, UserDbModel user, GroupCollection arguments,
        CancellationToken ctx)
    {
        await using var db = new ApplicationDbContext();

        var wagers = await db.Wagers
            .Include(w => w.Gambler).ThenInclude(g => g.User)
            .ToListAsync(ctx);

        if (wagers.Count == 0)
        {
            await botInstance.SendChatMessageAsync($"{user.FormatUsername()}, no gambling records found.", true, whisperTo: user.KfId);
            return;
        }

        var top5 = wagers
            .GroupBy(w => w.Gambler.Id)
            .Select(g => new
            {
                Username = g.First().Gambler.User.KfUsername,
                NetProfit = g.Sum(w => w.WagerEffect)
            })
            .OrderByDescending(x => x.NetProfit)
            .Take(5)
            .ToList();

        var response = $"{user.FormatUsername()}, top 5 gambling gainers:[br]";
        for (var i = 0; i < top5.Count; i++)
        {
            var entry = top5[i];
            response += $"#{i + 1} {entry.Username}: {await entry.NetProfit.FormatKasinoCurrencyAsync(wrapInPlainBbCode: false)}[br]";
        }

        await botInstance.SendChatMessageAsync(response, true, whisperTo: user.KfId);
    }
}