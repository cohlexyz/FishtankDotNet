
using System.Net;
using System.Text.Json;
using System.Text.RegularExpressions;
using KfChatDotNetBot.Models;
using KfChatDotNetBot.Models.DbModels;
using KfChatDotNetWsClient.Models.Events;
using NLog;

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

        await Task.Delay(TimeSpan.FromMinutes(1), ctx);
    }
}
