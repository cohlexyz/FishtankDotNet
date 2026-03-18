namespace KfChatDotNetBot.Models;

public class RecentChatMessageModel
{
    public required int AuthorId { get; set; }
    public required string AuthorUsername { get; set; }
    public required string MessageUuid { get; set; }
}
