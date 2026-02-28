
using System.Reflection;
using System.Text.Json;
using System.Text.Json.Serialization;
using System.Text.RegularExpressions;
using KfChatDotNetWsClient;
using KfChatDotNetBot;

namespace KfChatDotNetBot.Models;

[JsonConverter(typeof(MessageConverter))]
public abstract class UDPMessage
{
    [JsonPropertyName("type")]
    public string Type { get; set; } = "";

    internal virtual async Task HandleMessage(ChatBot chat)
    {
        await Task.CompletedTask;
    }
}

[AttributeUsage(AttributeTargets.Class, Inherited = false)]
public class UDPMessageAttribute : Attribute
{
    public string MessageType { get; }

    public UDPMessageAttribute(string messageType)
    {
        MessageType = messageType;
    }
}

public class MessageConverter : JsonConverter<UDPMessage>
{
    private static readonly Dictionary<string, Type> TypeMap = LoadMessageTypes();

    private static Dictionary<string, Type> LoadMessageTypes()
    {
        var map = new Dictionary<string, Type>();
        var baseType = typeof(UDPMessage);

        var messageTypes = Assembly.GetExecutingAssembly().GetTypes()
            .Where(t => baseType.IsAssignableFrom(t) && !t.IsAbstract);

        foreach (var type in messageTypes)
        {
            var attr = type.GetCustomAttribute<UDPMessageAttribute>();
            if (attr != null)
            {
                map[attr.MessageType] = type;
            }
        }

        return map;
    }

    public override UDPMessage? Read(ref Utf8JsonReader reader, Type typeToConvert, JsonSerializerOptions options)
    {
        var doc = JsonDocument.ParseValue(ref reader);

        if (!doc.RootElement.TryGetProperty("type", out var typeProp))
            throw new JsonException("Missing 'type' field.");

        var typeKey = typeProp.GetString();
        if (typeKey == null || !TypeMap.TryGetValue(typeKey, out var targetType))
            throw new JsonException($"Unknown message type '{typeKey}'.");

        var json = doc.RootElement.GetRawText();
        return (UDPMessage?)JsonSerializer.Deserialize(json, targetType, options);
    }

    public override void Write(Utf8JsonWriter writer, UDPMessage value, JsonSerializerOptions options)
    {
        JsonSerializer.Serialize(writer, (object)value, value.GetType(), options);
    }
}

[UDPMessage("new_poll")]
public class NewPollMessage : UDPMessage
{
    internal static SentMessageTrackerModel? LastPollMessage = null;
    internal static readonly SemaphoreSlim _lock = new SemaphoreSlim(1, 1);

    private static CancellationTokenSource? _timeoutCts;

    [JsonPropertyName("question")]
    public string? Question { get; set; }

    [JsonPropertyName("options")]
    public List<string>? Options { get; set; }

    internal override async Task HandleMessage(ChatBot chat)
    {
        if (string.IsNullOrEmpty(Question) || Options!.Count == 0)
            return;

        await _lock.WaitAsync();
        try
        {
            var options = string.Join("\n", Options.Select((x, i) => $" - {i + 1}. {x}"));
            LastPollMessage = await chat.SendChatMessageAsync($"[img]https://i.postimg.cc/yYkxDzwB/ft-0.png[/img] 🗳️ [b]{Question}[/b]\n" + options);

            _timeoutCts?.Cancel();
            _timeoutCts = new CancellationTokenSource();
            var cts = _timeoutCts;

            _ = Task.Run(async () =>
            {
                try
                {
                    await Task.Delay(45000, cts.Token);
                    await _lock.WaitAsync();
                    try
                    {
                        // Only null if no new value was set
                        if (_timeoutCts == cts)
                        {
                            LastPollMessage = null;
                            Console.WriteLine("Stopping poll updates.");
                        }
                    }
                    finally
                    {
                        _lock.Release();
                    }
                }
                catch (TaskCanceledException) { }
            });
        }
        finally
        {
            _lock.Release();
        }
    }


}

public class PollOption
{
    [JsonPropertyName("option")]
    public string? Option { get; set; }

    [JsonPropertyName("score")]
    public int Score { get; set; } = 0;
}

[UDPMessage("update_poll")]
public class UpdatePollMessage : UDPMessage
{
    [JsonPropertyName("question")]
    public string? Question { get; set; }

    [JsonPropertyName("options")]
    public List<PollOption>? Options { get; set; }

    internal override async Task HandleMessage(ChatBot chat)
    {
        if (string.IsNullOrEmpty(Question) || Options!.Count == 0)
            return;

        await NewPollMessage._lock.WaitAsync();
        try
        {
            if (NewPollMessage.LastPollMessage == null)
                return;

            var totalVotes = (double)Options.Sum(x => x.Score);
            if (totalVotes <= 0) return;
            var options = string.Join("\n", Options.OrderByDescending(opt => opt.Option).Select((x, i) => $" - {i + 1}. {x.Option} ({x.Score} / {Math.Round(x.Score / totalVotes * 1000f) / 10f}%)"));
            await chat.KfClient.EditMessageAsync(NewPollMessage.LastPollMessage!.ChatMessageUuid!, $"[img]https://i.postimg.cc/yYkxDzwB/ft-0.png[/img] 🗳️ [b]{Question}[/b]\n" + options);
        }
        finally
        {
            NewPollMessage._lock.Release();
        }
    }
}

[UDPMessage("notification")]
public class NotificationMessage : UDPMessage
{
    [JsonPropertyName("notification")]
    public string? Notification { get; set; }

    internal override async Task HandleMessage(ChatBot chat)
    {
        if (string.IsNullOrEmpty(Notification))
            return;
        await chat.SendChatMessageAsync("[img]https://i.postimg.cc/yYkxDzwB/ft-0.png[/img] ⚠" + Notification);
    }
}

// boo
[UDPMessage("ghost")]
public class GhostMessage : UDPMessage
{
    [JsonPropertyName("message")]
    public string? Message { get; set; }


    internal override async Task HandleMessage(ChatBot chat)
    {
        if (string.IsNullOrEmpty(Message))
        {
            return;
        }

        await chat.SendChatMessageAsync($"{Message}");
    }
}

[UDPMessage("chat")]
public class ChatMessage : UDPMessage
{
    [JsonPropertyName("message")]
    public string? Message { get; set; }

    [JsonPropertyName("user")]
    public string? User { get; set; }

    [JsonPropertyName("is_fish")]
    public bool IsFish { get; set; }

    [JsonPropertyName("is_staff")]
    public bool IsStaff { get; set; }

    public class EmojiData
    {
        public string URL { get; set; } = string.Empty;
        public DateTime UploadTime { get; set; } = DateTime.MinValue;
    }

    internal static Dictionary<string, EmojiData> EmojiDatabase { get; set; } = new Dictionary<string, EmojiData>();
    private static bool _emojiDatabaseLoaded = false;
    private static readonly SemaphoreSlim _emojiLock = new SemaphoreSlim(1, 1);

    internal static async Task LoadEmojiDatabaseAsync()
    {
        await _emojiLock.WaitAsync();
        try
        {
            if (_emojiDatabaseLoaded)
                return;

            try
            {
                var setting = await Settings.SettingsProvider.GetValueAsync("fishtank_emoji_cache");
                if (!string.IsNullOrEmpty(setting.Value))
                {
                    var loaded = JsonSerializer.Deserialize<Dictionary<string, EmojiData>>(setting.Value);
                    if (loaded != null)
                    {
                        EmojiDatabase = loaded;
                    }
                }
            }
            catch (KeyNotFoundException)
            {
                // Setting doesn't exist yet, will be created on first save
            }

            _emojiDatabaseLoaded = true;
        }
        finally
        {
            _emojiLock.Release();
        }
    }

    internal static async Task SaveEmojiURLsAsync()
    {
        await _emojiLock.WaitAsync();
        try
        {
            try
            {
                await Settings.SettingsProvider.SetValueAsJsonObjectAsync("fishtank_emoji_cache", EmojiDatabase);
            }
            catch (KeyNotFoundException)
            {
                // Setting doesn't exist, create it manually
                await using var db = new ApplicationDbContext();
                db.Settings.Add(new DbModels.SettingDbModel
                {
                    Key = "fishtank_emoji_cache",
                    Value = JsonSerializer.Serialize(EmojiDatabase),
                    Regex = @".+",
                    Description = "Fishtank emoji URL cache",
                    Default = "{}",
                    CacheDuration = 3600
                });
                await db.SaveChangesAsync();
            }
        }
        finally
        {
            _emojiLock.Release();
        }
    }

    internal async Task<string> ReplaceEmojis(string message)
    {
        // Ensure emoji database is loaded
        if (!_emojiDatabaseLoaded)
        {
            await LoadEmojiDatabaseAsync();
        }

        // Replace *emoji* with [img]https://example.com/emojis/emoji.png[/img]
        const string emojiPattern = @"\*(\w+)\*";
        var matches = System.Text.RegularExpressions.Regex.Matches(message, emojiPattern);
        if (matches.Count == 0)
            return message;

        int lastIndex = 0;
        var result = new System.Text.StringBuilder();

        foreach (Match match in matches)
        {
            result.Append(message, lastIndex, match.Index - lastIndex);
            string emojiName = match.Groups[1].Value;
            string replacement;
            if (EmojiDatabase.TryGetValue(emojiName, out EmojiData? emoji))
            {
                if (emoji.URL.StartsWith("http://") || emoji.URL.StartsWith("https://"))
                {
                    replacement = $"[img]{emoji}[/img]";
                }
                else
                {
                    replacement = match.Value;
                }
            }
            else
            {
                // Download/upload logic here (async)
                try
                {
                    const string endpoint = "https://cdn.fishtank.live/emojis/16/emotion_";
                    using var client = new HttpClient();
                    var response = await client.GetAsync($"{endpoint}{emojiName}.png");
                    if (response.IsSuccessStatusCode)
                    {
                        var stream = await response.Content.ReadAsStreamAsync();
                        var url = await Utils.PostImageUploadAsync($"{emojiName}.png", stream);
                        var emojiData = new EmojiData
                        {
                            URL = url,
                            UploadTime = DateTime.Now
                        };
                        EmojiDatabase[emojiName] = emojiData;
                        replacement = $"[img]{url}[/img]";
                        _ = SaveEmojiURLsAsync(); // Fire and forget to avoid blocking
                    }
                    else
                    {
                        var emojiData = new EmojiData
                        {
                            URL = match.Value, // Keep the original emoji if download fails
                            UploadTime = DateTime.Now
                        };
                        replacement = match.Value;
                        EmojiDatabase[emojiName] = emojiData; // Store the original if download fails, so we don't try again
                        _ = SaveEmojiURLsAsync(); // Fire and forget to avoid blocking
                    }
                }
                catch (HttpRequestException)
                {
                    var emojiData = new EmojiData
                    {
                        URL = match.Value, // Keep the original emoji if download fails
                        UploadTime = DateTime.Now
                    };
                    replacement = match.Value;
                    EmojiDatabase[emojiName] = emojiData; // Store the original if download fails, so we don't try again
                    _ = SaveEmojiURLsAsync(); // Fire and forget to avoid blocking
                }
            }

            result.Append(replacement);
            lastIndex = match.Index + match.Length;
        }

        result.Append(message, lastIndex, message.Length - lastIndex);
        return result.ToString();
    }

    internal static async Task<bool> IsWhitelistedUserAsync(string username)
    {
        try
        {
            var setting = await Settings.SettingsProvider.GetValueAsync("fishtank_whitelist");
            if (string.IsNullOrEmpty(setting.Value))
                return false;

            var whitelist = JsonSerializer.Deserialize<List<string>>(setting.Value);
            if (whitelist == null)
                return false;

            return whitelist.Any(u => u.Equals(username, StringComparison.OrdinalIgnoreCase));
        }
        catch (KeyNotFoundException)
        {
            return false;
        }
    }

    internal override async Task HandleMessage(ChatBot chat)
    {
        if (string.IsNullOrEmpty(Message) || string.IsNullOrEmpty(User))
            return;

        if (!await IsWhitelistedUserAsync(User))
        {
            return;
        }

        const string pattern = @"clip:(\d+)";
        const string replacement = "https://fishtank.live/clip/$1";

        // remove all potential bb code from the message
        Message = Regex.Replace(Message, @"\[/?[a-zA-Z]+\]", string.Empty);

        string formattedMessage = Regex.Replace(Message, pattern, replacement);

        formattedMessage = await ReplaceEmojis(formattedMessage);
        await chat.SendChatMessageAsync("[img]https://i.postimg.cc/yYkxDzwB/ft-0.png[/img] " + formattedMessage);
    }
}