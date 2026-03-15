
using System.Net;
using System.Text.Json;
using System.Text.RegularExpressions;
using KfChatDotNetBot.Extensions;
using KfChatDotNetBot.Models;
using KfChatDotNetBot.Models.DbModels;
using KfChatDotNetBot.Services;
using KfChatDotNetBot.Settings;
using KfChatDotNetWsClient.Models.Events;
using NLog;
using StackExchange.Redis;

namespace KfChatDotNetBot.Commands;

public class StoxCommand : ICommand
{
    public List<Regex> Patterns => [
        new Regex("^stox$")
    ];

    public string? HelpText => "Get stox data";
    public UserRight RequiredRight => UserRight.Loser;
    // Increased timeout as it has to wait for Sneedchat to echo the message and that can be slow sometimes
    public TimeSpan Timeout => TimeSpan.FromSeconds(15);
    public RateLimitOptionsModel? RateLimitOptions => null;

    internal static List<Stox> LastStocksValues { get; set; } = new();
    public async Task RunCommand(ChatBot botInstance, MessageModel message, UserDbModel user, GroupCollection arguments, CancellationToken ctx)
    {
        // fetch json from api
        const string endpoint = "https://api.fishtank.live/v1/stocks";
        using var httpClient = new HttpClient();
        var response = await httpClient.GetAsync(endpoint);
        if (!response.IsSuccessStatusCode)
        {
            await botInstance.SendChatMessageAsync("Failed to fetch stox prices.", autoDeleteAfter: TimeSpan.FromSeconds(5));
            return;
        }

        var json = await response.Content.ReadAsStringAsync();

        var stoxData = JsonSerializer.Deserialize<StoxData>(json);

        if (stoxData == null || stoxData.Stocks.Count == 0)
        {
            await botInstance.SendChatMessageAsync("No stox data available.", autoDeleteAfter: TimeSpan.FromSeconds(5));
            return;
        }

        // sort stocks by current price descending
        stoxData.Stocks.Sort((x, y) => y.CurrentPrice.CompareTo(x.CurrentPrice));

        string msg = "";

        if (LastStocksValues.Count == 0)
        {
            foreach (var stox in stoxData.Stocks)
            {
                msg += $"{stox.Symbol}: ₣{stox.CurrentPrice}\n";
            }
        }
        else
        {
            List<(Stox, Stox?)> CurrentAndPrevious = new();
            for (int i = 0; i < stoxData.Stocks.Count; i++)
            {
                var current = stoxData.Stocks[i];
                var previous = LastStocksValues.FirstOrDefault(x => x.Symbol == current.Symbol);
                CurrentAndPrevious.Add((current, previous));
            }

            foreach (var (current, previous) in CurrentAndPrevious)
            {
                if (previous == null)
                {
                    msg += $"{current.Symbol}: ₣{current.CurrentPrice}\n";
                }
                else
                {
                    int change = current.CurrentPrice - previous.CurrentPrice;
                    string changeStr = "";

                    if (change > 0)
                    {
                        changeStr = $"[B][COLOR=#00ff00]₣{change}↗[/COLOR][/B]";
                    }
                    else if (change < 0)
                    {
                        changeStr = $"[B][COLOR=#ff0000]₣{change}↘[/COLOR][/B]";
                    }
                    else
                    {
                        changeStr = "₣0";
                    }
                    msg += $"{current.Symbol}: ₣{current.CurrentPrice} ({changeStr})\n";
                }
            }
        }
        LastStocksValues = stoxData.Stocks;

        await botInstance.SendChatMessageAsync(msg, autoDeleteAfter: TimeSpan.FromSeconds(90));
    }
}

public class StoxBuyCommand : ICommand
{
    public List<Regex> Patterns => [
        new Regex(@"^stox buy (?<symbol>\w+) (?<amount>\d+)$", RegexOptions.IgnoreCase)
    ];
    public string? HelpText => "Buy stocks with your Kasino balance: !stox buy <symbol> <amount>";
    public UserRight RequiredRight => UserRight.Loser;
    public TimeSpan Timeout => TimeSpan.FromSeconds(15);
    public RateLimitOptionsModel? RateLimitOptions => new()
    {
        MaxInvocations = 5,
        Window = TimeSpan.FromSeconds(30)
    };

    public async Task RunCommand(ChatBot botInstance, MessageModel message, UserDbModel user, GroupCollection arguments,
        CancellationToken ctx)
    {
        var symbol = arguments["symbol"].Value.ToUpper();
        var amount = int.Parse(arguments["amount"].Value);

        if (symbol.Contains("?"))
        {
            await botInstance.SendChatMessageAsync($"{user.FormatUsername()}, can't buy stox of unrevealed fish.",
                true, autoDeleteAfter: TimeSpan.FromSeconds(10));
            return;
        }

        if (amount <= 0)
        {
            await botInstance.SendChatMessageAsync($"{user.FormatUsername()}, amount must be greater than 0.",
                true, autoDeleteAfter: TimeSpan.FromSeconds(10));
            return;
        }

        const string endpoint = "https://api.fishtank.live/v1/stocks";
        using var httpClient = new HttpClient();
        var response = await httpClient.GetAsync(endpoint, ctx);
        if (!response.IsSuccessStatusCode)
        {
            await botInstance.SendChatMessageAsync($"{user.FormatUsername()}, failed to fetch stox prices.",
                true, autoDeleteAfter: TimeSpan.FromSeconds(10));
            return;
        }
        var json = await response.Content.ReadAsStringAsync(ctx);
        var stoxData = JsonSerializer.Deserialize<StoxData>(json);
        var stock = stoxData?.Stocks.FirstOrDefault(s =>
            s.Symbol.Equals(symbol, StringComparison.OrdinalIgnoreCase));

        if (stock == null)
        {
            var available = stoxData?.Stocks.Select(s => s.Symbol) ?? [];
            await botInstance.SendChatMessageAsync(
                $"{user.FormatUsername()}, unknown symbol \"{symbol}\". Available: {string.Join(", ", available)}",
                true, autoDeleteAfter: TimeSpan.FromSeconds(10));
            return;
        }

        var cost = (decimal)stock.CurrentPrice * amount;
        var gambler = await Money.GetGamblerEntityAsync(user.Id, ct: ctx);
        if (gambler == null)
            throw new InvalidOperationException($"Caught a null when retrieving gambler for {user.KfUsername}");

        if (gambler.Balance < cost)
        {
            await botInstance.SendChatMessageAsync(
                $"{user.FormatUsername()}, your balance of {await gambler.Balance.FormatKasinoCurrencyAsync()} isn't enough to buy {amount}x {symbol} at ₣{stock.CurrentPrice} each (total: {await cost.FormatKasinoCurrencyAsync()}).",
                true, autoDeleteAfter: TimeSpan.FromSeconds(10));
            return;
        }

        var connectionString = await SettingsProvider.GetValueAsync(BuiltIn.Keys.BotRedisConnectionString);
        if (connectionString.Value == null)
        {
            await botInstance.SendChatMessageAsync(
                $"{user.FormatUsername()}, stox trading is currently unavailable (Redis not configured).",
                true, autoDeleteAfter: TimeSpan.FromSeconds(10));
            return;
        }
        using var redis = await ConnectionMultiplexer.ConnectAsync(connectionString.Value);
        var db = redis.GetDatabase();

        var portfolioKey = $"Stox.Portfolio.{gambler.Id}";
        var portfolioJson = await db.StringGetAsync(portfolioKey);
        var portfolio = portfolioJson.HasValue
            ? JsonSerializer.Deserialize<Dictionary<string, int>>(portfolioJson.ToString()) ?? []
            : new Dictionary<string, int>();

        portfolio[symbol] = portfolio.GetValueOrDefault(symbol, 0) + amount;
        await db.StringSetAsync(portfolioKey, JsonSerializer.Serialize(portfolio));

        var newBalance = await Money.ModifyBalanceAsync(gambler.Id, -cost, TransactionSourceEventType.StoxPurchase,
            $"Bought {amount}x {symbol} @ ₣{stock.CurrentPrice}", ct: ctx);

        await botInstance.SendChatMessageAsync(
            $"{user.FormatUsername()}, bought {amount}x {symbol} @ ₣{stock.CurrentPrice} for {await cost.FormatKasinoCurrencyAsync()}. New balance: {await newBalance.FormatKasinoCurrencyAsync()}. You now hold {portfolio[symbol]}x {symbol}.",
            true, autoDeleteAfter: TimeSpan.FromSeconds(30));
    }
}

public class StoxSellCommand : ICommand
{
    public List<Regex> Patterns => [
        new Regex(@"^stox sell (?<symbol>\w+) (?<amount>\d+)$", RegexOptions.IgnoreCase)
    ];
    public string? HelpText => "Sell stocks for Kasino balance: !stox sell <symbol> <amount>";
    public UserRight RequiredRight => UserRight.Loser;
    public TimeSpan Timeout => TimeSpan.FromSeconds(15);
    public RateLimitOptionsModel? RateLimitOptions => new()
    {
        MaxInvocations = 5,
        Window = TimeSpan.FromSeconds(30)
    };

    public async Task RunCommand(ChatBot botInstance, MessageModel message, UserDbModel user, GroupCollection arguments,
        CancellationToken ctx)
    {
        var symbol = arguments["symbol"].Value.ToUpper();
        var amount = int.Parse(arguments["amount"].Value);

        if (amount <= 0)
        {
            await botInstance.SendChatMessageAsync($"{user.FormatUsername()}, amount must be greater than 0.",
                true, autoDeleteAfter: TimeSpan.FromSeconds(10));
            return;
        }

        var gambler = await Money.GetGamblerEntityAsync(user.Id, ct: ctx);
        if (gambler == null)
            throw new InvalidOperationException($"Caught a null when retrieving gambler for {user.KfUsername}");

        var connectionString = await SettingsProvider.GetValueAsync(BuiltIn.Keys.BotRedisConnectionString);
        if (connectionString.Value == null)
        {
            await botInstance.SendChatMessageAsync(
                $"{user.FormatUsername()}, stox trading is currently unavailable (Redis not configured).",
                true, autoDeleteAfter: TimeSpan.FromSeconds(10));
            return;
        }
        using var redis = await ConnectionMultiplexer.ConnectAsync(connectionString.Value);
        var db = redis.GetDatabase();

        var portfolioKey = $"Stox.Portfolio.{gambler.Id}";
        var portfolioJson = await db.StringGetAsync(portfolioKey);
        var portfolio = portfolioJson.HasValue
            ? JsonSerializer.Deserialize<Dictionary<string, int>>(portfolioJson.ToString()) ?? []
            : new Dictionary<string, int>();

        var held = portfolio.GetValueOrDefault(symbol, 0);
        if (held < amount)
        {
            await botInstance.SendChatMessageAsync(
                $"{user.FormatUsername()}, you only hold {held}x {symbol} and can't sell {amount}x.",
                true, autoDeleteAfter: TimeSpan.FromSeconds(10));
            return;
        }

        const string endpoint = "https://api.fishtank.live/v1/stocks";
        using var httpClient = new HttpClient();
        var response = await httpClient.GetAsync(endpoint, ctx);
        if (!response.IsSuccessStatusCode)
        {
            await botInstance.SendChatMessageAsync($"{user.FormatUsername()}, failed to fetch stox prices.",
                true, autoDeleteAfter: TimeSpan.FromSeconds(10));
            return;
        }
        var json = await response.Content.ReadAsStringAsync(ctx);
        var stoxData = JsonSerializer.Deserialize<StoxData>(json);
        var stock = stoxData?.Stocks.FirstOrDefault(s =>
            s.Symbol.Equals(symbol, StringComparison.OrdinalIgnoreCase));

        if (stock == null)
        {
            await botInstance.SendChatMessageAsync(
                $"{user.FormatUsername()}, unknown symbol \"{symbol}\".",
                true, autoDeleteAfter: TimeSpan.FromSeconds(10));
            return;
        }

        var proceeds = (decimal)stock.CurrentPrice * amount;
        portfolio[symbol] = held - amount;
        if (portfolio[symbol] == 0)
            portfolio.Remove(symbol);
        await db.StringSetAsync(portfolioKey, JsonSerializer.Serialize(portfolio));

        var newBalance = await Money.ModifyBalanceAsync(gambler.Id, proceeds, TransactionSourceEventType.StoxSale,
            $"Sold {amount}x {symbol} @ ₣{stock.CurrentPrice}", ct: ctx);

        var remaining = portfolio.TryGetValue(symbol, out var rem)
            ? $". You still hold {rem}x {symbol}."
            : $". You no longer hold any {symbol}.";
        await botInstance.SendChatMessageAsync(
            $"{user.FormatUsername()}, sold {amount}x {symbol} @ ₣{stock.CurrentPrice} for {await proceeds.FormatKasinoCurrencyAsync()}. New balance: {await newBalance.FormatKasinoCurrencyAsync()}{remaining}",
            true, autoDeleteAfter: TimeSpan.FromSeconds(30));
    }
}

public class StoxPortfolioCommand : ICommand
{
    public List<Regex> Patterns => [
        new Regex(@"^stox portfolio$", RegexOptions.IgnoreCase),
        new Regex(@"^stox port$", RegexOptions.IgnoreCase)
    ];
    public string? HelpText => "View your stox portfolio: !stox portfolio";
    public UserRight RequiredRight => UserRight.Loser;
    public TimeSpan Timeout => TimeSpan.FromSeconds(15);
    public RateLimitOptionsModel? RateLimitOptions => new()
    {
        MaxInvocations = 3,
        Window = TimeSpan.FromSeconds(30)
    };

    public async Task RunCommand(ChatBot botInstance, MessageModel message, UserDbModel user, GroupCollection arguments,
        CancellationToken ctx)
    {
        var gambler = await Money.GetGamblerEntityAsync(user.Id, ct: ctx);
        if (gambler == null)
            throw new InvalidOperationException($"Caught a null when retrieving gambler for {user.KfUsername}");

        var connectionString = await SettingsProvider.GetValueAsync(BuiltIn.Keys.BotRedisConnectionString);
        if (connectionString.Value == null)
        {
            await botInstance.SendChatMessageAsync(
                $"{user.FormatUsername()}, stox trading is currently unavailable (Redis not configured).",
                true, autoDeleteAfter: TimeSpan.FromSeconds(10));
            return;
        }
        using var redis = await ConnectionMultiplexer.ConnectAsync(connectionString.Value);
        var db = redis.GetDatabase();

        var portfolioKey = $"Stox.Portfolio.{gambler.Id}";
        var portfolioJson = await db.StringGetAsync(portfolioKey);
        var portfolio = portfolioJson.HasValue
            ? JsonSerializer.Deserialize<Dictionary<string, int>>(portfolioJson.ToString()) ?? []
            : new Dictionary<string, int>();

        if (portfolio.Count == 0)
        {
            await botInstance.SendChatMessageAsync(
                $"{user.FormatUsername()}, you don't hold any stocks. Use !stox buy <symbol> <amount> to get started.",
                true, autoDeleteAfter: TimeSpan.FromSeconds(15));
            return;
        }

        const string endpoint = "https://api.fishtank.live/v1/stocks";
        using var httpClient = new HttpClient();
        var response = await httpClient.GetAsync(endpoint, ctx);
        if (!response.IsSuccessStatusCode)
        {
            await botInstance.SendChatMessageAsync($"{user.FormatUsername()}, failed to fetch stox prices.",
                true, autoDeleteAfter: TimeSpan.FromSeconds(10));
            return;
        }
        var json = await response.Content.ReadAsStringAsync(ctx);
        var stoxData = JsonSerializer.Deserialize<StoxData>(json);

        var lines = portfolio
            .OrderBy(kvp => kvp.Key)
            .Select(kvp =>
            {
                var price = stoxData?.Stocks.FirstOrDefault(s =>
                    s.Symbol.Equals(kvp.Key, StringComparison.OrdinalIgnoreCase))?.CurrentPrice;
                var valueStr = price.HasValue
                    ? $" (value: ₣{price.Value * kvp.Value})"
                    : string.Empty;
                return $"{kvp.Key}: {kvp.Value}x{valueStr}";
            });

        await botInstance.SendChatMessageAsync(
            $"{user.FormatUsername()}'s stox portfolio:\n{string.Join("\n", lines)}",
            true, autoDeleteAfter: TimeSpan.FromSeconds(30));
    }
}
