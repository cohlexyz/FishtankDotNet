using KfChatDotNetBot.Settings;
using System.Text.Json;
using NLog;
using StackExchange.Redis;

namespace KfChatDotNetBot.Services;

public static class Redis
{
    public static bool IsAvailable => LazyMultiplexer.IsValueCreated;
    // Claude told me this will act like a singleton ConnectionMultiplexer
    // while keeping things nice and convenient with static methods
    // FYI the exception will be thrown once, cached for the lifetime of the application
    // If you configure a Redis connection string, you MUST restart the application
    // https://learn.microsoft.com/en-us/dotnet/api/system.lazy-1?view=net-10.0#:~:text=Exception%20caching,-When
    private static readonly Lazy<ConnectionMultiplexer> LazyMultiplexer =
        new(() =>
        {
            var logger = LogManager.GetCurrentClassLogger();
            var connectionString = SettingsProvider.GetValueAsync(BuiltIn.Keys.BotRedisConnectionString).Result.Value;
            if (string.IsNullOrEmpty(connectionString))
            {
                logger.Error($"Could not initiate the lazy connection multiplexer for the Redis service as the " +
                             $"connection string is not configured in {BuiltIn.Keys.BotRedisConnectionString}. " +
                             $"Redis won't be available to anything that relies on it. " +
                             $"If you do configure {BuiltIn.Keys.BotRedisConnectionString}, YOU MUST RESTART THE BOT");
                throw new InvalidOperationException();
            }

            try
            {
                return ConnectionMultiplexer.Connect(
                    SettingsProvider.GetValueAsync(BuiltIn.Keys.BotRedisConnectionString).Result.Value ??
                    throw new InvalidOperationException(
                        $"{BuiltIn.Keys.BotRedisConnectionString} not defined, cannot connect to Redis"));
            }
            catch (Exception e)
            {
                logger.Error($"Caught an exception when connecting to Redis at {connectionString}");
                logger.Error(e);
                throw;
            }
        });

    // You can just grab this from wherever if you want a ready to go Redis connection
    // ReSharper disable once MemberCanBePrivate.Global
    public static ConnectionMultiplexer Multiplexer => LazyMultiplexer.Value;

    private static IDatabase Db => Multiplexer.GetDatabase();

    /// <summary>
    /// Fetches a key from Redis asynchronously and deserializes its JSON value to T.
    /// Returns default(T) if the key doesn't exist.
    /// </summary>
    /// <param name="key">Redis key</param>
    public static async Task<T?> GetJsonAsync<T>(string key)
    {
        var value = await Db.StringGetAsync(key);
        if (value.IsNullOrEmpty)
            return default;

        return JsonSerializer.Deserialize<T>(value.ToString());
    }

    /// <summary>
    /// Fetches a key from Redis synchronously and deserializes its JSON value to T.
    /// Returns default(T) if the key doesn't exist.
    /// </summary>
    /// <param name="key">Redis key</param>
    public static T? GetJson<T>(string key)
    {
        var value = Db.StringGet(key);
        if (value.IsNullOrEmpty)
            return default;

        return JsonSerializer.Deserialize<T>(value.ToString());
    }

    /// <summary>
    /// Asynchronously set a key to a given object serialized using JSON
    /// </summary>
    /// <param name="key">Redis key</param>
    /// <param name="value">Object that you wish to serialize</param>
    /// <param name="expires">Expiration (null means never expires)</param>
    /// <param name="when">Redis behavior whether the key has a value or not
    /// When.Always = set the value regardless of whether the key has a value
    /// When.Exists = only set the value if the key has a value already
    /// When.NotExists = only set the value if the key has no value
    /// </param>
    public static async Task SetJsonAsync(string key, object value, TimeSpan? expires = null, When when = When.Always)
    {
        await Db.StringSetAsync(key, JsonSerializer.Serialize(value), expires, when);
    }
    
    /// <summary>
    /// Synchronously set a key to a given object serialized using JSON
    /// </summary>
    /// <param name="key">Redis key</param>
    /// <param name="value">Object that you wish to serialize</param>
    /// <param name="expires">Expiration (null means never expires)</param>
    /// <param name="when">Redis behavior whether the key has a value or not
    /// When.Always = set the value regardless of whether the key has a value
    /// When.Exists = only set the value if the key has a value already
    /// When.NotExists = only set the value if the key has no value
    /// </param>
    public static void SetJson(string key, object value, TimeSpan? expires = null, When when = When.Always)
    {
        Db.StringSet(key, JsonSerializer.Serialize(value), expires, when);
    }
}
