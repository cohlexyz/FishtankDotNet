
using System.Globalization;
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
    public UserRight RequiredRight => UserRight.Guest;
    // Increased timeout as it has to wait for Sneedchat to echo the message and that can be slow sometimes
    public TimeSpan Timeout => TimeSpan.FromSeconds(15);
    public RateLimitOptionsModel? RateLimitOptions => new()
    {
        MaxInvocations = 1,
        Window = TimeSpan.FromSeconds(60),
        Flags = RateLimitFlags.NoResponse
    };

    internal static List<Stox> LastStocksValues { get; set; } = new();

    public bool WhisperCanInvoke => true;
    public async Task RunCommand(ChatBot botInstance, BotCommandMessageModel message, UserDbModel user, GroupCollection arguments, CancellationToken ctx)
    {
        // fetch json from api
        const string endpoint = "https://api.fishtank.live/v1/stocks";
        using var httpClient = new HttpClient();
        var response = await httpClient.GetAsync(endpoint);
        if (!response.IsSuccessStatusCode)
        {
            await botInstance.SendChatMessageAsync("Failed to fetch stox prices.", whisperTo: user.KfId);
            return;
        }

        var json = await response.Content.ReadAsStringAsync();

        var stoxData = JsonSerializer.Deserialize<StoxData>(json);

        if (stoxData == null || stoxData.Stocks.Count == 0)
        {
            await botInstance.SendChatMessageAsync("No stox data available.", whisperTo: user.KfId);
            return;
        }

        // sort stocks by current price descending
        stoxData.Stocks.Sort((x, y) => y.CurrentPrice.CompareTo(x.CurrentPrice));

        string msg = $"{user.FormatUsername()}, [size=80][TABLE width=\"1%\"]";

        var currentAndPrevious = stoxData.Stocks
            .Select(s => (current: s, previous: LastStocksValues.Count > 0 ? LastStocksValues.FirstOrDefault(x => x.Symbol == s.Symbol) : null))
            .ToList();

        static string GetCells(Stox current, Stox? previous)
        {
            if (previous == null)
                return $"[TD]{current.Symbol}[/TD][TD]₣{current.CurrentPrice}[/TD]";

            int change = current.CurrentPrice - previous.CurrentPrice;
            string changeStr = change > 0
                ? $"[B][COLOR=#00ff00]₣{change}↗[/COLOR][/B]"
                : change < 0
                    ? $"[B][COLOR=#ff0000]₣{change}↘[/COLOR][/B]"
                    : "₣0";

            return $"[TD]{current.Symbol}[/TD][TD]₣{current.CurrentPrice} ({changeStr})[/TD]";
        }

        // we want them spread across two rows
        var stoxPerRow = (int)Math.Ceiling(currentAndPrevious.Count / 2.0);

        for (int i = 0; i < 2; i++)
        {
            var row = string.Empty;
            for (int j = 0; j < stoxPerRow; j++)
            {
                var index = i * stoxPerRow + j;
                if (index >= currentAndPrevious.Count)
                    break;

                var (current, previous) = currentAndPrevious[index];
                row += GetCells(current, previous);
            }
            msg += $"[TR]{row}[/TR]";
        }

        LastStocksValues = stoxData.Stocks;

        await botInstance.SendChatMessageAsync(msg, whisperTo: user.KfId);
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
        new Regex(@"^buy (?<symbol>\w+) (?<amount>\d+(?:\.\d+)?)$", RegexOptions.IgnoreCase),
        new Regex(@"^long (?<symbol>\w+) (?<amount>\d+(?:\.\d+)?)$", RegexOptions.IgnoreCase),
        new Regex(@"^stox buy (?<symbol>\w+) (?<amount>\d+(?:\.\d+)?)$", RegexOptions.IgnoreCase),
        new Regex(@"^stox long (?<symbol>\w+) (?<amount>\d+(?:\.\d+)?)$", RegexOptions.IgnoreCase),
        new Regex(@"^buy (?<symbol>\w+) (?<amount>\d+(?:\.\d+)?) (?<leverage>\d+(?:\.\d+)?)x$", RegexOptions.IgnoreCase),
        new Regex(@"^long (?<symbol>\w+) (?<amount>\d+(?:\.\d+)?) (?<leverage>\d+(?:\.\d+)?)x$", RegexOptions.IgnoreCase),
        new Regex(@"^stox buy (?<symbol>\w+) (?<amount>\d+(?:\.\d+)?) (?<leverage>\d+(?:\.\d+)?)x$", RegexOptions.IgnoreCase),
        new Regex(@"^stox long (?<symbol>\w+) (?<amount>\d+(?:\.\d+)?) (?<leverage>\d+(?:\.\d+)?)x$", RegexOptions.IgnoreCase)
    ];
    public string? HelpText => "Buy stocks with your Kasino balance: !stox buy <symbol> <amount> [<leverage>x]"; // e.g. !stox buy FISH 10 3x for 3x leverage (can go into debt!)"
    public UserRight RequiredRight => UserRight.Loser;
    public TimeSpan Timeout => TimeSpan.FromSeconds(15);
    public RateLimitOptionsModel? RateLimitOptions => new()
    {
        MaxInvocations = 2,
        Window = TimeSpan.FromSeconds(60),
        Flags = RateLimitFlags.NoResponse
    };

    public bool WhisperCanInvoke => true;

    public async Task RunCommand(ChatBot botInstance, BotCommandMessageModel message, UserDbModel user, GroupCollection arguments,
        CancellationToken ctx)
    {
        if (!await StoxMarket.IsOpenAsync())
        {
            await botInstance.SendWhisperAsync(user.KfId, $"the stox market is currently closed.");
            return;
        }

        var symbol = arguments["symbol"].Value.ToUpper();
        var amount = decimal.Parse(arguments["amount"].Value, CultureInfo.InvariantCulture);

        if (symbol.Contains("?"))
        {
            await botInstance.SendWhisperAsync(user.KfId, $"can't buy stox of unrevealed fish.");
            return;
        }

        if (amount <= 0)
        {
            await botInstance.SendWhisperAsync(user.KfId, $"amount must be greater than 0.");
            return;
        }

        const string endpoint = "https://api.fishtank.live/v1/stocks";
        using var httpClient = new HttpClient();
        var response = await httpClient.GetAsync(endpoint, ctx);
        if (!response.IsSuccessStatusCode)
        {
            await botInstance.SendWhisperAsync(user.KfId, $"failed to fetch stox prices.");
            return;
        }
        var json = await response.Content.ReadAsStringAsync(ctx);
        var stoxData = JsonSerializer.Deserialize<StoxData>(json);
        var stock = stoxData?.Stocks.FirstOrDefault(s =>
            s.Symbol.Equals(symbol, StringComparison.OrdinalIgnoreCase));

        if (stock == null)
        {
            var available = stoxData?.Stocks.Select(s => s.Symbol) ?? [];
            await botInstance.SendWhisperAsync(user.KfId,
                $"unknown symbol \"{symbol}\". Available: {string.Join(", ", available)}");
            return;
        }

        if (stock.CurrentPrice <= 0)
        {
            await botInstance.SendWhisperAsync(user.KfId, $"{symbol} is currently not for sale.");
            return;
        }

        var leverageStr = arguments["leverage"].Value;
        var leverage = string.IsNullOrEmpty(leverageStr) ? 1m : decimal.Parse(leverageStr, CultureInfo.InvariantCulture);
        const decimal maxLeverage = 10m;
        if (leverage < 1m || leverage > maxLeverage)
        {
            await botInstance.SendWhisperAsync(user.KfId, $"leverage must be between 1x and {maxLeverage:0.##}x.");
            return;
        }

        var positionValue = (decimal)stock.CurrentPrice * amount;
        var margin = positionValue / leverage;
        var borrowed = positionValue - margin;

        var gambler = await Money.GetGamblerEntityAsync(user.Id, ct: ctx);
        if (gambler == null)
            throw new InvalidOperationException($"Caught a null when retrieving gambler for {user.KfUsername}");

        if (gambler.Balance < margin)
        {
            var balanceMsg = leverage > 1m
                ? $"you need {await margin.FormatKasinoCurrencyAsync()} margin ({leverage:0.##}x leverage) to buy {amount:0.####}x {symbol} at ₣{stock.CurrentPrice} (full position: {await positionValue.FormatKasinoCurrencyAsync()}), but only have {await gambler.Balance.FormatKasinoCurrencyAsync()}."
                : $"your balance of {await gambler.Balance.FormatKasinoCurrencyAsync()} isn't enough to buy {amount:0.####}x {symbol} at ₣{stock.CurrentPrice} each (total: {await positionValue.FormatKasinoCurrencyAsync()}[plain])[/plain].";

            await botInstance.SendWhisperAsync(user.KfId, balanceMsg);
            return;
        }

        var connectionString = await SettingsProvider.GetValueAsync(BuiltIn.Keys.BotRedisConnectionString);
        if (connectionString.Value == null)
        {
            await botInstance.SendWhisperAsync(user.KfId,
                $"stox trading is currently unavailable (Redis not configured).");
            return;
        }
        using var redis = await ConnectionMultiplexer.ConnectAsync(connectionString.Value);
        var db = redis.GetDatabase();

        if (leverage > 1m)
        {
            var leveragedKey = $"Stox.Leveraged.{gambler.Id}";
            var leveragedJson = await db.StringGetAsync(leveragedKey);
            var leveragedPortfolio = leveragedJson.HasValue
                ? JsonSerializer.Deserialize<Dictionary<string, StoxLeveragedPosition>>(leveragedJson.ToString()) ?? []
                : new Dictionary<string, StoxLeveragedPosition>();

            if (leveragedPortfolio.TryGetValue(symbol, out var existing))
            {
                var totalQty = existing.Quantity + amount;
                var avgEntry = (existing.Quantity * existing.EntryPrice + amount * (decimal)stock.CurrentPrice) / totalQty;
                leveragedPortfolio[symbol] = new StoxLeveragedPosition
                {
                    Quantity = totalQty,
                    EntryPrice = avgEntry,
                    Borrowed = existing.Borrowed + borrowed
                };
            }
            else
            {
                leveragedPortfolio[symbol] = new StoxLeveragedPosition
                {
                    Quantity = amount,
                    EntryPrice = stock.CurrentPrice,
                    Borrowed = borrowed
                };
            }
            await db.StringSetAsync(leveragedKey, JsonSerializer.Serialize(leveragedPortfolio));

            var newBalance = await Money.ModifyBalanceAsync(gambler.Id, -margin, TransactionSourceEventType.StoxLeveragedPurchase,
                $"Leveraged buy {amount:0.####}x {symbol} @ ₣{stock.CurrentPrice} ({leverage:0.##}x, margin: ₣{margin:0.##}, borrowed: ₣{borrowed:0.##})", ct: ctx);

            var pos = leveragedPortfolio[symbol];
            await botInstance.SendChatMessageAsync(
                $"{user.FormatUsername()}, opened {leverage:0.##}x leveraged: {amount:0.####}x {symbol} @ ₣{stock.CurrentPrice}. Margin: {await margin.FormatKasinoCurrencyAsync()} | Borrowed: ₣{borrowed:0.##} | Full position: {await positionValue.FormatKasinoCurrencyAsync()}. New balance: {await newBalance.FormatKasinoCurrencyAsync()}. Total leveraged: {pos.Quantity:0.####}x {symbol} (avg entry: ₣{pos.EntryPrice:0.##}[plain])[/plain].",
                true, whisperTo: user.KfId, autoDeleteAfter: TimeSpan.FromSeconds(20));
        }
        else
        {
            var portfolioKey = $"Stox.Portfolio.{gambler.Id}";
            var portfolioJson = await db.StringGetAsync(portfolioKey);
            var portfolio = portfolioJson.HasValue
                ? JsonSerializer.Deserialize<Dictionary<string, decimal>>(portfolioJson.ToString()) ?? []
                : new Dictionary<string, decimal>();

            portfolio[symbol] = portfolio.GetValueOrDefault(symbol, 0m) + amount;
            await db.StringSetAsync(portfolioKey, JsonSerializer.Serialize(portfolio));

            var newBalance = await Money.ModifyBalanceAsync(gambler.Id, -positionValue, TransactionSourceEventType.StoxPurchase,
                $"{user.KfUsername}, you bought {amount:0.####}x {symbol} @ ₣{stock.CurrentPrice}", ct: ctx);

            await botInstance.SendChatMessageAsync(
                $"{user.FormatUsername()}, you bought {amount:0.####}x {symbol} @ ₣{stock.CurrentPrice} for {await positionValue.FormatKasinoCurrencyAsync()}. New balance: {await newBalance.FormatKasinoCurrencyAsync()}. You now hold {portfolio[symbol]:0.####}x {symbol}.",
                true, whisperTo: user.KfId, autoDeleteAfter: TimeSpan.FromSeconds(20));
        }
    }
}

public class StoxSellCommand : ICommand
{
    public List<Regex> Patterns => [
        new Regex(@"^sell (?<symbol>\w+) (?<amount>\d+(?:\.\d+)?)$", RegexOptions.IgnoreCase),
        new Regex(@"^stox sell (?<symbol>\w+) (?<amount>\d+(?:\.\d+)?)$", RegexOptions.IgnoreCase)
    ];
    public string? HelpText => "Sell stocks for Kasino balance: !stox sell <symbol> <amount>";
    public UserRight RequiredRight => UserRight.Loser;
    public TimeSpan Timeout => TimeSpan.FromSeconds(15);
    public RateLimitOptionsModel? RateLimitOptions => new()
    {
        MaxInvocations = 2,
        Window = TimeSpan.FromSeconds(60),
        Flags = RateLimitFlags.NoResponse
    };

    public bool WhisperCanInvoke => true;

    public async Task RunCommand(ChatBot botInstance, BotCommandMessageModel message, UserDbModel user, GroupCollection arguments,
        CancellationToken ctx)
    {
        if (!await StoxMarket.IsOpenAsync())
        {
            await botInstance.SendWhisperAsync(user.KfId, $"the stox market is currently closed.");
            return;
        }

        var symbol = arguments["symbol"].Value.ToUpper();
        var amount = decimal.Parse(arguments["amount"].Value, CultureInfo.InvariantCulture);

        if (amount <= 0)
        {
            await botInstance.SendWhisperAsync(user.KfId, $"amount must be greater than 0.");
            return;
        }

        var gambler = await Money.GetGamblerEntityAsync(user.Id, ct: ctx);
        if (gambler == null)
            throw new InvalidOperationException($"Caught a null when retrieving gambler for {user.KfUsername}");

        var connectionString = await SettingsProvider.GetValueAsync(BuiltIn.Keys.BotRedisConnectionString);
        if (connectionString.Value == null)
        {
            await botInstance.SendWhisperAsync(user.KfId, $"stox trading is currently unavailable (Redis not configured).");
            return;
        }
        using var redis = await ConnectionMultiplexer.ConnectAsync(connectionString.Value);
        var db = redis.GetDatabase();

        var portfolioKey = $"Stox.Portfolio.{gambler.Id}";
        var leveragedKey = $"Stox.Leveraged.{gambler.Id}";

        var portfolioJson = await db.StringGetAsync(portfolioKey);
        var portfolio = portfolioJson.HasValue
            ? JsonSerializer.Deserialize<Dictionary<string, decimal>>(portfolioJson.ToString()) ?? []
            : new Dictionary<string, decimal>();

        var leveragedJson = await db.StringGetAsync(leveragedKey);
        var leveragedPortfolio = leveragedJson.HasValue
            ? JsonSerializer.Deserialize<Dictionary<string, StoxLeveragedPosition>>(leveragedJson.ToString()) ?? []
            : new Dictionary<string, StoxLeveragedPosition>();

        var regularHeld = portfolio.GetValueOrDefault(symbol, 0m);
        var leveragedPos = leveragedPortfolio.GetValueOrDefault(symbol);
        var leveragedHeld = leveragedPos?.Quantity ?? 0m;
        var totalHeld = regularHeld + leveragedHeld;

        if (totalHeld < amount)
        {
            var heldMsg = leveragedHeld > 0
                ? $"you only hold {totalHeld:0.####}x {symbol} (regular: {regularHeld:0.####}, leveraged: {leveragedHeld:0.####}) and can't sell {amount:0.####}x."
                : $"you only hold {regularHeld:0.####}x {symbol} and can't sell {amount:0.####}x.";
            await botInstance.SendWhisperAsync(user.KfId, heldMsg);
            return;
        }

        const string endpoint = "https://api.fishtank.live/v1/stocks";
        using var httpClient = new HttpClient();
        var response = await httpClient.GetAsync(endpoint, ctx);
        if (!response.IsSuccessStatusCode)
        {
            await botInstance.SendWhisperAsync(user.KfId, $"failed to fetch stox prices.");
            return;
        }
        var json = await response.Content.ReadAsStringAsync(ctx);
        var stoxData = JsonSerializer.Deserialize<StoxData>(json);
        var stock = stoxData?.Stocks.FirstOrDefault(s =>
            s.Symbol.Equals(symbol, StringComparison.OrdinalIgnoreCase));

        if (stock == null)
        {
            await botInstance.SendWhisperAsync(user.KfId, $"unknown symbol \"{symbol}\".");
            return;
        }

        var remainingToSell = amount;
        decimal regularSold = 0m;
        decimal regularProceeds = 0m;
        decimal leveragedSold = 0m;
        decimal leveragedBorrowedRepaid = 0m;
        decimal leveragedNet = 0m;

        // Sell from regular (non-leveraged) portfolio first
        if (regularHeld > 0 && remainingToSell > 0)
        {
            regularSold = Math.Min(remainingToSell, regularHeld);
            regularProceeds = (decimal)stock.CurrentPrice * regularSold;
            portfolio[symbol] = regularHeld - regularSold;
            if (portfolio[symbol] == 0m) portfolio.Remove(symbol);
            remainingToSell -= regularSold;
        }

        // Then sell from leveraged portfolio (proceeds minus borrowed = net, can be negative)
        if (remainingToSell > 0 && leveragedPos != null)
        {
            leveragedSold = remainingToSell;
            var borrowedPerShare = leveragedPos.Borrowed / leveragedPos.Quantity;
            leveragedBorrowedRepaid = borrowedPerShare * leveragedSold;
            leveragedNet = (decimal)stock.CurrentPrice * leveragedSold - leveragedBorrowedRepaid;

            leveragedPos.Quantity -= leveragedSold;
            leveragedPos.Borrowed -= leveragedBorrowedRepaid;
            if (leveragedPos.Quantity <= 0m)
                leveragedPortfolio.Remove(symbol);
            else
                leveragedPortfolio[symbol] = leveragedPos;
        }

        await db.StringSetAsync(portfolioKey, JsonSerializer.Serialize(portfolio));
        await db.StringSetAsync(leveragedKey, JsonSerializer.Serialize(leveragedPortfolio));

        decimal finalBalance = gambler.Balance;
        if (regularSold > 0)
            finalBalance = await Money.ModifyBalanceAsync(gambler.Id, regularProceeds, TransactionSourceEventType.StoxSale,
                $"Sold {regularSold:0.####}x {symbol} @ ₣{stock.CurrentPrice}", ct: ctx);
        if (leveragedSold > 0)
            finalBalance = await Money.ModifyBalanceAsync(gambler.Id, leveragedNet, TransactionSourceEventType.StoxLeveragedSale,
                $"Closed leveraged {leveragedSold:0.####}x {symbol} @ ₣{stock.CurrentPrice}, repaid ₣{leveragedBorrowedRepaid:0.##}", ct: ctx);

        var regularRem = portfolio.TryGetValue(symbol, out var rr) ? rr : 0m;
        var leveragedRem = leveragedPortfolio.TryGetValue(symbol, out var lr) ? lr.Quantity : 0m;
        var totalRem = regularRem + leveragedRem;
        string remaining;
        if (totalRem == 0)
            remaining = $". You no longer hold any {symbol}.";
        else if (regularRem > 0 && leveragedRem > 0)
            remaining = $". You still hold {totalRem:0.####}x {symbol} (regular: {regularRem:0.####}, leveraged: {leveragedRem:0.####}).";
        else if (regularRem > 0)
            remaining = $". You still hold {regularRem:0.####}x {symbol}.";
        else
            remaining = $". You still hold {leveragedRem:0.####}x {symbol} (leveraged).";

        string saleMsg;
        if (leveragedSold == 0m)
        {
            saleMsg = $"{user.FormatUsername()}, you sold {amount:0.####}x {symbol} @ ₣{stock.CurrentPrice} for {await regularProceeds.FormatKasinoCurrencyAsync()}. New balance: {await finalBalance.FormatKasinoCurrencyAsync()}{remaining}";
        }
        else if (regularSold == 0m)
        {
            var grossProceeds = (decimal)stock.CurrentPrice * leveragedSold;
            var netColor = leveragedNet >= 0 ? "#00ff00" : "#ff0000";
            saleMsg = $"{user.FormatUsername()}, sold {amount:0.####}x {symbol} @ ₣{stock.CurrentPrice}: gross ₣{grossProceeds:0.##} - ₣{leveragedBorrowedRepaid:0.##} borrowed = [B][COLOR={netColor}]{await leveragedNet.FormatKasinoCurrencyAsync()}[/COLOR][/B] net. New balance: {await finalBalance.FormatKasinoCurrencyAsync()}{remaining}";
        }
        else
        {
            var grossLevProceeds = (decimal)stock.CurrentPrice * leveragedSold;
            var netColor = leveragedNet >= 0 ? "#00ff00" : "#ff0000";
            saleMsg = $"{user.FormatUsername()}, sold {amount:0.####}x {symbol} @ ₣{stock.CurrentPrice}. Regular ({regularSold:0.####}x): {await regularProceeds.FormatKasinoCurrencyAsync()}. Leveraged ({leveragedSold:0.####}x): ₣{grossLevProceeds:0.##} - ₣{leveragedBorrowedRepaid:0.##} borrowed = [B][COLOR={netColor}]{await leveragedNet.FormatKasinoCurrencyAsync()}[/COLOR][/B] net. New balance: {await finalBalance.FormatKasinoCurrencyAsync()}{remaining}";
        }

        await botInstance.SendChatMessageAsync(saleMsg, true, whisperTo: user.KfId, autoDeleteAfter: TimeSpan.FromSeconds(20));
    }
}

public class StoxPortfolioCommand : ICommand
{
    public List<Regex> Patterns => [
        new Regex(@"^portfolio$", RegexOptions.IgnoreCase),
        new Regex(@"^port$", RegexOptions.IgnoreCase),
        new Regex(@"^stox portfolio$", RegexOptions.IgnoreCase),
        new Regex(@"^stox port$", RegexOptions.IgnoreCase)
    ];
    public string? HelpText => "View your stox portfolio: !stox portfolio";
    public UserRight RequiredRight => UserRight.Loser;
    public TimeSpan Timeout => TimeSpan.FromSeconds(15);
    public RateLimitOptionsModel? RateLimitOptions => new()
    {
        MaxInvocations = 2,
        Window = TimeSpan.FromSeconds(120),
        Flags = RateLimitFlags.NoResponse
    };

    public bool WhisperCanInvoke => true;

    public async Task RunCommand(ChatBot botInstance, BotCommandMessageModel message, UserDbModel user, GroupCollection arguments,
        CancellationToken ctx)
    {
        var gambler = await Money.GetGamblerEntityAsync(user.Id, ct: ctx);
        if (gambler == null)
            throw new InvalidOperationException($"Caught a null when retrieving gambler for {user.KfUsername}");

        var connectionString = await SettingsProvider.GetValueAsync(BuiltIn.Keys.BotRedisConnectionString);
        if (connectionString.Value == null)
        {
            await botInstance.SendWhisperAsync(user.KfId, $"stox trading is currently unavailable (Redis not configured).");
            return;
        }
        using var redis = await ConnectionMultiplexer.ConnectAsync(connectionString.Value);
        var db = redis.GetDatabase();

        var portfolioKey = $"Stox.Portfolio.{gambler.Id}";
        var shortsKey = $"Stox.Shorts.{gambler.Id}";
        var leveragedKey = $"Stox.Leveraged.{gambler.Id}";

        var portfolioJson = await db.StringGetAsync(portfolioKey);
        var portfolio = portfolioJson.HasValue
            ? JsonSerializer.Deserialize<Dictionary<string, decimal>>(portfolioJson.ToString()) ?? new Dictionary<string, decimal>()
            : new Dictionary<string, decimal>();

        var shortsJson = await db.StringGetAsync(shortsKey);
        var shorts = shortsJson.HasValue
            ? JsonSerializer.Deserialize<Dictionary<string, StoxShortPosition>>(shortsJson.ToString()) ?? new Dictionary<string, StoxShortPosition>()
            : new Dictionary<string, StoxShortPosition>();

        var leveragedJson = await db.StringGetAsync(leveragedKey);
        var leveraged = leveragedJson.HasValue
            ? JsonSerializer.Deserialize<Dictionary<string, StoxLeveragedPosition>>(leveragedJson.ToString()) ?? new Dictionary<string, StoxLeveragedPosition>()
            : new Dictionary<string, StoxLeveragedPosition>();

        if (portfolio.Count == 0 && shorts.Count == 0 && leveraged.Count == 0)
        {
            await botInstance.SendWhisperAsync(user.KfId, $"you don't hold any stocks. Use !stox buy <symbol> <amount> [<leverage>x] or !stox short <symbol> <amount> to get started.");
            return;
        }

        const string endpoint = "https://api.fishtank.live/v1/stocks";
        using var httpClient = new HttpClient();
        var response = await httpClient.GetAsync(endpoint, ctx);
        if (!response.IsSuccessStatusCode)
        {
            await botInstance.SendWhisperAsync(user.KfId, $"failed to fetch stox prices.");
            return;
        }
        var json = await response.Content.ReadAsStringAsync(ctx);
        var stoxData = JsonSerializer.Deserialize<StoxData>(json);

        var outputLines = new List<string>();

        if (portfolio.Count > 0)
        {
            outputLines.Add("Longs:");
            var longs = portfolio
                .OrderBy(kvp => kvp.Key)
                .Select(kvp =>
                {
                    var price = stoxData?.Stocks.FirstOrDefault(s =>
                        s.Symbol.Equals(kvp.Key, StringComparison.OrdinalIgnoreCase))?.CurrentPrice;
                    var valueStr = price.HasValue
                        ? $" (value: ₣{price.Value * kvp.Value:0.##}[plain])[/plain]"
                        : string.Empty;
                    return $"{kvp.Key}: {kvp.Value:0.####}x{valueStr}";
                });
            outputLines.AddRange(string.Join(", ", longs));
        }

        if (shorts.Count > 0)
        {
            outputLines.Add("Shorts:");
            var shortStr = shorts
                .OrderBy(kvp => kvp.Key)
                .Select(kvp =>
                {
                    var pos = kvp.Value;
                    var currentPrice = stoxData?.Stocks.FirstOrDefault(s =>
                        s.Symbol.Equals(kvp.Key, StringComparison.OrdinalIgnoreCase))?.CurrentPrice;
                    if (!currentPrice.HasValue)
                        return $"  {kvp.Key}: {pos.Quantity:0.####}x short (entry: ₣{pos.EntryPrice:0.##}[plain])[/plain]";
                    var pnl = pos.Quantity * (pos.EntryPrice - currentPrice.Value);
                    var pnlStr = pnl >= 0
                        ? $"[COLOR=#00ff00]+₣{pnl:0.##}[/COLOR]"
                        : $"[COLOR=#ff0000]₣{pnl:0.##}[/COLOR]";
                    return $"  {kvp.Key}: {pos.Quantity:0.####}x short (entry: ₣{pos.EntryPrice:0.##}, current: ₣{currentPrice.Value}, P&L: {pnlStr}[plain])[/plain]";
                });
            outputLines.AddRange(string.Join(", ", shortStr));
        }

        if (leveraged.Count > 0)
        {
            outputLines.Add("Leveraged Longs:");
            var leveragedStr = leveraged
                .OrderBy(kvp => kvp.Key)
                .Select(kvp =>
                {
                    var pos = kvp.Value;
                    var currentPrice = stoxData?.Stocks.FirstOrDefault(s =>
                        s.Symbol.Equals(kvp.Key, StringComparison.OrdinalIgnoreCase))?.CurrentPrice;
                    var fullEntry = pos.EntryPrice * pos.Quantity;
                    var effectiveLeverage = pos.Borrowed > 0m && fullEntry > pos.Borrowed
                        ? fullEntry / (fullEntry - pos.Borrowed)
                        : 1m;
                    if (!currentPrice.HasValue)
                        return $"  {kvp.Key}: {pos.Quantity:0.####}x leveraged ({effectiveLeverage:0.##}x, entry: ₣{pos.EntryPrice:0.##}[plain])[/plain]";
                    var pnl = (decimal)(currentPrice.Value - pos.EntryPrice) * pos.Quantity;
                    var pnlStr = pnl >= 0
                        ? $"[COLOR=#00ff00]+₣{pnl:0.##}[/COLOR]"
                        : $"[COLOR=#ff0000]₣{pnl:0.##}[/COLOR]";
                    return $"  {kvp.Key}: {pos.Quantity:0.####}x leveraged ({effectiveLeverage:0.##}x, entry: ₣{pos.EntryPrice:0.##}, current: ₣{currentPrice.Value}, P&L: {pnlStr}[plain])[/plain]";
                });
            outputLines.Add(string.Join(", ", leveragedStr));
        }

        await botInstance.SendChatMessageAsync(
            $"{user.FormatUsername()}'s stox portfolio:\n{string.Join("\n", outputLines)}",
            true, whisperTo: user.KfId, autoDeleteAfter: TimeSpan.FromSeconds(20));
    }
}

public class StoxShortCommand : ICommand
{
    public List<Regex> Patterns => [
        new Regex(@"^stox short (?<symbol>\w+) (?<amount>\d+(?:\.\d+)?)$", RegexOptions.IgnoreCase)
    ];
    public string? HelpText => "Short sell stocks (profit if price drops): !stox short <symbol> <amount>";
    public UserRight RequiredRight => UserRight.Loser;
    public TimeSpan Timeout => TimeSpan.FromSeconds(15);
    public RateLimitOptionsModel? RateLimitOptions => new()
    {
        MaxInvocations = 2,
        Window = TimeSpan.FromSeconds(60),
        Flags = RateLimitFlags.NoResponse
    };

    public bool WhisperCanInvoke => true;

    public async Task RunCommand(ChatBot botInstance, BotCommandMessageModel message, UserDbModel user, GroupCollection arguments,
        CancellationToken ctx)
    {
        if (!await StoxMarket.IsOpenAsync())
        {
            await botInstance.SendWhisperAsync(user.KfId, $"the stox market is currently closed.");
            return;
        }

        var symbol = arguments["symbol"].Value.ToUpper();
        var amount = decimal.Parse(arguments["amount"].Value, CultureInfo.InvariantCulture);

        if (symbol.Contains("?"))
        {
            await botInstance.SendWhisperAsync(user.KfId, $"can't short unrevealed fish.");
            return;
        }

        if (amount <= 0)
        {
            await botInstance.SendWhisperAsync(user.KfId, $"amount must be greater than 0.");
            return;
        }

        const string endpoint = "https://api.fishtank.live/v1/stocks";
        using var httpClient = new HttpClient();
        var response = await httpClient.GetAsync(endpoint, ctx);
        if (!response.IsSuccessStatusCode)
        {
            await botInstance.SendWhisperAsync(user.KfId, $"failed to fetch stox prices.");
            return;
        }
        var json = await response.Content.ReadAsStringAsync(ctx);
        var stoxData = JsonSerializer.Deserialize<StoxData>(json);
        var stock = stoxData?.Stocks.FirstOrDefault(s =>
            s.Symbol.Equals(symbol, StringComparison.OrdinalIgnoreCase));

        if (stock == null)
        {
            var available = stoxData?.Stocks.Select(s => s.Symbol) ?? new List<string>();
            await botInstance.SendWhisperAsync(user.KfId, $"unknown symbol \"{symbol}\". Available: {string.Join(", ", available)}");
            return;
        }

        var collateral = (decimal)stock.CurrentPrice * amount;
        var gambler = await Money.GetGamblerEntityAsync(user.Id, ct: ctx);
        if (gambler == null)
            throw new InvalidOperationException($"Caught a null when retrieving gambler for {user.KfUsername}");

        if (gambler.Balance < collateral)
        {
            await botInstance.SendWhisperAsync(user.KfId,
                $"you need {await collateral.FormatKasinoCurrencyAsync()} collateral to short {amount:0.####}x {symbol} at ₣{stock.CurrentPrice} each, but only have {await gambler.Balance.FormatKasinoCurrencyAsync()}.");
            return;
        }

        var connectionString = await SettingsProvider.GetValueAsync(BuiltIn.Keys.BotRedisConnectionString);
        if (connectionString.Value == null)
        {
            await botInstance.SendWhisperAsync(user.KfId,
                $"stox trading is currently unavailable (Redis not configured).");
            return;
        }
        using var redis = await ConnectionMultiplexer.ConnectAsync(connectionString.Value);
        var db = redis.GetDatabase();

        var shortsKey = $"Stox.Shorts.{gambler.Id}";
        var shortsJson = await db.StringGetAsync(shortsKey);
        var shorts = shortsJson.HasValue
            ? JsonSerializer.Deserialize<Dictionary<string, StoxShortPosition>>(shortsJson.ToString()) ?? new Dictionary<string, StoxShortPosition>()
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
            $"Opened short {amount:0.####}x {symbol} @ ₣{stock.CurrentPrice}", ct: ctx);

        var pos = shorts[symbol];
        await botInstance.SendChatMessageAsync(
            $"{user.FormatUsername()}, you opened short of {amount:0.####}x {symbol} @ ₣{stock.CurrentPrice}. Collateral locked: {await collateral.FormatKasinoCurrencyAsync()}. New balance: {await newBalance.FormatKasinoCurrencyAsync()}. Total short: {pos.Quantity:0.####}x {symbol} (avg entry: ₣{pos.EntryPrice:0.##}[plain])[/plain].",
            true, whisperTo: user.KfId, autoDeleteAfter: TimeSpan.FromSeconds(20));
    }
}

public class StoxCoverCommand : ICommand
{
    public List<Regex> Patterns => [
        new Regex(@"^stox cover (?<symbol>\w+) (?<amount>\d+(?:\.\d+)?)$", RegexOptions.IgnoreCase)
    ];
    public string? HelpText => "Cover (close) a short position: !stox cover <symbol> <amount>";
    public UserRight RequiredRight => UserRight.Loser;
    public TimeSpan Timeout => TimeSpan.FromSeconds(15);
    public RateLimitOptionsModel? RateLimitOptions => new()
    {
        MaxInvocations = 2,
        Window = TimeSpan.FromSeconds(60),
        Flags = RateLimitFlags.NoResponse
    };

    public bool WhisperCanInvoke => true;

    public async Task RunCommand(ChatBot botInstance, BotCommandMessageModel message, UserDbModel user, GroupCollection arguments,
        CancellationToken ctx)
    {
        if (!await StoxMarket.IsOpenAsync())
        {
            await botInstance.SendWhisperAsync(user.KfId, $"the stox market is currently closed.");
            return;
        }

        var symbol = arguments["symbol"].Value.ToUpper();
        var amount = decimal.Parse(arguments["amount"].Value, CultureInfo.InvariantCulture);

        if (amount <= 0)
        {
            await botInstance.SendWhisperAsync(user.KfId, $"amount must be greater than 0.");
            return;
        }

        var gambler = await Money.GetGamblerEntityAsync(user.Id, ct: ctx);
        if (gambler == null)
            throw new InvalidOperationException($"Caught a null when retrieving gambler for {user.KfUsername}");

        var connectionString = await SettingsProvider.GetValueAsync(BuiltIn.Keys.BotRedisConnectionString);
        if (connectionString.Value == null)
        {
            await botInstance.SendWhisperAsync(user.KfId, $"stox trading is currently unavailable (Redis not configured).");
            return;
        }
        using var redis = await ConnectionMultiplexer.ConnectAsync(connectionString.Value);
        var db = redis.GetDatabase();

        var shortsKey = $"Stox.Shorts.{gambler.Id}";
        var shortsJson = await db.StringGetAsync(shortsKey);
        var shorts = shortsJson.HasValue
            ? JsonSerializer.Deserialize<Dictionary<string, StoxShortPosition>>(shortsJson.ToString()) ?? new Dictionary<string, StoxShortPosition>()
            : new Dictionary<string, StoxShortPosition>();

        if (!shorts.TryGetValue(symbol, out var position) || position.Quantity < amount)
        {
            var held = shorts.TryGetValue(symbol, out var p) ? p.Quantity : 0m;
            await botInstance.SendWhisperAsync(user.KfId,
                $"you only have {held:0.####}x {symbol} shorted and can't cover {amount:0.####}x.");
            return;
        }

        const string endpoint = "https://api.fishtank.live/v1/stocks";
        using var httpClient = new HttpClient();
        var response = await httpClient.GetAsync(endpoint, ctx);
        if (!response.IsSuccessStatusCode)
        {
            await botInstance.SendWhisperAsync(user.KfId, $"failed to fetch stox prices.");
            return;
        }
        var json = await response.Content.ReadAsStringAsync(ctx);
        var stoxData = JsonSerializer.Deserialize<StoxData>(json);
        var stock = stoxData?.Stocks.FirstOrDefault(s =>
            s.Symbol.Equals(symbol, StringComparison.OrdinalIgnoreCase));

        if (stock == null)
        {
            await botInstance.SendWhisperAsync(user.KfId, $"unknown symbol \"{symbol}\".");
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
            $"Covered {amount:0.####}x {symbol} short @ entry ₣{entryPrice:0.##}, close ₣{stock.CurrentPrice}", ct: ctx);

        var pnlStr = pnl >= 0
            ? $"[B][COLOR=#00ff00]+{await pnl.FormatKasinoCurrencyAsync()}[/COLOR][/B]"
            : $"[B][COLOR=#ff0000]{await pnl.FormatKasinoCurrencyAsync()}[/COLOR][/B]";
        var remaining = shorts.TryGetValue(symbol, out var rem)
            ? $". You still short {rem.Quantity:0.####}x {symbol}."
            : $". No remaining short position in {symbol}.";
        await botInstance.SendChatMessageAsync(
            $"{user.FormatUsername()}, you covered {amount:0.####}x {symbol} short. Entry: ₣{entryPrice:0.##}, close: ₣{stock.CurrentPrice}. P&L: {pnlStr}. New balance: {await newBalance.FormatKasinoCurrencyAsync()}{remaining}",
            true, whisperTo: user.KfId, autoDeleteAfter: TimeSpan.FromSeconds(20));
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
    public bool WhisperCanInvoke => true;

    public async Task RunCommand(ChatBot botInstance, BotCommandMessageModel message, UserDbModel user, GroupCollection arguments,
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
    public bool WhisperCanInvoke => true;

    public async Task RunCommand(ChatBot botInstance, BotCommandMessageModel message, UserDbModel user, GroupCollection arguments,
        CancellationToken ctx)
    {
        await SettingsProvider.SetValueAsBooleanAsync(BuiltIn.Keys.StoxMarketOpen, false);
        await botInstance.SendChatMessageAsync("Stox market is now [B][COLOR=#ff0000]CLOSED[/COLOR][/B]. Trading suspended.",
            true, autoDeleteAfter: TimeSpan.FromSeconds(30));
    }
}

public class StoxRenameSymbolCommand : ICommand
{
    public List<Regex> Patterns => [
        new Regex(@"^stox rename (?<oldSymbol>\w+) (?<newSymbol>\w+)$", RegexOptions.IgnoreCase)
    ];
    public string? HelpText => null;
    public UserRight RequiredRight => UserRight.TrueAndHonest;
    public TimeSpan Timeout => TimeSpan.FromSeconds(30);
    public RateLimitOptionsModel? RateLimitOptions => null;
    public bool WhisperCanInvoke => true;

    public async Task RunCommand(ChatBot botInstance, BotCommandMessageModel message, UserDbModel user, GroupCollection arguments,
        CancellationToken ctx)
    {
        var oldSymbol = arguments["oldSymbol"].Value.ToUpper();
        var newSymbol = arguments["newSymbol"].Value.ToUpper();

        if (oldSymbol == newSymbol)
        {
            await botInstance.SendChatMessageAsync($"old and new symbols are the same.",
                true, autoDeleteAfter: TimeSpan.FromSeconds(10));
            return;
        }

        var connectionString = await SettingsProvider.GetValueAsync(BuiltIn.Keys.BotRedisConnectionString);
        if (connectionString.Value == null)
        {
            await botInstance.SendChatMessageAsync(
                $"stox trading is currently unavailable (Redis not configured).",
                true, autoDeleteAfter: TimeSpan.FromSeconds(10));
            return;
        }

        using var redis = await ConnectionMultiplexer.ConnectAsync(connectionString.Value);
        var db = redis.GetDatabase();
        var server = redis.GetServer(redis.GetEndPoints().First());

        int portfoliosUpdated = 0;
        int shortsUpdated = 0;

        // Migrate long positions
        await foreach (var key in server.KeysAsync(pattern: "Stox.Portfolio.*"))
        {
            var json = await db.StringGetAsync(key);
            if (!json.HasValue) continue;

            var portfolio = JsonSerializer.Deserialize<Dictionary<string, decimal>>(json.ToString()) ?? [];
            if (!portfolio.TryGetValue(oldSymbol, out var qty)) continue;

            portfolio.Remove(oldSymbol);
            portfolio[newSymbol] = portfolio.GetValueOrDefault(newSymbol, 0m) + qty;
            await db.StringSetAsync(key, JsonSerializer.Serialize(portfolio));
            portfoliosUpdated++;
        }

        // Migrate short positions
        await foreach (var key in server.KeysAsync(pattern: "Stox.Shorts.*"))
        {
            var json = await db.StringGetAsync(key);
            if (!json.HasValue) continue;

            var shorts = JsonSerializer.Deserialize<Dictionary<string, StoxShortPosition>>(json.ToString()) ?? [];
            if (!shorts.TryGetValue(oldSymbol, out var pos)) continue;

            shorts.Remove(oldSymbol);
            if (shorts.TryGetValue(newSymbol, out var existing))
            {
                // Weighted average entry price
                var totalQty = existing.Quantity + pos.Quantity;
                var avgEntry = (existing.Quantity * existing.EntryPrice + pos.Quantity * pos.EntryPrice) / totalQty;
                shorts[newSymbol] = new StoxShortPosition { Quantity = totalQty, EntryPrice = avgEntry };
            }
            else
            {
                shorts[newSymbol] = pos;
            }
            await db.StringSetAsync(key, JsonSerializer.Serialize(shorts));
            shortsUpdated++;
        }

        // Migrate leveraged long positions
        int leveragedUpdated = 0;
        await foreach (var key in server.KeysAsync(pattern: "Stox.Leveraged.*"))
        {
            var json = await db.StringGetAsync(key);
            if (!json.HasValue) continue;

            var leveraged = JsonSerializer.Deserialize<Dictionary<string, StoxLeveragedPosition>>(json.ToString()) ?? [];
            if (!leveraged.TryGetValue(oldSymbol, out var pos)) continue;

            leveraged.Remove(oldSymbol);
            if (leveraged.TryGetValue(newSymbol, out var existing))
            {
                var totalQty = existing.Quantity + pos.Quantity;
                var avgEntry = (existing.Quantity * existing.EntryPrice + pos.Quantity * pos.EntryPrice) / totalQty;
                leveraged[newSymbol] = new StoxLeveragedPosition
                {
                    Quantity = totalQty,
                    EntryPrice = avgEntry,
                    Borrowed = existing.Borrowed + pos.Borrowed
                };
            }
            else
            {
                leveraged[newSymbol] = pos;
            }
            await db.StringSetAsync(key, JsonSerializer.Serialize(leveraged));
            leveragedUpdated++;
        }

        await botInstance.SendChatMessageAsync(
            $"renamed stox symbol {oldSymbol} → {newSymbol}. Updated {portfoliosUpdated} long portfolio(s), {shortsUpdated} short position(s), and {leveragedUpdated} leveraged position(s).",
            true, autoDeleteAfter: TimeSpan.FromSeconds(30));
    }
}
