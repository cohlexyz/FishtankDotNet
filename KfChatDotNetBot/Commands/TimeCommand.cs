using System.Text.RegularExpressions;
using KfChatDotNetBot.Models;
using KfChatDotNetBot.Models.DbModels;
using KfChatDotNetWsClient.Models.Events;

namespace KfChatDotNetBot.Commands;

public class TimeCommand : ICommand
{
    public List<Regex> Patterns => [new Regex("^time$")];
    public string? HelpText => "Get current time in FTT";
    public UserRight RequiredRight => UserRight.Guest;
    public TimeSpan Timeout => TimeSpan.FromSeconds(10);
    public RateLimitOptionsModel? RateLimitOptions => null;
    public bool WhisperCanInvoke => true;
    public async Task RunCommand(ChatBot botInstance, BotCommandMessageModel message, UserDbModel user, GroupCollection arguments, CancellationToken ctx)
    {
        var estZone = TimeZoneInfo.FindSystemTimeZoneById("Eastern Standard Time");
        var nowEst = TimeZoneInfo.ConvertTimeFromUtc(DateTime.UtcNow, estZone);
        var estOffset = estZone.GetUtcOffset(nowEst);
        var ftt = new DateTimeOffset(nowEst, estOffset);

        var targetUtc = new DateTime(2026, 3, 15, 0, 0, 0, DateTimeKind.Utc);
        var nowUtc = DateTime.UtcNow;

        string extraInfo;
        if (nowUtc < targetUtc)
        {
            var remaining = targetUtc - nowUtc;
            extraInfo = $" | {(int)remaining.TotalDays}d {remaining.Hours}h {remaining.Minutes}m {remaining.Seconds}s until Fishtank starts";
        }
        else
        {
            var elapsed = nowUtc - targetUtc;
            var days = (int)Math.Ceiling(elapsed.TotalDays);
            extraInfo = $" on day {days}";
        }

        await botInstance.SendChatMessageAsync($"It's currently {ftt:dddd h:mm:ss tt} FTT{extraInfo}", whisperTo: user.KfId);
    }
}