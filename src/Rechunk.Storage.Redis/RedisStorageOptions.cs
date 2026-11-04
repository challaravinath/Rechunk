namespace Rechunk.Storage.Redis;

public sealed class RedisStorageOptions
{
    /// <summary>A StackExchange.Redis configuration string, for example localhost:6379.</summary>
    public string Configuration { get; set; } = "localhost:6379";

    /// <summary>Put in front of every key, so Rechunk can share a Redis with other things.</summary>
    public string KeyPrefix { get; set; } = "rechunk";

    /// <summary>The Redis database number, or -1 for the default one.</summary>
    public int Database { get; set; } = -1;
}
