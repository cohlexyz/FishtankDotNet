using System.Globalization;
using System.Text.RegularExpressions;
using KfChatDotNetBot.Extensions;
using KfChatDotNetBot.Models;
using KfChatDotNetBot.Models.DbModels;
using KfChatDotNetBot.Services;
using KfChatDotNetBot.Settings;
using Microsoft.EntityFrameworkCore;
using NLog;
using RandN;
using RandN.Compat;

namespace KfChatDotNetBot.Commands.Kasino;

// The avelloon insists every great rap battle needs a mediator skimming off the top.
// This is that mediator. It is the Kasino, and it is nefarious.
//
// Persistence model: all battle state lives in Redis so the bot can be restarted at any time without
// losing an in-flight battle. Timeouts are durable too - each battle carries a deadline and a background
// sweeper (BotServices) processes anything that has expired, instead of a fragile in-memory timer.
// The one thing that stays in memory is the Gate: it only serialises concurrent operations *within* the
// running process (there's a single bot instance), and on restart there is nothing in flight to serialise.
[KasinoCommand]
[WagerCommand]
public class RapBattleCommand : ICommand
{
    // One broad pattern; the meaning of the trailing text depends entirely on game state,
    // so all routing (challenge / accept / decline / verse) is decided inside RunCommand.
    public List<Regex> Patterns =>
    [
        new Regex(@"^rapbattle(?:\s+(?<args>.+))?\s*$", RegexOptions.IgnoreCase | RegexOptions.Singleline)
    ];

    public string? HelpText =>
        "!rapbattle <bet> <user id> to challenge someone, !rapbattle accept|decline to respond, !rapbattle <your bars> to spit your verse";
    public UserRight RequiredRight => UserRight.Loser;
    // Generous so the fake "evaluating…" suspense delay always fits inside one command invocation.
    public TimeSpan Timeout => TimeSpan.FromSeconds(60);
    public RateLimitOptionsModel? RateLimitOptions => new()
    {
        MaxInvocations = 5,
        Window = TimeSpan.FromSeconds(20)
    };
    public bool WhisperCanInvoke => false;

    private static readonly Logger Logger = LogManager.GetCurrentClassLogger();

    // In-process mutual exclusion for all state transitions (and the money moves that go with them) so a
    // user can't double-accept, double-submit, or have the sweeper race a resolution. State itself is in
    // Redis; this lock does NOT need to survive a restart because there are no in-flight ops after one.
    private static readonly SemaphoreSlim Gate = new(1, 1);

    // FD is a big Aussie Rigger
    private const string StateKeyPrefix = "rapbattle:state:";  // -> RapBattleState JSON
    private const string UserKeyPrefix = "rapbattle:user:";    // kfId -> battle id (both participants)
    private const string ActiveKey = "rapbattle:active";       // JSON list of in-progress battle ids
    // Anti-leak backstop only. Battles normally resolve within seconds of their (<=90s) deadline via the
    // sweeper, long before this; it just stops keys lingering forever if something goes badly wrong.
    private static readonly TimeSpan KeyTtl = TimeSpan.FromHours(24);

    private static string StateKey(string id) => $"{StateKeyPrefix}{id}";
    private static string UserKey(int kfId) => $"{UserKeyPrefix}{kfId}";

    private enum RapBattlePhase
    {
        PendingAccept,
        CollectingVerses
    }

    // Plain, JSON-serialisable DTO (System.Text.Json round-trips it through Redis). No behaviour, no
    // in-memory-only references (e.g. message trackers) - only the announcement's UUID string is kept.
    private sealed class RapBattleState
    {
        public string Id { get; set; } = string.Empty;
        public RapBattlePhase Phase { get; set; }
        // Unix ms after which the current phase has expired. The sweeper refunds anything past this.
        public long DeadlineUnixMs { get; set; }
        public decimal Bet { get; set; }

        public int ChallengerKfId { get; set; }
        public int ChallengerUserDbId { get; set; }
        public int ChallengerGamblerId { get; set; }
        public string ChallengerUsername { get; set; } = string.Empty;
        public string? ChallengerVerse { get; set; }
        public bool ChallengerUsedCheat { get; set; }

        public int OpponentKfId { get; set; }
        public int OpponentUserDbId { get; set; }
        public int OpponentGamblerId { get; set; }
        public string OpponentUsername { get; set; } = string.Empty;
        public string? OpponentVerse { get; set; }
        public bool OpponentUsedCheat { get; set; }

        // Chat message UUIDs so we can tidy up the challenge clutter once the battle is accepted.
        public string? ChallengeCommandMessageUuid { get; set; } // challenger's "!rapbattle <bet> <id>"
        public string? ChallengeAnnouncementUuid { get; set; }   // the bot's "you've been challenged" post
    }

    private sealed record RapBattleConfig(
        int TimeoutSeconds,
        decimal CasinoCutPercent,
        decimal RefundFeePercent,
        decimal MinimumBet,
        int MinimumVerseLength,
        int MinimumBars,
        int EvaluatingDelayMs,
        TimeSpan CleanupDelay,
        string FeedbackFile,
        string ChallengeFile,
        bool CheatEnabled,
        string GreenColor,
        string RedColor);

    // Secret cheat code. If a rapper drops this exact phrase anywhere in their verse, they auto-win —
    // unless BOTH rappers do it in the same battle, in which case the cheat is burned server-side forever
    // and the house keeps both buy-ins. Stored lowercased + single-spaced to match the normalised verse.
    private const string CheatPhrase =
        "like an avelloon on the moon, you will never make me swoon. you a troon!";

    public async Task RunCommand(ChatBot botInstance, BotCommandMessageModel message, UserDbModel user,
        GroupCollection arguments, CancellationToken ctx)
    {
        var settings = await SettingsProvider.GetMultipleValuesAsync([
            BuiltIn.Keys.KasinoRapBattleEnabled, BuiltIn.Keys.KasinoGameDisabledMessageCleanupDelay,
            BuiltIn.Keys.KiwiFarmsUsername
        ]);
        var disabledDelay =
            TimeSpan.FromMilliseconds(settings[BuiltIn.Keys.KasinoGameDisabledMessageCleanupDelay].ToType<int>());

        if (!settings[BuiltIn.Keys.KasinoRapBattleEnabled].ToBoolean())
        {
            await botInstance.SendChatMessageAsync($"{user.FormatUsername()}, rap battles are currently disabled.", true,
                autoDeleteAfter: disabledDelay);
            return;
        }

        // Rap battles are Redis-backed so they survive restarts. If Redis isn't configured/reachable, the
        // game can't run - fail clearly instead of throwing deep inside a handler.
        if (!RedisReady())
        {
            await botInstance.SendChatMessageAsync(
                $"{user.FormatUsername()}, rap battles are unavailable right now (the Kasino's Redis backend isn't configured).",
                true, autoDeleteAfter: disabledDelay);
            return;
        }

        var cfg = await LoadConfigAsync();
        var argsRaw = arguments["args"].Success ? arguments["args"].Value.Trim() : string.Empty;
        var myKfId = message.Author.Id;

        // Explicit help works in any state and doesn't touch the game, so handle it before the gate.
        if (argsRaw.Equals("help", StringComparison.OrdinalIgnoreCase))
        {
            await botInstance.SendChatMessageAsync(BuildHelp(cfg), true, autoDeleteAfter: cfg.CleanupDelay);
            RateLimitService.RemoveMostRecentEntry(user, this);
            return;
        }

        await Gate.WaitAsync(ctx);
        try
        {
            var battle = await FindBattleForUserAsync(myKfId);
            if (battle != null)
            {
                if (battle.Phase == RapBattlePhase.PendingAccept)
                {
                    if (battle.OpponentKfId == myKfId)
                    {
                        if (argsRaw.Equals("accept", StringComparison.OrdinalIgnoreCase))
                        {
                            await HandleAcceptAsync(botInstance, user, battle, cfg, message.MessageUuid, ctx);
                            return;
                        }

                        if (argsRaw.Equals("decline", StringComparison.OrdinalIgnoreCase))
                        {
                            await HandleDeclineAsync(botInstance, battle, cfg);
                            return;
                        }

                        await botInstance.SendChatMessageAsync(
                            $"{user.FormatUsername()}, {battle.ChallengerUsername} challenged you to a rap battle for " +
                            $"{await battle.Bet.FormatKasinoCurrencyAsync()}. Reply !rapbattle accept or !rapbattle decline.",
                            true, autoDeleteAfter: cfg.CleanupDelay);
                        return;
                    }

                    // The challenger is poking the bot while still waiting on their opponent.
                    await botInstance.SendChatMessageAsync(
                        $"{user.FormatUsername()}, your challenge to {battle.OpponentUsername} is still pending. " +
                        "Wait for them to accept or decline.", true, autoDeleteAfter: cfg.CleanupDelay);
                    return;
                }

                // CollectingVerses: any !rapbattle text from a participant is treated as their verse.
                var isChallenger = battle.ChallengerKfId == myKfId;
                var alreadySubmitted = isChallenger ? battle.ChallengerVerse != null : battle.OpponentVerse != null;
                if (alreadySubmitted)
                {
                    var otherName = isChallenger ? battle.OpponentUsername : battle.ChallengerUsername;
                    await botInstance.SendChatMessageAsync(
                        $"{user.FormatUsername()}, you've already dropped your verse. Waiting on {otherName}…", true,
                        autoDeleteAfter: cfg.CleanupDelay);
                    return;
                }

                if (string.IsNullOrWhiteSpace(argsRaw))
                {
                    await botInstance.SendChatMessageAsync(
                        $"{user.FormatUsername()}, the beat's running! Drop your bars with !rapbattle <your verse>.", true,
                        autoDeleteAfter: cfg.CleanupDelay);
                    return;
                }

                await HandleVerseAsync(botInstance, user, battle, isChallenger, argsRaw, cfg, ctx);
                return;
            }

            // Not currently in a battle -> the only valid action is starting one. The target is either a
            // numeric KfId or a username; usernames can contain spaces, so capture everything after the bet.
            var challengeMatch = Regex.Match(argsRaw, @"^(?<bet>\d+(?:\.\d+)?)\s+(?<target>.+?)\s*$");
            if (challengeMatch.Success)
            {
                var bet = decimal.Parse(challengeMatch.Groups["bet"].Value, NumberStyles.Number,
                    CultureInfo.InvariantCulture);
                // Mirror !whois input handling: tolerate a leading @ and trailing punctuation/space.
                var target = challengeMatch.Groups["target"].Value.TrimStart('@').TrimEnd(',').TrimEnd();

                int opponentKfId;
                if (Regex.IsMatch(target, @"^\d+$"))
                {
                    // All-digits is treated as a KfId, consistent with !whois / !juice / !pocketwatch.
                    opponentKfId = int.Parse(target);
                }
                else
                {
                    // Resolve the username to a KfId ourselves (exact match, case-insensitive, no fuzzy guessing).
                    var resolved = await FindOpponentKfIdByUsernameAsync(target, ctx);
                    if (resolved == null)
                    {
                        await botInstance.SendChatMessageAsync(
                            $"{user.FormatUsername()}, I couldn't find anyone called '{target}'. Check the spelling, " +
                            $"or challenge them by numeric ID (get it with !whois {target}).", true,
                            autoDeleteAfter: cfg.CleanupDelay);
                        RateLimitService.RemoveMostRecentEntry(user, this);
                        return;
                    }

                    opponentKfId = resolved.Value;
                }

                await HandleChallengeAsync(botInstance, user, bet, opponentKfId, cfg,
                    settings[BuiltIn.Keys.KiwiFarmsUsername].Value, message.MessageUuid, ctx);
                return;
            }

            await botInstance.SendChatMessageAsync($"{user.FormatUsername()}, that's not valid rap battle syntax.[br]" +
                BuildHelp(cfg), true, autoDeleteAfter: cfg.CleanupDelay);
            RateLimitService.RemoveMostRecentEntry(user, this);
        }
        finally
        {
            Gate.Release();
        }
    }

    /// <summary>
    /// Resolve a KiwiFarms username to a KfId the way !whois does its exact lookup: prefer an exact-case
    /// match, then fall back to case-insensitive. There is deliberately NO fuzzy guessing, so a typo can
    /// never lock the challenger's buy-in against the wrong person. Returns null if no such user is known.
    /// </summary>
    private static async Task<int?> FindOpponentKfIdByUsernameAsync(string username, CancellationToken ct)
    {
        await using var db = new ApplicationDbContext();
        var match = await db.Users.FirstOrDefaultAsync(u => u.KfUsername == username, ct);
        if (match == null)
        {
            var lower = username.ToLower();
            match = await db.Users.FirstOrDefaultAsync(u => u.KfUsername.ToLower() == lower, ct);
        }

        return match?.KfId;
    }

    private async Task HandleChallengeAsync(ChatBot botInstance, UserDbModel challenger,
        decimal bet, int opponentKfId, RapBattleConfig cfg, string? botUsername,
        string? challengerMessageUuid, CancellationToken ctx)
    {
        if (bet < cfg.MinimumBet)
        {
            await botInstance.SendChatMessageAsync(
                $"{challenger.FormatUsername()}, the minimum rap battle bet is {await cfg.MinimumBet.FormatKasinoCurrencyAsync()}.",
                true, autoDeleteAfter: cfg.CleanupDelay);
            RateLimitService.RemoveMostRecentEntry(challenger, this);
            return;
        }

        if (opponentKfId == challenger.KfId)
        {
            await botInstance.SendChatMessageAsync(
                $"{challenger.FormatUsername()}, you can't rap battle yourself. Find an opponent.", true,
                autoDeleteAfter: cfg.CleanupDelay);
            RateLimitService.RemoveMostRecentEntry(challenger, this);
            return;
        }

        await using var db = new ApplicationDbContext();
        var opponentUser = await db.Users.FirstOrDefaultAsync(u => u.KfId == opponentKfId, ctx);
        if (opponentUser == null)
        {
            await botInstance.SendChatMessageAsync(
                $"{challenger.FormatUsername()}, I've never seen a user with ID {opponentKfId}.", true,
                autoDeleteAfter: cfg.CleanupDelay);
            RateLimitService.RemoveMostRecentEntry(challenger, this);
            return;
        }

        if (botUsername != null && opponentUser.KfUsername.Equals(botUsername, StringComparison.OrdinalIgnoreCase))
        {
            await botInstance.SendChatMessageAsync(
                $"{challenger.FormatUsername()}, you can't rap battle the house.", true,
                autoDeleteAfter: cfg.CleanupDelay);
            RateLimitService.RemoveMostRecentEntry(challenger, this);
            return;
        }

        if (await FindBattleForUserAsync(opponentKfId) != null)
        {
            await botInstance.SendChatMessageAsync(
                $"{challenger.FormatUsername()}, {opponentUser.FormatUsername()} is already in a rap battle.", true,
                autoDeleteAfter: cfg.CleanupDelay);
            RateLimitService.RemoveMostRecentEntry(challenger, this);
            return;
        }

        var challengerGambler = await Money.GetGamblerEntityAsync(challenger.Id, ct: ctx);
        if (challengerGambler == null)
            throw new InvalidOperationException($"Caught a null when retrieving gambler for {challenger.KfUsername}");
        if (challengerGambler.Balance < bet)
        {
            await botInstance.SendChatMessageAsync(
                $"{challenger.FormatUsername()}, your balance of {await challengerGambler.Balance.FormatKasinoCurrencyAsync()} " +
                $"isn't enough to put up {await bet.FormatKasinoCurrencyAsync()}.", true,
                autoDeleteAfter: cfg.CleanupDelay);
            RateLimitService.RemoveMostRecentEntry(challenger, this);
            return;
        }

        var opponentGambler = await Money.GetGamblerEntityAsync(opponentUser.Id, ct: ctx);
        if (opponentGambler == null)
        {
            await botInstance.SendChatMessageAsync(
                $"{challenger.FormatUsername()}, {opponentUser.FormatUsername()} is banned from the Kasino and can't battle.",
                true, autoDeleteAfter: cfg.CleanupDelay);
            RateLimitService.RemoveMostRecentEntry(challenger, this);
            return;
        }

        var opponentExclusion = await Money.GetActiveExclusionAsync(opponentGambler.Id, ct: ctx);
        if (opponentExclusion != null)
        {
            await botInstance.SendChatMessageAsync(
                $"{challenger.FormatUsername()}, {opponentUser.FormatUsername()} is self-excluded and can't be dragged into a battle.",
                true, autoDeleteAfter: cfg.CleanupDelay);
            RateLimitService.RemoveMostRecentEntry(challenger, this);
            return;
        }

        // Lock the challenger's buy-in immediately so it can't be spent elsewhere while the opponent decides.
        // CancellationToken.None: a money move must never be torn half-way by the command timeout.
        await Money.ModifyBalanceAsync(challengerGambler.Id, -bet, TransactionSourceEventType.Gambling,
            $"Rap battle buy-in vs {opponentUser.KfUsername}", ct: CancellationToken.None);

        var battle = new RapBattleState
        {
            Id = Money.GenerateEventId(),
            Phase = RapBattlePhase.PendingAccept,
            DeadlineUnixMs = DeadlineFromNow(cfg),
            Bet = bet,
            ChallengerKfId = challenger.KfId,
            ChallengerUserDbId = challenger.Id,
            ChallengerGamblerId = challengerGambler.Id,
            ChallengerUsername = challenger.FormatUsername(),
            OpponentKfId = opponentUser.KfId,
            OpponentUserDbId = opponentUser.Id,
            OpponentGamblerId = opponentGambler.Id,
            OpponentUsername = opponentUser.FormatUsername(),
            ChallengeCommandMessageUuid = challengerMessageUuid
        };
        // Persist BEFORE announcing so a crash right here leaves a valid battle the sweeper can refund,
        // rather than a charged challenger with no record.
        await SaveBattleAsync(battle);

        // Append a random flourish from the editable challenge pool, e.g. "GET ON STAGE!".
        var flourish = PickChallengeFlourish(cfg.ChallengeFile, challenger.FormatUsername(),
            $"{opponentUser.FormatUsername()}");

        // Lead with the opponent's @-mention so Sneedchat pings them and they realise they've been challenged.
        var announcement = await botInstance.SendChatMessageAsync(
            $"🎤 {opponentUser.FormatUsername()}, you've been challenged to a RAP BATTLE by {challenger.FormatUsername()} for " +
            $"{await bet.FormatKasinoCurrencyAsync()} a head. {flourish} Reply [b]!rapbattle accept[/b] to throw down or " +
            $"[b]!rapbattle decline[/b] to back out — you've got {cfg.TimeoutSeconds} seconds. " +
            $"({challenger.FormatUsername()}'s buy-in is already locked up.)", true);

        // Record the announcement's UUID (once echoed) so it can be deleted on accept, even across a restart.
        await botInstance.WaitForChatMessageAsync(announcement, TimeSpan.FromSeconds(3), ctx);
        if (!string.IsNullOrEmpty(announcement.ChatMessageUuid))
        {
            battle.ChallengeAnnouncementUuid = announcement.ChatMessageUuid;
            await SaveBattleAsync(battle);
        }
    }

    private async Task HandleAcceptAsync(ChatBot botInstance, UserDbModel opponentUser, RapBattleState battle,
        RapBattleConfig cfg, string? accepterMessageUuid, CancellationToken ctx)
    {
        // Re-fetch the opponent's gambler to validate funds at the moment of acceptance.
        var opponentGambler = await Money.GetGamblerEntityAsync(opponentUser.Id, ct: ctx);
        if (opponentGambler == null)
        {
            await botInstance.SendChatMessageAsync(
                $"{opponentUser.FormatUsername()}, you can't accept, you're banned from the Kasino.", true,
                autoDeleteAfter: cfg.CleanupDelay);
            return;
        }

        if (opponentGambler.Balance < battle.Bet)
        {
            await botInstance.SendChatMessageAsync(
                $"{opponentUser.FormatUsername()}, your balance of {await opponentGambler.Balance.FormatKasinoCurrencyAsync()} " +
                $"isn't enough to match the {await battle.Bet.FormatKasinoCurrencyAsync()} buy-in. Juice up and try again before the clock runs out.",
                true, autoDeleteAfter: cfg.CleanupDelay);
            return;
        }

        battle.OpponentGamblerId = opponentGambler.Id;

        // Both buy-ins are now locked. Per the spec, the opponent is charged the instant they accept.
        // CancellationToken.None: a money move must never be torn half-way by the command timeout.
        await Money.ModifyBalanceAsync(opponentGambler.Id, -battle.Bet, TransactionSourceEventType.Gambling,
            $"Rap battle buy-in vs {battle.ChallengerUsername}", ct: CancellationToken.None);

        battle.Phase = RapBattlePhase.CollectingVerses;
        battle.DeadlineUnixMs = DeadlineFromNow(cfg); // fresh clock for the verse phase
        await SaveBattleAsync(battle);

        // Tidy up the challenge clutter now that it's accepted: the challenge command, the accept command,
        // and the bot's "you've been challenged" post. All best-effort.
        await TryDeleteMessageAsync(botInstance, battle.ChallengeCommandMessageUuid);
        await TryDeleteMessageAsync(botInstance, accepterMessageUuid);
        await TryDeleteMessageAsync(botInstance, battle.ChallengeAnnouncementUuid);

        var pot = battle.Bet * 2m;
        await botInstance.SendChatMessageAsync(
            $"🔥 IT'S ON! {battle.ChallengerUsername} vs {battle.OpponentUsername} for a pot of " +
            $"{await pot.FormatKasinoCurrencyAsync()}. Both of you, drop your fire verse with [b]!rapbattle <your bars>[/b]. " +
            $"You've got {cfg.TimeoutSeconds} seconds. 🎤", true);
    }

    private async Task HandleDeclineAsync(ChatBot botInstance, RapBattleState battle, RapBattleConfig cfg)
    {
        // The battle never started, so nobody is penalised. The challenger gets their full buy-in back.
        // Charging a fee here would let an opponent drain the challenger just by refusing, so we don't.
        await DeleteBattleAsync(battle.Id);

        // CancellationToken.None: a refund must never be torn half-way by the command timeout.
        var newBalance = await Money.ModifyBalanceAsync(battle.ChallengerGamblerId, battle.Bet,
            TransactionSourceEventType.Gambling,
            $"Rap battle declined by {battle.OpponentUsername}, buy-in refunded in full", ct: CancellationToken.None);

        await botInstance.SendChatMessageAsync(
            $"🐔 {battle.OpponentUsername} declined the rap battle. No harm done — {battle.ChallengerUsername}'s " +
            $"buy-in of {await battle.Bet.FormatKasinoCurrencyAsync()} has been returned in full " +
            $"(now {await newBalance.FormatKasinoCurrencyAsync()}).", true);
    }

    private async Task HandleVerseAsync(ChatBot botInstance, UserDbModel user, RapBattleState battle, bool isChallenger,
        string verse, RapBattleConfig cfg, CancellationToken ctx)
    {
        // The secret phrase counts as a cheat only while the cheat is still enabled, and the magic words
        // bypass the effort guards (they're inherently "enough"). We never reveal this happened.
        var usedCheat = cfg.CheatEnabled && ContainsCheat(verse);

        if (!usedCheat)
        {
            // Guard against low-effort entries. Reject without recording so the rapper can try again.
            if (verse.Length < cfg.MinimumVerseLength)
            {
                await botInstance.SendChatMessageAsync(
                    $"{user.FormatUsername()}, that's not a verse, that's a grunt. Give me at least {cfg.MinimumVerseLength} " +
                    "characters of actual bars with !rapbattle <your verse>.", true, autoDeleteAfter: cfg.CleanupDelay);
                RateLimitService.RemoveMostRecentEntry(user, this);
                return;
            }

            // Require at least the configured number of bars (sentences separated by full stops). No maximum.
            if (CountBars(verse) < cfg.MinimumBars)
            {
                await botInstance.SendChatMessageAsync(
                    $"{user.FormatUsername()}, a verse needs at least {cfg.MinimumBars} bars separated by full stops — " +
                    "e.g. [plain]bar one. bar two. bar three.[/plain] Spit it again with !rapbattle <your bars>.", true,
                    autoDeleteAfter: cfg.CleanupDelay);
                RateLimitService.RemoveMostRecentEntry(user, this);
                return;
            }
        }

        // We never read the verse to decide a winner - we just record it. The snippet quoted back later is
        // pure theatre to preserve the illusion that the bars mattered.
        if (isChallenger)
        {
            battle.ChallengerVerse = verse;
            battle.ChallengerUsedCheat = usedCheat;
        }
        else
        {
            battle.OpponentVerse = verse;
            battle.OpponentUsedCheat = usedCheat;
        }

        await SaveBattleAsync(battle);

        if (battle.ChallengerVerse == null || battle.OpponentVerse == null)
        {
            var otherName = isChallenger ? battle.OpponentUsername : battle.ChallengerUsername;
            await botInstance.SendChatMessageAsync(
                $"🎤 {user.FormatUsername()} has spit their verse! Waiting on {otherName} to drop theirs…", true,
                autoDeleteAfter: cfg.CleanupDelay);
            return;
        }

        await ResolveBattleAsync(botInstance, battle, cfg, ctx);
    }

    private async Task ResolveBattleAsync(ChatBot botInstance, RapBattleState battle, RapBattleConfig cfg,
        CancellationToken ctx)
    {
        // Fake suspense: make it look like the avelloon is actually weighing the bars before the verdict drops.
        // Keep the tracker so the "evaluating…" line can be deleted once the verdict has posted.
        var evaluating = EvaluatingMessages[RandomShim.Create(StandardRng.Create()).Next(0, EvaluatingMessages.Length)];
        var evaluatingMsg = await botInstance.SendChatMessageAsync(evaluating, true);
        await Task.Delay(TimeSpan.FromMilliseconds(cfg.EvaluatingDelayMs), ctx);

        // Both rappers played the cheat: nobody wins, the cheat is burned server-side forever, and the house
        // keeps both buy-ins. Enjoy riggery.
        if (battle.ChallengerUsedCheat && battle.OpponentUsedCheat)
        {
            await ResolveBothCheatedAsync(botInstance, battle);
            await TryDeleteTrackedMessageAsync(botInstance, evaluatingMsg);
            return;
        }

        // A single cheat forces that rapper's win; otherwise the winner is pure chance.
        bool challengerWins;
        if (battle.ChallengerUsedCheat) challengerWins = true;
        else if (battle.OpponentUsedCheat) challengerWins = false;
        else challengerWins = RandomShim.Create(StandardRng.Create()).NextDouble() < 0.5;

        var winnerName = challengerWins ? battle.ChallengerUsername : battle.OpponentUsername;
        var loserName = challengerWins ? battle.OpponentUsername : battle.ChallengerUsername;
        var winnerGamblerId = challengerWins ? battle.ChallengerGamblerId : battle.OpponentGamblerId;
        var loserGamblerId = challengerWins ? battle.OpponentGamblerId : battle.ChallengerGamblerId;
        var winnerVerse = challengerWins ? battle.ChallengerVerse : battle.OpponentVerse;

        var pot = battle.Bet * 2m;
        var casinoCut = pot * (cfg.CasinoCutPercent / 100m);
        var winnerShare = pot - casinoCut;
        var winnerPercent = 100m - cfg.CasinoCutPercent;

        // Retire the battle from Redis FIRST, then pay. State is durable now, so if we paid first and the
        // process died before the delete, the sweeper would find the still-live battle after restart and
        // refund both players on top of the payout — i.e. mint money. Deleting first makes double-payment
        // impossible; the only downside is that a (rare) crash between here and the payout strands the pot
        // with the house rather than paying it out, which deflates, never inflates. We hold the gate
        // throughout so the sweeper can't race us.
        await DeleteBattleAsync(battle.Id);

        var winnerBalance = await Money.ModifyBalanceAsync(winnerGamblerId, winnerShare,
            TransactionSourceEventType.Gambling, $"Rap battle winnings vs {loserName}", ct: CancellationToken.None);

        var verseSnippet = MakeVerseSnippet(winnerVerse);
        var reason = PickFeedback(cfg.FeedbackFile, $"{winnerName}", $"{loserName}", verseSnippet);

        await botInstance.SendChatMessageAsync(
            $"🎤🏆 The Kasino has mediated the battle between {battle.ChallengerUsername} and {battle.OpponentUsername}… " +
            $"and the winner is [b][color={cfg.GreenColor}]{winnerName}[/color][/b]! {reason} " +
            $"{winnerName} takes [color={cfg.GreenColor}]{await winnerShare.FormatKasinoCurrencyAsync()}[/color] " +
            $"({winnerPercent:0.##}% of the {await pot.FormatKasinoCurrencyAsync()} pot, balance now {await winnerBalance.FormatKasinoCurrencyAsync()}). " +
            $"As your humble mediator, the Kasino keeps {await casinoCut.FormatKasinoCurrencyAsync()} " +
            $"({cfg.CasinoCutPercent}% of the pot). Better luck next time, {loserName}. 🎤", true);

        // The verdict is up, so retire the "evaluating…" suspense line.
        await TryDeleteTrackedMessageAsync(botInstance, evaluatingMsg);

        // Record balance-neutral wager rows so the bout feeds wagered stats, rakeback and VIP progress.
        // Best-effort: the money is already settled, so a stats failure must not undo the result or surface
        // to the player as a command error.
        try
        {
            await Money.NewWagerAsync(winnerGamblerId, battle.Bet, winnerShare - battle.Bet, WagerGame.RapBattle,
                autoModifyBalance: false, ct: CancellationToken.None);
            await Money.NewWagerAsync(loserGamblerId, battle.Bet, -battle.Bet, WagerGame.RapBattle,
                autoModifyBalance: false, ct: CancellationToken.None);
        }
        catch (Exception e)
        {
            Logger.Error($"Failed to record rap battle wager stats for battle {battle.Id}");
            Logger.Error(e);
        }
    }

    private async Task ResolveBothCheatedAsync(ChatBot botInstance, RapBattleState battle)
    {
        // Both buy-ins already left their balances at escrow and they simply stay with the house — no payout,
        // no refund. Retire the battle (gate held, so the sweeper can't race us).
        await DeleteBattleAsync(battle.Id);

        // Burn the cheat out of the booth permanently. SetValueAsBooleanAsync invalidates the cache, so the
        // very next battle reads it as disabled.
        await SettingsProvider.SetValueAsBooleanAsync(BuiltIn.Keys.KasinoRapBattleCheatEnabled, false);
        Logger.Warn($"Rap battle cheat code burned: both {battle.ChallengerUsername} and {battle.OpponentUsername} " +
                    $"invoked it in battle {battle.Id}");

        var pot = battle.Bet * 2m;
        await botInstance.SendChatMessageAsync(
            $"🎈🌙 {battle.ChallengerUsername} and {battle.OpponentUsername} — You both invoked the Avelloon on the " +
            $"moon? Foolish, stalker child. Enjoy riggery! Both buy-ins of {await battle.Bet.FormatKasinoCurrencyAsync()} " +
            $"are forfeit to the house ({await pot.FormatKasinoCurrencyAsync()} total), and the cheat has been burned out " +
            "of the booth forever.", true);

        // Best-effort stats: record both as total losses (the money already left at buy-in time).
        try
        {
            await Money.NewWagerAsync(battle.ChallengerGamblerId, battle.Bet, -battle.Bet, WagerGame.RapBattle,
                autoModifyBalance: false, ct: CancellationToken.None);
            await Money.NewWagerAsync(battle.OpponentGamblerId, battle.Bet, -battle.Bet, WagerGame.RapBattle,
                autoModifyBalance: false, ct: CancellationToken.None);
        }
        catch (Exception e)
        {
            Logger.Error($"Failed to record rap battle wager stats for both-cheated battle {battle.Id}");
            Logger.Error(e);
        }
    }

    /// <summary>
    /// Called on a timer by BotServices. Refunds any battle whose deadline has passed - this is what makes
    /// timeouts survive a restart (there are no in-memory timers). Safe to call when Redis is unconfigured.
    /// </summary>
    public static async Task SweepExpiredBattlesAsync(ChatBot botInstance)
    {
        if (!RedisReady()) return;
        var ids = await GetActiveBattleIdsAsync();
        if (ids.Count == 0) return;

        var cfg = await LoadConfigAsync();
        await Gate.WaitAsync();
        try
        {
            var nowMs = DateTimeOffset.UtcNow.ToUnixTimeMilliseconds();
            foreach (var id in ids)
            {
                RapBattleState? battle;
                try
                {
                    battle = await LoadBattleAsync(id);
                }
                catch (Exception e)
                {
                    Logger.Error($"Rap battle sweep failed to load {id}");
                    Logger.Error(e);
                    continue;
                }

                if (battle == null)
                {
                    await RemoveFromActiveAsync(id); // stale index entry
                    continue;
                }

                if (nowMs < battle.DeadlineUnixMs) continue; // still within its window

                try
                {
                    await ProcessExpiredBattleAsync(botInstance, battle, cfg);
                }
                catch (Exception e)
                {
                    Logger.Error($"Rap battle sweep failed to process expired battle {id}");
                    Logger.Error(e);
                }
            }
        }
        finally
        {
            Gate.Release();
        }
    }

    private static async Task ProcessExpiredBattleAsync(ChatBot botInstance, RapBattleState battle, RapBattleConfig cfg)
    {
        await DeleteBattleAsync(battle.Id);

        if (battle.Phase == RapBattlePhase.PendingAccept)
        {
            // The battle never started (opponent never accepted), so the challenger is made whole.
            // No fee here, otherwise an opponent could drain the challenger just by ignoring the challenge.
            var newBalance = await Money.ModifyBalanceAsync(battle.ChallengerGamblerId, battle.Bet,
                TransactionSourceEventType.Gambling,
                "Rap battle timed out (no response), buy-in refunded in full", ct: CancellationToken.None);
            await botInstance.SendChatMessageAsync(
                $"⏰ {battle.OpponentUsername} didn't answer the rap battle in time. No harm done — " +
                $"{battle.ChallengerUsername}'s buy-in of {await battle.Bet.FormatKasinoCurrencyAsync()} is " +
                $"refunded in full (now {await newBalance.FormatKasinoCurrencyAsync()}).", true);
            return;
        }

        // CollectingVerses expired: both rappers committed and were charged, so the spec's mediation fee
        // applies. Refund both, less the fee each.
        var fee = battle.Bet * (cfg.RefundFeePercent / 100m);
        var refund = battle.Bet - fee;
        var challengerBalance = await Money.ModifyBalanceAsync(battle.ChallengerGamblerId, refund,
            TransactionSourceEventType.Gambling,
            $"Rap battle timed out (no verses), refund less {cfg.RefundFeePercent}% fee", ct: CancellationToken.None);
        var opponentBalance = await Money.ModifyBalanceAsync(battle.OpponentGamblerId, refund,
            TransactionSourceEventType.Gambling,
            $"Rap battle timed out (no verses), refund less {cfg.RefundFeePercent}% fee", ct: CancellationToken.None);

        var whoChoked = (battle.ChallengerVerse, battle.OpponentVerse) switch
        {
            (null, not null) => $"{battle.ChallengerUsername} froze up",
            (not null, null) => $"{battle.OpponentUsername} froze up",
            _ => "neither rapper could spit"
        };

        await botInstance.SendChatMessageAsync(
            $"⏰ Time's up — {whoChoked}! The battle between {battle.ChallengerUsername} and " +
            $"{battle.OpponentUsername} is off. The Kasino keeps a {cfg.RefundFeePercent}% mediation fee from each, " +
            $"refunding {battle.ChallengerUsername} {await refund.FormatKasinoCurrencyAsync()} " +
            $"(now {await challengerBalance.FormatKasinoCurrencyAsync()}) and {battle.OpponentUsername} " +
            $"{await refund.FormatKasinoCurrencyAsync()} (now {await opponentBalance.FormatKasinoCurrencyAsync()}).",
            true);
    }

    // ---- Redis-backed state store (all mutations happen under the Gate) --------------------------------

    private static bool RedisReady()
    {
        try
        {
            _ = Redis.Multiplexer; // triggers the lazy connect; throws if unconfigured/unreachable
            return true;
        }
        catch
        {
            return false;
        }
    }

    private static long DeadlineFromNow(RapBattleConfig cfg) =>
        DateTimeOffset.UtcNow.ToUnixTimeMilliseconds() + cfg.TimeoutSeconds * 1000L;

    private static async Task<RapBattleState?> LoadBattleAsync(string id) =>
        await Redis.GetJsonAsync<RapBattleState>(StateKey(id));

    private static async Task SaveBattleAsync(RapBattleState battle)
    {
        var db = Redis.Multiplexer.GetDatabase();
        await Redis.SetJsonAsync(StateKey(battle.Id), battle, KeyTtl);
        await db.StringSetAsync(UserKey(battle.ChallengerKfId), battle.Id, KeyTtl);
        await db.StringSetAsync(UserKey(battle.OpponentKfId), battle.Id, KeyTtl);
        await AddToActiveAsync(battle.Id);
    }

    private static async Task DeleteBattleAsync(string id)
    {
        var db = Redis.Multiplexer.GetDatabase();
        var state = await LoadBattleAsync(id);
        if (state != null)
        {
            await db.KeyDeleteAsync(UserKey(state.ChallengerKfId));
            await db.KeyDeleteAsync(UserKey(state.OpponentKfId));
        }
        await db.KeyDeleteAsync(StateKey(id));
        await RemoveFromActiveAsync(id);
    }

    private static async Task<RapBattleState?> FindBattleForUserAsync(int kfId)
    {
        var db = Redis.Multiplexer.GetDatabase();
        var id = await db.StringGetAsync(UserKey(kfId));
        if (string.IsNullOrEmpty(id)) return null;
        var state = await LoadBattleAsync(id!);
        if (state == null) await db.KeyDeleteAsync(UserKey(kfId)); // stale pointer, tidy it up
        return state;
    }

    private static async Task<List<string>> GetActiveBattleIdsAsync() =>
        await Redis.GetJsonAsync<List<string>>(ActiveKey) ?? [];

    private static async Task AddToActiveAsync(string id)
    {
        var list = await GetActiveBattleIdsAsync();
        if (list.Contains(id)) return;
        list.Add(id);
        await Redis.SetJsonAsync(ActiveKey, list);
    }

    private static async Task RemoveFromActiveAsync(string id)
    {
        var list = await GetActiveBattleIdsAsync();
        if (list.Remove(id)) await Redis.SetJsonAsync(ActiveKey, list);
    }

    private static async Task<RapBattleConfig> LoadConfigAsync()
    {
        var s = await SettingsProvider.GetMultipleValuesAsync([
            BuiltIn.Keys.KasinoRapBattleTimeout, BuiltIn.Keys.KasinoRapBattleCasinoCutPercent,
            BuiltIn.Keys.KasinoRapBattleRefundFeePercent, BuiltIn.Keys.KasinoRapBattleMinimumBet,
            BuiltIn.Keys.KasinoRapBattleMinimumVerseLength, BuiltIn.Keys.KasinoRapBattleMinimumBars,
            BuiltIn.Keys.KasinoRapBattleEvaluatingDelay, BuiltIn.Keys.KasinoRapBattleCleanupDelay,
            BuiltIn.Keys.KasinoRapBattleFeedbackFile, BuiltIn.Keys.KasinoRapBattleChallengeFile,
            BuiltIn.Keys.KasinoRapBattleCheatEnabled, BuiltIn.Keys.KiwiFarmsGreenColor, BuiltIn.Keys.KiwiFarmsRedColor
        ]);
        return new RapBattleConfig(
            s[BuiltIn.Keys.KasinoRapBattleTimeout].ToType<int>(),
            s[BuiltIn.Keys.KasinoRapBattleCasinoCutPercent].ToType<decimal>(),
            s[BuiltIn.Keys.KasinoRapBattleRefundFeePercent].ToType<decimal>(),
            s[BuiltIn.Keys.KasinoRapBattleMinimumBet].ToType<decimal>(),
            s[BuiltIn.Keys.KasinoRapBattleMinimumVerseLength].ToType<int>(),
            s[BuiltIn.Keys.KasinoRapBattleMinimumBars].ToType<int>(),
            s[BuiltIn.Keys.KasinoRapBattleEvaluatingDelay].ToType<int>(),
            TimeSpan.FromMilliseconds(s[BuiltIn.Keys.KasinoRapBattleCleanupDelay].ToType<int>()),
            Path.Join("Assets", s[BuiltIn.Keys.KasinoRapBattleFeedbackFile].Value ?? "rapbattle_feedback.txt"),
            Path.Join("Assets", s[BuiltIn.Keys.KasinoRapBattleChallengeFile].Value ?? "rapbattle_challenges.txt"),
            s[BuiltIn.Keys.KasinoRapBattleCheatEnabled].ToBoolean(),
            s[BuiltIn.Keys.KiwiFarmsGreenColor].Value ?? "#3dd179",
            s[BuiltIn.Keys.KiwiFarmsRedColor].Value ?? "#f1323e");
    }

    // ---- Chat message helpers -------------------------------------------------------------------------

    // Best-effort message deletion - a failure (already gone, perms, disconnect) must not break the flow.
    private static async Task TryDeleteMessageAsync(ChatBot botInstance, string? messageUuid)
    {
        if (string.IsNullOrEmpty(messageUuid)) return;
        try
        {
            await botInstance.KfClient.DeleteMessageAsync(messageUuid);
        }
        catch (Exception e)
        {
            Logger.Error($"Failed to delete rap battle message {messageUuid}");
            Logger.Error(e);
        }
    }

    // Delete a message the bot sent, briefly waiting for Sneedchat to echo it back so we know its UUID.
    private static async Task TryDeleteTrackedMessageAsync(ChatBot botInstance, SentMessageTrackerModel? tracker)
    {
        if (tracker == null) return;
        await botInstance.WaitForChatMessageAsync(tracker, TimeSpan.FromSeconds(2));
        await TryDeleteMessageAsync(botInstance, tracker.ChatMessageUuid);
    }

    // ---- Verse / feedback / challenge helpers ---------------------------------------------------------

    // Fake "the judges are deliberating" suspense lines, shown for a few seconds before the verdict.
    private static readonly string[] EvaluatingMessages =
    [
        "🧠 Evaluating the bars…",
        "🎚️ The avelloon is reviewing the tapes…",
        "📊 Running both verses through the riggery engine…",
        "🤔 Weighing flow, wordplay and sheer disrespect…",
        "⚖️ Deliberating… ooh, this one's close…",
        "🔬 Analysing every syllable for hidden heat…",
        "🎧 Replaying the punchlines in slow motion…",
        "📝 Tallying the scorecards…"
    ];

    // Built-in fallback so the game still works if the editable text file is missing or empty.
    private static readonly string[] FallbackFeedback =
    [
        "{winner}'s flow simply hit harder.",
        "{winner} had the crowd; {loser} had the silence.",
        "{winner}'s bars landed while {loser}'s missed.",
        "The Kasino found the line \"{verse}\" very moving. You win, {winner}.",
        "\"{verse}\" — that bar alone buried {loser}. {winner} takes it."
    ];

    // Built-in fallback challenge flourishes if the editable text file is missing or empty.
    private static readonly string[] FallbackChallenges =
    [
        "GET ON STAGE!",
        "Bars or be felted.",
        "The avelloon is watching. Don't choke.",
        "Step up or step off."
    ];

    private static string BuildHelp(RapBattleConfig cfg)
    {
        var winnerPercent = 100m - cfg.CasinoCutPercent;
        return
            "🎤 [b]RAP BATTLE[/b] 🎤[br]" +
            "① Challenge someone: [b]!rapbattle <bet> <user id>[/b] (find an id with [b]!whois <username>[/b])[br]" +
            "② They answer with [b]!rapbattle accept[/b] or [b]!rapbattle decline[/b] (declining costs nobody a thing)[br]" +
            $"③ Once it's on, spit your verse in ONE message within {cfg.TimeoutSeconds}s: [b]!rapbattle <your bars>[/b][br]" +
            $"Write at least {cfg.MinimumBars} bars in that single message, separated by full stops — e.g. " +
            "[plain]!rapbattle I run this room. you fold under pressure. your wallet felted itself at the kasino.[/plain][br]" +
            $"The avelloon weighs your every word and crowns a winner. Winner takes {winnerPercent:0.##}% of the pot, " +
            $"the Kasino keeps {cfg.CasinoCutPercent:0.##}% as your humble mediator.";
    }

    /// <summary>
    /// Count the bars (sentences separated by full stops) in a verse, ignoring empty/whitespace-only segments.
    /// Matches how <see cref="MakeVerseSnippet"/> splits, so the guard and the quote stay consistent.
    /// </summary>
    private static int CountBars(string verse)
    {
        return verse.Split('.', StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries).Length;
    }

    /// <summary>
    /// Whether the verse contains the secret cheat phrase anywhere within it. Case-insensitive and
    /// whitespace-tolerant (runs of whitespace are collapsed before matching).
    /// </summary>
    private static bool ContainsCheat(string verse)
    {
        var normalized = Regex.Replace(verse, @"\s+", " ").Trim().ToLowerInvariant();
        return normalized.Contains(CheatPhrase, StringComparison.Ordinal);
    }

    /// <summary>
    /// Build a chat-safe quotable snippet from the winner's verse. The verse is arbitrary user input, so we
    /// strip anything that could form BBCode/emotes, collapse whitespace, truncate, and wrap in [plain] tags.
    /// Players are told to separate their bars with full stops, so we quote one whole bar when we can.
    /// </summary>
    private static string MakeVerseSnippet(string? verse)
    {
        const int maxWords = 8;
        const int maxChars = 60;
        // Drop brackets so the snippet can't break out of the [plain] wrapper or inject BBCode/emotes,
        // and tame control characters (newlines, tabs) to spaces.
        var cleaned = new string((verse ?? string.Empty)
            .Where(c => c != '[' && c != ']')
            .Select(c => char.IsControl(c) ? ' ' : c)
            .ToArray());

        // Players are asked to separate their bars with full stops, so quote one whole bar when we can.
        var bars = cleaned.Split('.', StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries)
            .Select(bar => Regex.Replace(bar, @"\s+", " ").Trim())
            .Where(bar => bar.Length > 0)
            .ToArray();

        string snippet;
        if (bars.Length > 0)
        {
            var rng = RandomShim.Create(StandardRng.Create());
            snippet = bars[rng.Next(0, bars.Length)];
        }
        else
        {
            snippet = Regex.Replace(cleaned, @"\s+", " ").Trim();
        }

        var words = snippet.Split(' ', StringSplitOptions.RemoveEmptyEntries);
        var truncated = words.Length > maxWords;
        snippet = string.Join(' ', words.Take(maxWords));
        if (snippet.Length > maxChars)
        {
            snippet = snippet[..maxChars].TrimEnd();
            truncated = true;
        }

        if (string.IsNullOrWhiteSpace(snippet)) snippet = "that closing bar";
        else if (truncated) snippet += "…";

        return $"[plain]{snippet}[/plain]";
    }

    private static string PickFeedback(string feedbackFile, string winnerMention, string loserMention,
        string verseSnippet)
    {
        var pool = LoadLinePool(feedbackFile, FallbackFeedback);
        var rng = RandomShim.Create(StandardRng.Create());
        var line = pool[rng.Next(0, pool.Length)];
        return line.Replace("{winner}", winnerMention).Replace("{loser}", loserMention)
            .Replace("{verse}", verseSnippet);
    }

    private static string PickChallengeFlourish(string challengeFile, string challengerMention, string opponentMention)
    {
        var pool = LoadLinePool(challengeFile, FallbackChallenges);
        var rng = RandomShim.Create(StandardRng.Create());
        var line = pool[rng.Next(0, pool.Length)];
        return line.Replace("{challenger}", challengerMention).Replace("{opponent}", opponentMention);
    }

    /// <summary>
    /// Read an editable, hot-reloaded pool of lines from a text file, skipping blanks and '#' comments.
    /// Falls back to the supplied built-in pool if the file is missing, empty, or unreadable.
    /// </summary>
    private static string[] LoadLinePool(string file, string[] fallback)
    {
        try
        {
            if (!File.Exists(file))
            {
                Logger.Warn($"Rap battle text file '{file}' not found, using built-in fallback");
                return fallback;
            }

            var pool = File.ReadAllLines(file)
                .Select(l => l.Trim())
                .Where(l => l.Length > 0 && !l.StartsWith('#'))
                .ToArray();
            return pool.Length == 0 ? fallback : pool;
        }
        catch (Exception e)
        {
            Logger.Error($"Failed to read rap battle text file '{file}', using built-in fallback");
            Logger.Error(e);
            return fallback;
        }
    }
}
