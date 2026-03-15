using System.ComponentModel.DataAnnotations;

namespace KfChatDotNetBot.Models.DbModels;

public class CounterDbModel
{
    public int Id { get; set; }
    [MaxLength(64)]
    public required string Name { get; set; }
    public long Value { get; set; } = 0;
}
