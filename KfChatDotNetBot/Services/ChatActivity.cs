using KfChatDotNetBot;
using KfChatDotNetBot.Settings;
using NLog;
using Microsoft.EntityFrameworkCore;

public class ChatActivity
{
    private readonly Logger _logger = LogManager.GetCurrentClassLogger();

    private readonly ChatBot _chatBot;

    private float _messagesPerMinute5MinAvg = 0;

    public ChatActivity(ChatBot chatBot)
    {
        _chatBot = chatBot;
    }

    public async Task Update()
    {
        // Simple moving average over 5 minutes
        _messagesPerMinute5MinAvg = (_messagesPerMinute5MinAvg * 4 + _chatBot.IncomingMessageCounter) / 5;
        _logger.Info($"Chat activity: {_messagesPerMinute5MinAvg:F2} messages/min (5 min avg)");
        _chatBot.IncomingMessageCounter = 0;


        var modeSetting = await SettingsProvider.GetValueAsync(BuiltIn.Keys.OutputSilenceThreshold);
        if (modeSetting == null) return;

        var silenceThreshold = modeSetting.ToType<int>();

        if (_messagesPerMinute5MinAvg >= silenceThreshold)
        {
            _logger.Warn($"Chat activity above threshold ({_messagesPerMinute5MinAvg:F2} >= {silenceThreshold}), silencing bot outputs");
            await SettingsProvider.SetValueAsync(BuiltIn.Keys.OutputSilenced, "true");
        }
        else
        {
            var currentlySilenced = await SettingsProvider.GetValueAsync(BuiltIn.Keys.OutputSilenced);
            if (currentlySilenced != null && currentlySilenced.ToBoolean())
            {
                _logger.Info($"Chat activity below threshold ({_messagesPerMinute5MinAvg:F2} < {silenceThreshold}), unsilencing bot outputs");
                await SettingsProvider.SetValueAsync(BuiltIn.Keys.OutputSilenced, "false");
            }
        }
    }

    public static async Task<bool> IsKasinoOpen(CancellationToken ctx)
    {

        await using var db = new ApplicationDbContext();
        var allGameSettings = await db.Settings
            .Where(s => s.Key.StartsWith("Kasino.") && s.Key.EndsWith(".Enabled") && !s.Key.Contains("DailyDollar"))
            .ToListAsync(ctx);

        var values = await SettingsProvider.GetMultipleValuesAsync(allGameSettings.Select(s => s.Key).ToArray());

        if (values == null) return false;
        return values.Values.Any(v => v != null && v.Value != null && v.Value.Equals("true", StringComparison.OrdinalIgnoreCase));
    }

    public static async Task<bool> IsBotQuiet()
    {
        var modeSetting = await SettingsProvider.GetValueAsync(BuiltIn.Keys.OutputSilenced);
        if (modeSetting == null) return false;

        return modeSetting.ToBoolean();
    }

}