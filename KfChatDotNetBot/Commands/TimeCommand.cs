using System.Text.RegularExpressions;
using KfChatDotNetBot.Models;
using KfChatDotNetBot.Models.DbModels;
using KfChatDotNetWsClient.Models.Events;

namespace KfChatDotNetBot.Commands;

public class TimeCommand : ICommand
{
    public List<Regex> Patterns => [new Regex("^time")];
    public string? HelpText => "Get current time in FTT";
    public UserRight RequiredRight => UserRight.Guest;
    public TimeSpan Timeout => TimeSpan.FromSeconds(10);
    public RateLimitOptionsModel? RateLimitOptions => null;
    public async Task RunCommand(ChatBot botInstance, MessageModel message, UserDbModel user, GroupCollection arguments, CancellationToken ctx)
    {
        var estZone = TimeZoneInfo.FindSystemTimeZoneById("Eastern Standard Time");
        var nowEst = TimeZoneInfo.ConvertTimeFromUtc(DateTime.UtcNow, estZone);
        var ftt = new DateTimeOffset(nowEst, estZone.BaseUtcOffset);

        var targetEst = new DateTime(2026, 3, 15, 16, 0, 0, DateTimeKind.Unspecified);
        var targetOffset = new DateTimeOffset(targetEst, estZone.BaseUtcOffset);

        string extraInfo;
        if (ftt < targetOffset)
        {
            var remaining = targetOffset - ftt;
            extraInfo = $" | {(int)remaining.TotalDays}d {remaining.Hours}h {remaining.Minutes}m {remaining.Seconds}s until Fishtank starts";
        }
        else
        {
            var elapsed = ftt - targetOffset;
            var days = (int)elapsed.TotalDays;
            extraInfo = $" on day {days}";
        }

        await botInstance.SendChatMessageAsync($"It's currently {ftt:dddd h:mm:ss tt} FTT{extraInfo}");
    }
}