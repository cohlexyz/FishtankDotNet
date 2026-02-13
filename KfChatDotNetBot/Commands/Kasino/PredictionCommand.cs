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

namespace KfChatDotNetBot.Commands.Kasino;

/// <summary>
/// Command to start a new prediction
/// </summary>
[KasinoCommand]
public class PredictionStartCommand : ICommand
{
    public List<Regex> Patterns =>
    [
        new Regex(@"^prediction start (.+)$", RegexOptions.IgnoreCase),
        new Regex(@"^pred start (.+)$", RegexOptions.IgnoreCase)
    ];

    public string? HelpText => "!prediction start \"description\" \"option1\" \"option2\" [\"option3\"] ... - Start a new prediction";
    public UserRight RequiredRight => UserRight.TrueAndHonest;
    public TimeSpan Timeout => TimeSpan.FromSeconds(10);
    public RateLimitOptionsModel? RateLimitOptions => null;

    private static readonly Logger _logger = LogManager.GetCurrentClassLogger();

    public async Task RunCommand(ChatBot botInstance, MessageModel message, UserDbModel user, GroupCollection arguments,
        CancellationToken ctx)
    {
        var settings = await SettingsProvider.GetMultipleValuesAsync([
            BuiltIn.Keys.BotRedisConnectionString
        ]);

        if (string.IsNullOrEmpty(settings[BuiltIn.Keys.BotRedisConnectionString].Value))
        {
            await botInstance.SendChatMessageAsync($"{user.FormatUsername()}, predictions are not available at this time", true,
                autoDeleteAfter: TimeSpan.FromSeconds(15));
            return;
        }

        var redis = await ConnectionMultiplexer.ConnectAsync(settings[BuiltIn.Keys.BotRedisConnectionString].Value!);
        var redisDb = redis.GetDatabase();

        // Check if there's already an active prediction
        var activePredictions = await redisDb.SetMembersAsync("predictions:active");
        if (activePredictions.Length > 0)
        {
            await botInstance.SendChatMessageAsync(
                $"{user.FormatUsername()}, there is already an active prediction. End it first with !prediction end",
                true, autoDeleteAfter: TimeSpan.FromSeconds(15));
            return;
        }

        // Parse quoted strings from the message content
        var messageText = message.Message;
        var startMatch = Regex.Match(messageText, @"^(?:prediction|pred)\s+start\s+(.+)$", RegexOptions.IgnoreCase);
        if (!startMatch.Success)
        {
            await botInstance.SendChatMessageAsync(
                $"{user.FormatUsername()}, invalid syntax. Use: !prediction start \"description\" \"option1\" \"option2\" [\"option3\" ...]",
                true, autoDeleteAfter: TimeSpan.FromSeconds(15));
            return;
        }

        var argsText = startMatch.Groups[1].Value;
        var quotedStrings = new List<string>();
        var regex = new Regex(@"""([^""]*)""");
        var matches = regex.Matches(argsText);

        foreach (Match match in matches)
        {
            if (match.Groups.Count > 1)
            {
                quotedStrings.Add(match.Groups[1].Value);
            }
        }

        if (quotedStrings.Count < 3)
        {
            await botInstance.SendChatMessageAsync(
                $"{user.FormatUsername()}, you need a description and at least 2 options. Use: !prediction start \"description\" \"option1\" \"option2\" [\"option3\" ...]",
                true, autoDeleteAfter: TimeSpan.FromSeconds(15));
            return;
        }

        var description = quotedStrings[0];
        var options = quotedStrings.Skip(1).ToList();

        if (options.Count < 2)
        {
            await botInstance.SendChatMessageAsync(
                $"{user.FormatUsername()}, you need at least 2 options for a prediction",
                true, autoDeleteAfter: TimeSpan.FromSeconds(15));
            return;
        }

        // Generate unique ID for the prediction
        var predictionId = Guid.NewGuid().ToString("N")[..8];

        // Create prediction object
        var prediction = new PredictionData
        {
            Id = predictionId,
            Description = description,
            Options = options.Select((opt, idx) => new PredictionOption
            {
                Index = idx + 1,
                Text = opt,
                TotalBet = 0
            }).ToList(),
            CreatedBy = user.Id,
            CreatedAt = DateTimeOffset.UtcNow,
            State = PredictionState.Active
        };

        // Store in Redis
        await redisDb.StringSetAsync($"prediction:{predictionId}", JsonSerializer.Serialize(prediction));
        await redisDb.SetAddAsync("predictions:active", predictionId);

        // Build response message
        var optionsText = string.Join("[br]", prediction.Options.Select(o => $"  {o.Index}. {o.Text}"));
        await botInstance.SendChatMessageAsync(
            $":!: NEW PREDICTION :!:[br]{description}[br][br]Options:[br]{optionsText}[br][br]Use !bet {predictionId} <amount> <option> to place your bet!",
            true);
    }
}

/// <summary>
/// Command to place a bet on a prediction
/// </summary>
[KasinoCommand]
[WagerCommand]
public class PredictionBetCommand : ICommand
{
    public List<Regex> Patterns =>
    [
        new Regex(@"^bet (?<predictionId>\w+) (?<amount>\d+(?:\.\d+)?) (?<option>\d+)$", RegexOptions.IgnoreCase),
        new Regex(@"^bet (?<amount>\d+(?:\.\d+)?) (?<option>\d+)$", RegexOptions.IgnoreCase)
    ];

    public string? HelpText => "!bet <prediction_id> <amount> <option> - Bet on a prediction outcome";
    public UserRight RequiredRight => UserRight.Loser;
    public TimeSpan Timeout => TimeSpan.FromSeconds(5);
    public RateLimitOptionsModel? RateLimitOptions => new()
    {
        MaxInvocations = 10,
        Window = TimeSpan.FromSeconds(30)
    };

    private static readonly Logger _logger = LogManager.GetCurrentClassLogger();

    public async Task RunCommand(ChatBot botInstance, MessageModel message, UserDbModel user, GroupCollection arguments,
        CancellationToken ctx)
    {
        var settings = await SettingsProvider.GetMultipleValuesAsync([
            BuiltIn.Keys.BotRedisConnectionString
        ]);

        if (string.IsNullOrEmpty(settings[BuiltIn.Keys.BotRedisConnectionString].Value))
        {
            await botInstance.SendChatMessageAsync($"{user.FormatUsername()}, predictions are not available at this time", true,
                autoDeleteAfter: TimeSpan.FromSeconds(15));
            return;
        }

        var redis = await ConnectionMultiplexer.ConnectAsync(settings[BuiltIn.Keys.BotRedisConnectionString].Value!);
        var redisDb = redis.GetDatabase();

        // Get prediction ID - either from argument or from active predictions
        string? predictionId = null;
        if (arguments.TryGetValue("predictionId", out var predIdGroup))
        {
            predictionId = predIdGroup.Value;
        }
        else
        {
            // If no prediction ID provided, use the only active prediction
            var activePredictions = await redisDb.SetMembersAsync("predictions:active");
            if (activePredictions.Length == 0)
            {
                await botInstance.SendChatMessageAsync(
                    $"{user.FormatUsername()}, there are no active predictions",
                    true, autoDeleteAfter: TimeSpan.FromSeconds(10));
                return;
            }
            predictionId = activePredictions[0].ToString();
        }

        // Load prediction
        var predictionJson = await redisDb.StringGetAsync($"prediction:{predictionId}");
        if (predictionJson.IsNullOrEmpty)
        {
            await botInstance.SendChatMessageAsync(
                $"{user.FormatUsername()}, prediction not found",
                true, autoDeleteAfter: TimeSpan.FromSeconds(10));
            return;
        }

        var prediction = JsonSerializer.Deserialize<PredictionData>(predictionJson!.ToString());
        if (prediction == null || prediction.State != PredictionState.Active)
        {
            await botInstance.SendChatMessageAsync(
                $"{user.FormatUsername()}, this prediction is not active",
                true, autoDeleteAfter: TimeSpan.FromSeconds(10));
            return;
        }

        var amount = Convert.ToDecimal(arguments["amount"].Value);
        var optionIndex = int.Parse(arguments["option"].Value);

        // Validate option
        var option = prediction.Options.FirstOrDefault(o => o.Index == optionIndex);
        if (option == null)
        {
            await botInstance.SendChatMessageAsync(
                $"{user.FormatUsername()}, invalid option. Choose from 1-{prediction.Options.Count}",
                true, autoDeleteAfter: TimeSpan.FromSeconds(10));
            return;
        }

        // Get gambler and check balance
        var gambler = await Money.GetGamblerEntityAsync(user.Id, ct: ctx);
        if (gambler == null)
        {
            throw new InvalidOperationException($"Caught a null when retrieving gambler for {user.KfUsername}");
        }

        if (gambler.Balance < amount)
        {
            await botInstance.SendChatMessageAsync(
                $"{user.FormatUsername()}, your balance of {await gambler.Balance.FormatKasinoCurrencyAsync()} isn't enough for this bet.",
                true, autoDeleteAfter: TimeSpan.FromSeconds(10));
            return;
        }

        if (amount <= 0)
        {
            await botInstance.SendChatMessageAsync(
                $"{user.FormatUsername()}, you must bet more than 0",
                true, autoDeleteAfter: TimeSpan.FromSeconds(10));
            return;
        }

        // Check if user already has a bet
        var existingBetJson = await redisDb.HashGetAsync($"prediction:{predictionId}:bets", user.Id.ToString());
        if (!existingBetJson.IsNullOrEmpty)
        {
            await botInstance.SendChatMessageAsync(
                $"{user.FormatUsername()}, you've already placed a bet on this prediction",
                true, autoDeleteAfter: TimeSpan.FromSeconds(10));
            return;
        }

        // Place the incomplete wager (deduct money but don't record win/loss yet)
        var newBalance = await Money.NewWagerAsync(gambler.Id, amount, -amount, WagerGame.Prediction,
            isComplete: false, ct: ctx);

        // Store bet in Redis
        var bet = new PredictionBet
        {
            UserId = user.Id,
            Username = user.KfUsername,
            Amount = amount,
            OptionIndex = optionIndex,
            PlacedAt = DateTimeOffset.UtcNow
        };

        await redisDb.HashSetAsync($"prediction:{predictionId}:bets", user.Id.ToString(), JsonSerializer.Serialize(bet));

        // Update option total
        option.TotalBet += amount;
        await redisDb.StringSetAsync($"prediction:{predictionId}", JsonSerializer.Serialize(prediction));

        await botInstance.SendChatMessageAsync(
            $"{user.FormatUsername()}, you bet {await amount.FormatKasinoCurrencyAsync()} on option {optionIndex} ({option.Text}). " +
            $"Your new balance is {await newBalance.FormatKasinoCurrencyAsync()}",
            true, autoDeleteAfter: TimeSpan.FromSeconds(15));
    }
}

/// <summary>
/// Command to end a prediction and pay out winners
/// </summary>
[KasinoCommand]
public class PredictionEndCommand : ICommand
{
    public List<Regex> Patterns =>
    [
        new Regex(@"^prediction end (?<predictionId>\w+) (?<winningOption>\d+)$", RegexOptions.IgnoreCase),
        new Regex(@"^pred end (?<predictionId>\w+) (?<winningOption>\d+)$", RegexOptions.IgnoreCase),
        new Regex(@"^prediction end (?<winningOption>\d+)$", RegexOptions.IgnoreCase),
        new Regex(@"^pred end (?<winningOption>\d+)$", RegexOptions.IgnoreCase)
    ];

    public string? HelpText => "!prediction end <prediction_id> <winning_option> - End a prediction and pay winners";
    public UserRight RequiredRight => UserRight.TrueAndHonest;
    public TimeSpan Timeout => TimeSpan.FromSeconds(10);
    public RateLimitOptionsModel? RateLimitOptions => null;

    private static readonly Logger _logger = LogManager.GetCurrentClassLogger();

    public async Task RunCommand(ChatBot botInstance, MessageModel message, UserDbModel user, GroupCollection arguments,
        CancellationToken ctx)
    {
        var settings = await SettingsProvider.GetMultipleValuesAsync([
            BuiltIn.Keys.BotRedisConnectionString
        ]);

        if (string.IsNullOrEmpty(settings[BuiltIn.Keys.BotRedisConnectionString].Value))
        {
            await botInstance.SendChatMessageAsync($"{user.FormatUsername()}, predictions are not available at this time", true,
                autoDeleteAfter: TimeSpan.FromSeconds(15));
            return;
        }

        var redis = await ConnectionMultiplexer.ConnectAsync(settings[BuiltIn.Keys.BotRedisConnectionString].Value!);
        var redisDb = redis.GetDatabase();

        // Get prediction ID
        string? predictionId = null;
        if (arguments.TryGetValue("predictionId", out var predIdGroup))
        {
            predictionId = predIdGroup.Value;
        }
        else
        {
            var activePredictions = await redisDb.SetMembersAsync("predictions:active");
            if (activePredictions.Length == 0)
            {
                await botInstance.SendChatMessageAsync(
                    $"{user.FormatUsername()}, there are no active predictions",
                    true, autoDeleteAfter: TimeSpan.FromSeconds(10));
                return;
            }
            predictionId = activePredictions[0].ToString();
        }

        // Load prediction
        var predictionJson = await redisDb.StringGetAsync($"prediction:{predictionId}");
        if (predictionJson.IsNullOrEmpty)
        {
            await botInstance.SendChatMessageAsync(
                $"{user.FormatUsername()}, prediction not found",
                true, autoDeleteAfter: TimeSpan.FromSeconds(10));
            return;
        }

        var prediction = JsonSerializer.Deserialize<PredictionData>(predictionJson!.ToString());
        if (prediction == null)
        {
            await botInstance.SendChatMessageAsync(
                $"{user.FormatUsername()}, prediction data is invalid",
                true, autoDeleteAfter: TimeSpan.FromSeconds(10));
            return;
        }

        if (prediction.State != PredictionState.Active)
        {
            await botInstance.SendChatMessageAsync(
                $"{user.FormatUsername()}, this prediction is not active",
                true, autoDeleteAfter: TimeSpan.FromSeconds(10));
            return;
        }

        var winningOptionIndex = int.Parse(arguments["winningOption"].Value);
        var winningOption = prediction.Options.FirstOrDefault(o => o.Index == winningOptionIndex);
        if (winningOption == null)
        {
            await botInstance.SendChatMessageAsync(
                $"{user.FormatUsername()}, invalid winning option. Choose from 1-{prediction.Options.Count}",
                true, autoDeleteAfter: TimeSpan.FromSeconds(10));
            return;
        }

        // Get all bets
        var allBets = await redisDb.HashGetAllAsync($"prediction:{predictionId}:bets");
        var bets = allBets.Select(entry =>
            JsonSerializer.Deserialize<PredictionBet>(entry.Value!.ToString())).ToList();

        // Calculate total pot and winning pot
        var totalPot = bets.Sum(b => b!.Amount);
        var winningPot = bets.Where(b => b!.OptionIndex == winningOptionIndex).Sum(b => b!.Amount);

        _logger.Info($"Prediction {predictionId} ending. Total pot: {totalPot}, Winning pot: {winningPot}");

        var winners = bets.Where(b => b!.OptionIndex == winningOptionIndex).ToList();
        var winnersList = new List<string>();

        // Pay out winners
        foreach (var bet in winners)
        {
            if (bet == null) continue;

            var winShare = totalPot > 0 && winningPot > 0 ? (bet.Amount / winningPot) * totalPot : bet.Amount;
            var profit = winShare - bet.Amount;

            _logger.Info($"User {bet.Username} won {winShare:N} (profit: {profit:N}) from bet of {bet.Amount:N}");

            // Update balance with the complete wager
            var newBalance = await Money.ModifyBalanceAsync(bet.UserId, winShare,
                TransactionSourceEventType.Gambling,
                $"Prediction win: {prediction.Description}", ct: ctx);

            winnersList.Add($"{bet.Username}: +{await profit.FormatKasinoCurrencyAsync()}");
        }

        // Mark prediction as complete
        prediction.State = PredictionState.Complete;
        prediction.WinningOptionIndex = winningOptionIndex;
        prediction.CompletedAt = DateTimeOffset.UtcNow;
        await redisDb.StringSetAsync($"prediction:{predictionId}", JsonSerializer.Serialize(prediction));
        await redisDb.SetRemoveAsync("predictions:active", predictionId);

        // Cleanup after 24 hours
        await redisDb.KeyExpireAsync($"prediction:{predictionId}", TimeSpan.FromHours(24));
        await redisDb.KeyExpireAsync($"prediction:{predictionId}:bets", TimeSpan.FromHours(24));

        var winnersText = winners.Count > 0
            ? $"[br][br]Winners:[br]{string.Join("[br]", winnersList)}"
            : "[br][br]No one bet on the winning option!";

        await botInstance.SendChatMessageAsync(
            $":!: PREDICTION ENDED :!:[br]{prediction.Description}[br]" +
            $"Winning option: {winningOption.Index}. {winningOption.Text}[br]" +
            $"Total pot: {await totalPot.FormatKasinoCurrencyAsync()}[br]" +
            $"Winners: {winners.Count}{winnersText}",
            true);
    }
}

/// <summary>
/// Command to check current prediction status
/// </summary>
[KasinoCommand]
public class PredictionStatusCommand : ICommand
{
    public List<Regex> Patterns =>
    [
        new Regex(@"^prediction status(?:\s+(?<predictionId>\w+))?$", RegexOptions.IgnoreCase),
        new Regex(@"^pred status(?:\s+(?<predictionId>\w+))?$", RegexOptions.IgnoreCase),
        new Regex(@"^prediction$", RegexOptions.IgnoreCase),
        new Regex(@"^pred$", RegexOptions.IgnoreCase)
    ];

    public string? HelpText => "!prediction status [prediction_id] - Check current prediction status";
    public UserRight RequiredRight => UserRight.Loser;
    public TimeSpan Timeout => TimeSpan.FromSeconds(5);
    public RateLimitOptionsModel? RateLimitOptions => null;

    private static readonly Logger _logger = LogManager.GetCurrentClassLogger();

    public async Task RunCommand(ChatBot botInstance, MessageModel message, UserDbModel user, GroupCollection arguments,
        CancellationToken ctx)
    {
        var settings = await SettingsProvider.GetMultipleValuesAsync([
            BuiltIn.Keys.BotRedisConnectionString
        ]);

        if (string.IsNullOrEmpty(settings[BuiltIn.Keys.BotRedisConnectionString].Value))
        {
            await botInstance.SendChatMessageAsync($"{user.FormatUsername()}, predictions are not available at this time", true,
                autoDeleteAfter: TimeSpan.FromSeconds(15));
            return;
        }

        var redis = await ConnectionMultiplexer.ConnectAsync(settings[BuiltIn.Keys.BotRedisConnectionString].Value!);
        var redisDb = redis.GetDatabase();

        // Get prediction ID
        string? predictionId = null;
        if (arguments.TryGetValue("predictionId", out var predIdGroup))
        {
            predictionId = predIdGroup.Value;
        }
        else
        {
            var activePredictions = await redisDb.SetMembersAsync("predictions:active");
            if (activePredictions.Length == 0)
            {
                await botInstance.SendChatMessageAsync(
                    $"{user.FormatUsername()}, there are no active predictions",
                    true, autoDeleteAfter: TimeSpan.FromSeconds(10));
                return;
            }
            predictionId = activePredictions[0].ToString();
        }

        // Load prediction
        var predictionJson = await redisDb.StringGetAsync($"prediction:{predictionId}");
        if (predictionJson.IsNullOrEmpty)
        {
            await botInstance.SendChatMessageAsync(
                $"{user.FormatUsername()}, prediction not found",
                true, autoDeleteAfter: TimeSpan.FromSeconds(10));
            return;
        }

        var prediction = JsonSerializer.Deserialize<PredictionData>(predictionJson!.ToString());
        if (prediction == null)
        {
            await botInstance.SendChatMessageAsync(
                $"{user.FormatUsername()}, prediction data is invalid",
                true, autoDeleteAfter: TimeSpan.FromSeconds(10));
            return;
        }

        // Get all bets and calculate totals
        var allBets = await redisDb.HashGetAllAsync($"prediction:{predictionId}:bets");
        var bets = allBets.Select(entry =>
            JsonSerializer.Deserialize<PredictionBet>(entry.Value!.ToString())).ToList();

        var totalPot = bets.Sum(b => b!.Amount);

        // Update option totals
        foreach (var option in prediction.Options)
        {
            option.TotalBet = bets.Where(b => b!.OptionIndex == option.Index).Sum(b => b!.Amount);
        }

        var optionsText = string.Join("[br]", prediction.Options.Select(o =>
            $"  {o.Index}. {o.Text}: {o.TotalBet.FormatKasinoCurrencyAsync().Result} " +
            $"({(totalPot > 0 ? (o.TotalBet / totalPot * 100).ToString("F1") : "0")}%)"));

        await botInstance.SendChatMessageAsync(
            $"{user.FormatUsername()}, Prediction [{predictionId}]: {prediction.Description}[br][br]" +
            $"Options:[br]{optionsText}[br][br]" +
            $"Total pot: {await totalPot.FormatKasinoCurrencyAsync()}[br]" +
            $"Total bets: {bets.Count}",
            true);
    }
}

/// <summary>
/// Command to cancel a prediction and refund all bets
/// </summary>
[KasinoCommand]
public class PredictionCancelCommand : ICommand
{
    public List<Regex> Patterns =>
    [
        new Regex(@"^prediction cancel(?:\s+(?<predictionId>\w+))?$", RegexOptions.IgnoreCase),
        new Regex(@"^pred cancel(?:\s+(?<predictionId>\w+))?$", RegexOptions.IgnoreCase)
    ];

    public string? HelpText => "!prediction cancel [prediction_id] - Cancel a prediction and refund all bets";
    public UserRight RequiredRight => UserRight.TrueAndHonest;
    public TimeSpan Timeout => TimeSpan.FromSeconds(10);
    public RateLimitOptionsModel? RateLimitOptions => null;

    private static readonly Logger _logger = LogManager.GetCurrentClassLogger();

    public async Task RunCommand(ChatBot botInstance, MessageModel message, UserDbModel user, GroupCollection arguments,
        CancellationToken ctx)
    {
        var settings = await SettingsProvider.GetMultipleValuesAsync([
            BuiltIn.Keys.BotRedisConnectionString
        ]);

        if (string.IsNullOrEmpty(settings[BuiltIn.Keys.BotRedisConnectionString].Value))
        {
            await botInstance.SendChatMessageAsync($"{user.FormatUsername()}, predictions are not available at this time", true,
                autoDeleteAfter: TimeSpan.FromSeconds(15));
            return;
        }

        var redis = await ConnectionMultiplexer.ConnectAsync(settings[BuiltIn.Keys.BotRedisConnectionString].Value!);
        var redisDb = redis.GetDatabase();

        // Get prediction ID
        string? predictionId = null;
        if (arguments.TryGetValue("predictionId", out var predIdGroup))
        {
            predictionId = predIdGroup.Value;
        }
        else
        {
            var activePredictions = await redisDb.SetMembersAsync("predictions:active");
            if (activePredictions.Length == 0)
            {
                await botInstance.SendChatMessageAsync(
                    $"{user.FormatUsername()}, there are no active predictions",
                    true, autoDeleteAfter: TimeSpan.FromSeconds(10));
                return;
            }
            predictionId = activePredictions[0].ToString();
        }

        // Load prediction
        var predictionJson = await redisDb.StringGetAsync($"prediction:{predictionId}");
        if (predictionJson.IsNullOrEmpty)
        {
            await botInstance.SendChatMessageAsync(
                $"{user.FormatUsername()}, prediction not found",
                true, autoDeleteAfter: TimeSpan.FromSeconds(10));
            return;
        }

        var prediction = JsonSerializer.Deserialize<PredictionData>(predictionJson!.ToString());
        if (prediction == null)
        {
            await botInstance.SendChatMessageAsync(
                $"{user.FormatUsername()}, prediction data is invalid",
                true, autoDeleteAfter: TimeSpan.FromSeconds(10));
            return;
        }

        if (prediction.State != PredictionState.Active)
        {
            await botInstance.SendChatMessageAsync(
                $"{user.FormatUsername()}, this prediction is not active",
                true, autoDeleteAfter: TimeSpan.FromSeconds(10));
            return;
        }

        // Get all bets
        var allBets = await redisDb.HashGetAllAsync($"prediction:{predictionId}:bets");
        var bets = allBets.Select(entry =>
            JsonSerializer.Deserialize<PredictionBet>(entry.Value!.ToString())).ToList();

        _logger.Info($"Cancelling prediction {predictionId} and refunding {bets.Count} bets");

        // Refund all bets
        foreach (var bet in bets)
        {
            if (bet == null) continue;

            await Money.ModifyBalanceAsync(bet.UserId, bet.Amount,
                TransactionSourceEventType.Gambling,
                $"Prediction cancelled: {prediction.Description}", ct: ctx);

            _logger.Info($"Refunded {bet.Amount:N} to user {bet.Username}");
        }

        // Mark prediction as cancelled
        prediction.State = PredictionState.Cancelled;
        prediction.CompletedAt = DateTimeOffset.UtcNow;
        await redisDb.StringSetAsync($"prediction:{predictionId}", JsonSerializer.Serialize(prediction));
        await redisDb.SetRemoveAsync("predictions:active", predictionId);

        // Cleanup after 1 hour
        await redisDb.KeyExpireAsync($"prediction:{predictionId}", TimeSpan.FromHours(1));
        await redisDb.KeyExpireAsync($"prediction:{predictionId}:bets", TimeSpan.FromHours(1));

        await botInstance.SendChatMessageAsync(
            $"{user.FormatUsername()}, prediction cancelled. {bets.Count} bets have been refunded.",
            true);
    }
}

// Data models for predictions

public enum PredictionState
{
    Active,
    Complete,
    Cancelled
}

public class PredictionData
{
    public string Id { get; set; } = string.Empty;
    public string Description { get; set; } = string.Empty;
    public List<PredictionOption> Options { get; set; } = new();
    public int CreatedBy { get; set; }
    public DateTimeOffset CreatedAt { get; set; }
    public DateTimeOffset? CompletedAt { get; set; }
    public PredictionState State { get; set; }
    public int? WinningOptionIndex { get; set; }
}

public class PredictionOption
{
    public int Index { get; set; }
    public string Text { get; set; } = string.Empty;
    public decimal TotalBet { get; set; }
}

public class PredictionBet
{
    public int UserId { get; set; }
    public string Username { get; set; } = string.Empty;
    public decimal Amount { get; set; }
    public int OptionIndex { get; set; }
    public DateTimeOffset PlacedAt { get; set; }
}
