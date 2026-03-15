
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
    public RateLimitOptionsModel? RateLimitOptions => new()
    {
        MaxInvocations = 1,
        Window = TimeSpan.FromSeconds(60),
        Flags = RateLimitFlags.Global
    };

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

        string msg = "[size=50][TABLE]";

        if (LastStocksValues.Count == 0)
        {
            foreach (var stox in stoxData.Stocks)
            {
                msg += $"[TR][TD]{stox.Symbol}[/TD][TD]₣{stox.CurrentPrice}[/TD][/TR]";
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
                    msg += $"[TR][TD]{current.Symbol}[/TD][TD]₣{current.CurrentPrice}[/TD][/TR]";
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
                    msg += $"[TR][TD]{current.Symbol}[/TD][TD]₣{current.CurrentPrice} ({changeStr})[/TD][/TR]";
                }
            }
        }
        LastStocksValues = stoxData.Stocks;

        await botInstance.SendChatMessageAsync(msg, autoDeleteAfter: TimeSpan.FromSeconds(90));
    }
}

internal static class StoxMarket
{
    internal static async Task<bool> IsOpenAsync()
    {
        var setting = await SettingsProvider.GetValueAsync(BuiltIn.Keys.StoxMarketOpen);
        return setting.Value?.Equals("true", StringComparison.OrdinalIgnoreCase) ?? true;
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
        if (!await StoxMarket.IsOpenAsync())
        {
            await botInstance.SendChatMessageAsync($"{user.FormatUsername()}, the stox market is currently closed.",
                true, autoDeleteAfter: TimeSpan.FromSeconds(10));
            return;
        }

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
        if (!await StoxMarket.IsOpenAsync())
        {
            await botInstance.SendChatMessageAsync($"{user.FormatUsername()}, the stox market is currently closed.",
                true, autoDeleteAfter: TimeSpan.FromSeconds(10));
            return;
        }

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
            true, autoDeleteAfter: TimeSpan.FromSeconds(10));
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
        var shortsKey = $"Stox.Shorts.{gambler.Id}";

        var portfolioJson = await db.StringGetAsync(portfolioKey);
        var portfolio = portfolioJson.HasValue
            ? JsonSerializer.Deserialize<Dictionary<string, int>>(portfolioJson.ToString()) ?? []
            : new Dictionary<string, int>();

        var shortsJson = await db.StringGetAsync(shortsKey);
        var shorts = shortsJson.HasValue
            ? JsonSerializer.Deserialize<Dictionary<string, StoxShortPosition>>(shortsJson.ToString()) ?? []
            : new Dictionary<string, StoxShortPosition>();

        if (portfolio.Count == 0 && shorts.Count == 0)
        {
            await botInstance.SendChatMessageAsync(
                $"{user.FormatUsername()}, you don't hold any stocks. Use !stox buy <symbol> <amount> or !stox short <symbol> <amount> to get started.",
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

        var outputLines = new List<string>();

        if (portfolio.Count > 0)
        {
            outputLines.Add("Longs:");
            outputLines.AddRange(portfolio
                .OrderBy(kvp => kvp.Key)
                .Select(kvp =>
                {
                    var price = stoxData?.Stocks.FirstOrDefault(s =>
                        s.Symbol.Equals(kvp.Key, StringComparison.OrdinalIgnoreCase))?.CurrentPrice;
                    var valueStr = price.HasValue
                        ? $" (value: ₣{price.Value * kvp.Value})"
                        : string.Empty;
                    return $"  {kvp.Key}: {kvp.Value}x{valueStr}";
                }));
        }

        if (shorts.Count > 0)
        {
            outputLines.Add("Shorts:");
            outputLines.AddRange(shorts
                .OrderBy(kvp => kvp.Key)
                .Select(kvp =>
                {
                    var pos = kvp.Value;
                    var currentPrice = stoxData?.Stocks.FirstOrDefault(s =>
                        s.Symbol.Equals(kvp.Key, StringComparison.OrdinalIgnoreCase))?.CurrentPrice;
                    if (!currentPrice.HasValue)
                        return $"  {kvp.Key}: {pos.Quantity}x short (entry: ₣{pos.EntryPrice:0.##})";
                    var pnl = pos.Quantity * (pos.EntryPrice - currentPrice.Value);
                    var pnlStr = pnl >= 0
                        ? $"[COLOR=#00ff00]+₣{pnl:0.##}[/COLOR]"
                        : $"[COLOR=#ff0000]₣{pnl:0.##}[/COLOR]";
                    return $"  {kvp.Key}: {pos.Quantity}x short (entry: ₣{pos.EntryPrice:0.##}, current: ₣{currentPrice.Value}, P&L: {pnlStr})";
                }));
        }

        await botInstance.SendChatMessageAsync(
            $"{user.FormatUsername()}'s stox portfolio:\n{string.Join("\n", outputLines)}",
            true, autoDeleteAfter: TimeSpan.FromSeconds(30));
    }
}

public class StoxShortCommand : ICommand
{
    public List<Regex> Patterns => [
        new Regex(@"^stox short (?<symbol>\w+) (?<amount>\d+)$", RegexOptions.IgnoreCase)
    ];
    public string? HelpText => "Short sell stocks (profit if price drops): !stox short <symbol> <amount>";
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
        if (!await StoxMarket.IsOpenAsync())
        {
            await botInstance.SendChatMessageAsync($"{user.FormatUsername()}, the stox market is currently closed.",
                true, autoDeleteAfter: TimeSpan.FromSeconds(10));
            return;
        }

        var symbol = arguments["symbol"].Value.ToUpper();
        var amount = int.Parse(arguments["amount"].Value);

        if (symbol.Contains("?"))
        {
            await botInstance.SendChatMessageAsync($"{user.FormatUsername()}, can't short unrevealed fish.",
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

        var collateral = (decimal)stock.CurrentPrice * amount;
        var gambler = await Money.GetGamblerEntityAsync(user.Id, ct: ctx);
        if (gambler == null)
            throw new InvalidOperationException($"Caught a null when retrieving gambler for {user.KfUsername}");

        if (gambler.Balance < collateral)
        {
            await botInstance.SendChatMessageAsync(
                $"{user.FormatUsername()}, you need {await collateral.FormatKasinoCurrencyAsync()} collateral to short {amount}x {symbol} at ₣{stock.CurrentPrice} each, but only have {await gambler.Balance.FormatKasinoCurrencyAsync()}.",
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

        var shortsKey = $"Stox.Shorts.{gambler.Id}";
        var shortsJson = await db.StringGetAsync(shortsKey);
        var shorts = shortsJson.HasValue
            ? JsonSerializer.Deserialize<Dictionary<string, StoxShortPosition>>(shortsJson.ToString()) ?? []
            : new Dictionary<string, StoxShortPosition>();

        if (shorts.TryGetValue(symbol, out var existing))
        {
            // Weighted average entry price when adding to an existing short
            var totalQty = existing.Quantity + amount;
            var avgPrice = (existing.Quantity * existing.EntryPrice + amount * (decimal)stock.CurrentPrice) / totalQty;
            shorts[symbol] = new StoxShortPosition { Quantity = totalQty, EntryPrice = avgPrice };
        }
        else
        {
            shorts[symbol] = new StoxShortPosition { Quantity = amount, EntryPrice = stock.CurrentPrice };
        }
        await db.StringSetAsync(shortsKey, JsonSerializer.Serialize(shorts));

        var newBalance = await Money.ModifyBalanceAsync(gambler.Id, -collateral, TransactionSourceEventType.StoxShort,
            $"Opened short {amount}x {symbol} @ ₣{stock.CurrentPrice}", ct: ctx);

        var pos = shorts[symbol];
        await botInstance.SendChatMessageAsync(
            $"{user.FormatUsername()}, opened short of {amount}x {symbol} @ ₣{stock.CurrentPrice}. Collateral locked: {await collateral.FormatKasinoCurrencyAsync()}. New balance: {await newBalance.FormatKasinoCurrencyAsync()}. Total short: {pos.Quantity}x {symbol} (avg entry: ₣{pos.EntryPrice:0.##}).",
            true, autoDeleteAfter: TimeSpan.FromSeconds(30));
    }
}

public class StoxCoverCommand : ICommand
{
    public List<Regex> Patterns => [
        new Regex(@"^stox cover (?<symbol>\w+) (?<amount>\d+)$", RegexOptions.IgnoreCase)
    ];
    public string? HelpText => "Cover (close) a short position: !stox cover <symbol> <amount>";
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
        if (!await StoxMarket.IsOpenAsync())
        {
            await botInstance.SendChatMessageAsync($"{user.FormatUsername()}, the stox market is currently closed.",
                true, autoDeleteAfter: TimeSpan.FromSeconds(10));
            return;
        }

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

        var shortsKey = $"Stox.Shorts.{gambler.Id}";
        var shortsJson = await db.StringGetAsync(shortsKey);
        var shorts = shortsJson.HasValue
            ? JsonSerializer.Deserialize<Dictionary<string, StoxShortPosition>>(shortsJson.ToString()) ?? []
            : new Dictionary<string, StoxShortPosition>();

        if (!shorts.TryGetValue(symbol, out var position) || position.Quantity < amount)
        {
            var held = shorts.TryGetValue(symbol, out var p) ? p.Quantity : 0;
            await botInstance.SendChatMessageAsync(
                $"{user.FormatUsername()}, you only have {held}x {symbol} shorted and can't cover {amount}x.",
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

        // Return collateral for covered shares plus P&L.
        // P&L per share = entryPrice - currentPrice (positive = profit, negative = loss)
        var entryPrice = position.EntryPrice;
        var pnl = (decimal)amount * (entryPrice - stock.CurrentPrice);
        var collateralReturn = (decimal)amount * entryPrice;
        var netEffect = collateralReturn + pnl;

        position.Quantity -= amount;
        if (position.Quantity == 0)
            shorts.Remove(symbol);
        await db.StringSetAsync(shortsKey, JsonSerializer.Serialize(shorts));

        var newBalance = await Money.ModifyBalanceAsync(gambler.Id, netEffect, TransactionSourceEventType.StoxCover,
            $"Covered {amount}x {symbol} short @ entry ₣{entryPrice:0.##}, close ₣{stock.CurrentPrice}", ct: ctx);

        var pnlStr = pnl >= 0
            ? $"[B][COLOR=#00ff00]+{await pnl.FormatKasinoCurrencyAsync()}[/COLOR][/B]"
            : $"[B][COLOR=#ff0000]{await pnl.FormatKasinoCurrencyAsync()}[/COLOR][/B]";
        var remaining = shorts.TryGetValue(symbol, out var rem)
            ? $". You still short {rem.Quantity}x {symbol}."
            : $". No remaining short position in {symbol}.";
        await botInstance.SendChatMessageAsync(
            $"{user.FormatUsername()}, covered {amount}x {symbol} short. Entry: ₣{entryPrice:0.##}, close: ₣{stock.CurrentPrice}. P&L: {pnlStr}. New balance: {await newBalance.FormatKasinoCurrencyAsync()}{remaining}",
            true, autoDeleteAfter: TimeSpan.FromSeconds(30));
    }
}

public class StoxOpenMarketCommand : ICommand
{
    public List<Regex> Patterns => [
        new Regex(@"^stox open$", RegexOptions.IgnoreCase)
    ];
    public string? HelpText => null;
    public UserRight RequiredRight => UserRight.TrueAndHonest;
    public TimeSpan Timeout => TimeSpan.FromSeconds(10);
    public RateLimitOptionsModel? RateLimitOptions => null;

    public async Task RunCommand(ChatBot botInstance, MessageModel message, UserDbModel user, GroupCollection arguments,
        CancellationToken ctx)
    {
        await SettingsProvider.SetValueAsBooleanAsync(BuiltIn.Keys.StoxMarketOpen, true);
        await botInstance.SendChatMessageAsync("Stox market is now [B][COLOR=#00ff00]OPEN[/COLOR][/B]. Trading enabled.",
            true, autoDeleteAfter: TimeSpan.FromSeconds(30));
    }
}

public class StoxCloseMarketCommand : ICommand
{
    public List<Regex> Patterns => [
        new Regex(@"^stox close$", RegexOptions.IgnoreCase)
    ];
    public string? HelpText => null;
    public UserRight RequiredRight => UserRight.TrueAndHonest;
    public TimeSpan Timeout => TimeSpan.FromSeconds(10);
    public RateLimitOptionsModel? RateLimitOptions => null;

    public async Task RunCommand(ChatBot botInstance, MessageModel message, UserDbModel user, GroupCollection arguments,
        CancellationToken ctx)
    {
        await SettingsProvider.SetValueAsBooleanAsync(BuiltIn.Keys.StoxMarketOpen, false);
        await botInstance.SendChatMessageAsync("Stox market is now [B][COLOR=#ff0000]CLOSED[/COLOR][/B]. Trading suspended.",
            true, autoDeleteAfter: TimeSpan.FromSeconds(30));
    }
}
