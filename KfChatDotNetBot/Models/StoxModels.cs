using System.Text.Json.Serialization;

namespace KfChatDotNetBot.Models;

public class Stox
{
    [JsonPropertyName("tickerSymbol")]
    public string Symbol { get; set; } = string.Empty;
    [JsonPropertyName("currentPrice")]
    public int CurrentPrice { get; set; } = 0;
}

public class StoxData
{
    [JsonPropertyName("stocks")]
    public List<Stox> Stocks { get; set; } = new();
}
