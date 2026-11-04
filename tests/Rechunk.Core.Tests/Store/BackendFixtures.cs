using Microsoft.Data.Sqlite;
using StackExchange.Redis;
using Xunit;

namespace Rechunk.Core.Tests.Store;

/// <summary>A SQLite file in the temp folder that goes away with the test.</summary>
internal sealed class TempSqlite : IDisposable
{
    private readonly string _path = Path.Combine(Path.GetTempPath(), "rechunk-test-" + Guid.NewGuid().ToString("N") + ".db");

    public string ConnectionString => $"Data Source={_path}";

    public void Dispose()
    {
        SqliteConnection.ClearAllPools();
        foreach (var suffix in new[] { string.Empty, "-wal", "-shm" })
        {
            try
            {
                File.Delete(_path + suffix);
            }
            catch (IOException)
            {
                // a temp file that stays behind is not worth failing a test for
            }
        }
    }
}

/// <summary>
/// The Redis tests only run when RECHUNK_REDIS holds a connection string, for example localhost:6379. Every test uses
/// its own key prefix and removes its keys at the end, so a Redis that is used for other things is left alone.
/// </summary>
internal sealed class RedisTestEnvironment : IDisposable
{
    public const string Variable = "RECHUNK_REDIS";

    private static readonly Lazy<ConnectionMultiplexer?> Shared = new(() =>
    {
        var configuration = Environment.GetEnvironmentVariable(Variable);
        return string.IsNullOrWhiteSpace(configuration) ? null : ConnectionMultiplexer.Connect(configuration + ",allowAdmin=true");
    });

    public RedisTestEnvironment()
    {
        Skip.If(Shared.Value is null, $"Set {Variable} to a Redis connection string to run the Redis tests.");
        Redis = Shared.Value!;
        Prefix = "rechunk-test-" + Guid.NewGuid().ToString("N");
    }

    public IConnectionMultiplexer Redis { get; }

    public string Prefix { get; }

    public void Dispose()
    {
        foreach (var endpoint in Redis.GetEndPoints())
        {
            var server = Redis.GetServer(endpoint);
            if (server.IsReplica)
            {
                continue;
            }

            var keys = server.Keys(pattern: Prefix + ":*").ToArray();
            if (keys.Length > 0)
            {
                Redis.GetDatabase().KeyDelete(keys);
            }
        }
    }
}
