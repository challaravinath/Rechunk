using System.Security.Cryptography;
using System.Text;
using Microsoft.Data.Sqlite;
using Rechunk.Core.Tests.Store;
using Rechunk.Storage.Sqlite;

namespace Rechunk.Core.Tests;

public class WrappedKeyPersistenceTests
{
    private const string KeyHex = "00112233445566778899aabbccddeeff00112233445566778899aabbccddeeff";

    private static readonly byte[] WrappingKey = Convert.FromHexString(KeyHex);

    [Fact]
    public async Task A_restart_with_the_same_wrapping_key_returns_the_same_scope_key()
    {
        using var file = new TempSqlite();
        var first = new LocalWrappedKeyProvider(WrappingKey, new SqliteWrappedKeyStore(file.ConnectionString));
        var created = await first.CreateAsync("docs");
        var other = await first.CreateAsync("other");
        var id = ChunkIds.FromText("hello world", null, created);
        first.Dispose();

        using var again = new LocalWrappedKeyProvider(WrappingKey.ToArray(), new SqliteWrappedKeyStore(file.ConnectionString));
        var loaded = await again.GetRequiredAsync("docs");
        var loadedOther = await again.GetRequiredAsync("other");

        Assert.True(created.Material.Span.SequenceEqual(loaded.Material.Span));
        Assert.Equal(id, ChunkIds.FromText("hello world", null, loaded));
        Assert.False(loaded.Material.Span.SequenceEqual(loadedOther.Material.Span));
        Assert.NotEqual(id, ChunkIds.FromText("hello world", null, loadedOther));
        await AssertPlaintextIsNotStored(file.ConnectionString, created.Material.ToArray());
        var stored = await Snapshot(file.ConnectionString, "docs");
        var duplicate = new WrappedKeyRecord("docs", 1, stored.Nonce, stored.Tag, Enumerable.Repeat((byte)3, 32).ToArray());
        Assert.False(await new SqliteWrappedKeyStore(file.ConnectionString).TryAddAsync(duplicate));
        AssertSame(stored, await Snapshot(file.ConnectionString, "docs"));
    }

    [Fact]
    public async Task A_wrong_key_or_a_damaged_row_does_not_replace_what_is_stored()
    {
        using var file = new TempSqlite();
        var store = new SqliteWrappedKeyStore(file.ConnectionString);
        using var provider = new LocalWrappedKeyProvider(WrappingKey, store);
        await provider.CreateAsync("docs");
        var before = await Snapshot(file.ConnectionString, "docs");

        var wrong = new LocalWrappedKeyProvider(Enumerable.Repeat((byte)9, 32).ToArray(), store);
        var wrongError = await Assert.ThrowsAnyAsync<CryptographicException>(() => wrong.GetRequiredAsync("docs").AsTask());
        Assert.DoesNotContain(KeyHex, wrongError.ToString(), StringComparison.OrdinalIgnoreCase);
        Assert.DoesNotContain("hello world", wrongError.ToString(), StringComparison.Ordinal);
        AssertSame(before, await Snapshot(file.ConnectionString, "docs"));
        await Assert.ThrowsAnyAsync<CryptographicException>(() => wrong.CreateAsync("docs").AsTask());
        AssertSame(before, await Snapshot(file.ConnectionString, "docs"));

        await FlipCiphertext(file.ConnectionString, "docs");
        var tampered = await Snapshot(file.ConnectionString, "docs");
        var fresh = new LocalWrappedKeyProvider(WrappingKey, new SqliteWrappedKeyStore(file.ConnectionString));
        var tamperError = await Assert.ThrowsAnyAsync<CryptographicException>(() => fresh.GetRequiredAsync("docs").AsTask());
        Assert.DoesNotContain(KeyHex, tamperError.ToString(), StringComparison.OrdinalIgnoreCase);
        await Assert.ThrowsAnyAsync<CryptographicException>(() => fresh.CreateAsync("docs").AsTask());
        fresh.Dispose();
        AssertSame(tampered, await Snapshot(file.ConnectionString, "docs"));

        await SetVersion(file.ConnectionString, "docs", 2);
        await Assert.ThrowsAnyAsync<CryptographicException>(() =>
            new LocalWrappedKeyProvider(WrappingKey, new SqliteWrappedKeyStore(file.ConnectionString)).GetRequiredAsync("docs").AsTask());
        Assert.Equal(2, (await Snapshot(file.ConnectionString, "docs")).Version);

        await MoveScope(file.ConnectionString, "docs", "other");
        var moved = new SqliteWrappedKeyStore(file.ConnectionString);
        Assert.Null(await moved.ReadAsync("docs"));
        await Assert.ThrowsAnyAsync<CryptographicException>(() =>
            new LocalWrappedKeyProvider(WrappingKey, moved).GetRequiredAsync("other").AsTask());
        var afterMove = await Snapshot(file.ConnectionString, "other");
        await Assert.ThrowsAnyAsync<CryptographicException>(() =>
            new LocalWrappedKeyProvider(WrappingKey, moved).CreateAsync("other").AsTask());
        AssertSame(afterMove, await Snapshot(file.ConnectionString, "other"));
    }

    [Fact]
    public async Task Concurrent_creators_on_separate_connections_keep_one_stored_key()
    {
        using var file = new TempSqlite();
        var winners = await Task.WhenAll(Enumerable.Range(0, 8).Select(async _ =>
        {
            var provider = new LocalWrappedKeyProvider(
                WrappingKey.ToArray(),
                new SqliteWrappedKeyStore(file.ConnectionString));
            try
            {
                return (await provider.CreateAsync("docs")).Material.ToArray();
            }
            finally
            {
                provider.Dispose();
            }
        }));

        foreach (var winner in winners)
        {
            Assert.True(winners[0].AsSpan().SequenceEqual(winner));
        }

        await using var connection = new SqliteConnection(file.ConnectionString);
        await connection.OpenAsync();
        await using var count = connection.CreateCommand();
        count.CommandText = "SELECT COUNT(*) FROM rechunk_wrapped_key WHERE scope = $scope";
        count.Parameters.AddWithValue("$scope", "docs");
        Assert.Equal(1L, (long)(await count.ExecuteScalarAsync())!);
    }

    [Fact]
    public async Task Adding_the_key_table_leaves_an_existing_fingerprint_row_alone()
    {
        using var file = new TempSqlite();
        var backend = new SqliteFingerprintBackend(file.ConnectionString);
        var hash = Encoding.UTF8.GetBytes("0123456789abcdef0123456789abcdef");
        var claim = await backend.ClaimAsync(
            new ClaimRequest("docs", new ChunkId("abc"), hash, "holder", TimeSpan.FromMinutes(1)),
            DateTimeOffset.UnixEpoch,
            default);
        Assert.Equal(ClaimStatus.Claimed, claim.Status);

        using var keys = new LocalWrappedKeyProvider(WrappingKey, new SqliteWrappedKeyStore(file.ConnectionString));
        await keys.CreateAsync("tenant");

        var still = await backend.FindCommittedAsync("docs", hash);
        Assert.Null(still);
        var again = await backend.ClaimAsync(
            new ClaimRequest("docs", new ChunkId("abc"), hash, "holder", TimeSpan.FromMinutes(1)),
            DateTimeOffset.UnixEpoch.AddSeconds(1),
            default);
        Assert.Equal(ClaimStatus.Renewed, again.Status);
        Assert.Equal("abc", again.Owner.Value);
    }

    private static async Task AssertPlaintextIsNotStored(string connectionString, byte[] material)
    {
        var row = await Snapshot(connectionString, "docs");
        Assert.False(row.Ciphertext.AsSpan().SequenceEqual(material));
        Assert.False(row.Nonce.AsSpan().SequenceEqual(WrappingKey.AsSpan(0, 12)));
        var stored = row.Nonce.Concat(row.Tag).Concat(row.Ciphertext).ToArray();
        Assert.False(Contains(stored, material));
        Assert.False(Contains(stored, WrappingKey));
    }

    private static bool Contains(byte[] haystack, ReadOnlySpan<byte> needle)
    {
        for (var i = 0; i + needle.Length <= haystack.Length; i++)
        {
            if (haystack.AsSpan(i, needle.Length).SequenceEqual(needle))
            {
                return true;
            }
        }

        return false;
    }

    private static async Task<Row> Snapshot(string connectionString, string scope)
    {
        await using var connection = new SqliteConnection(connectionString);
        await connection.OpenAsync();
        await using var command = connection.CreateCommand();
        command.CommandText = """
            SELECT version, nonce, tag, ciphertext
            FROM rechunk_wrapped_key
            WHERE scope = $scope
            """;
        command.Parameters.AddWithValue("$scope", scope);
        await using var reader = await command.ExecuteReaderAsync();
        Assert.True(await reader.ReadAsync());
        return new Row(reader.GetInt32(0), (byte[])reader[1], (byte[])reader[2], (byte[])reader[3]);
    }

    private static async Task FlipCiphertext(string connectionString, string scope)
    {
        var row = await Snapshot(connectionString, scope);
        row.Ciphertext[0] ^= 0x01;
        await Write(connectionString, scope, row);
    }

    private static Task SetVersion(string connectionString, string scope, int version)
    {
        return WithCommand(connectionString, "UPDATE rechunk_wrapped_key SET version = $version WHERE scope = $scope", command =>
        {
            command.Parameters.AddWithValue("$version", version);
            command.Parameters.AddWithValue("$scope", scope);
        });
    }

    private static async Task MoveScope(string connectionString, string from, string to)
    {
        var row = await Snapshot(connectionString, from);
        await WithCommand(connectionString, "UPDATE rechunk_wrapped_key SET scope = $to WHERE scope = $from", command =>
        {
            command.Parameters.AddWithValue("$to", to);
            command.Parameters.AddWithValue("$from", from);
        });
        var moved = await Snapshot(connectionString, to);
        Assert.Equal(row.Ciphertext, moved.Ciphertext);
    }

    private static Task Write(string connectionString, string scope, Row row) =>
        WithCommand(connectionString, """
            UPDATE rechunk_wrapped_key
            SET version = $version, nonce = $nonce, tag = $tag, ciphertext = $ciphertext
            WHERE scope = $scope
            """, command =>
        {
            command.Parameters.AddWithValue("$version", row.Version);
            command.Parameters.AddWithValue("$nonce", row.Nonce);
            command.Parameters.AddWithValue("$tag", row.Tag);
            command.Parameters.AddWithValue("$ciphertext", row.Ciphertext);
            command.Parameters.AddWithValue("$scope", scope);
        });

    private static async Task WithCommand(string connectionString, string sql, Action<SqliteCommand> bind)
    {
        await using var connection = new SqliteConnection(connectionString);
        await connection.OpenAsync();
        await using var command = connection.CreateCommand();
        command.CommandText = sql;
        bind(command);
        await command.ExecuteNonQueryAsync();
    }

    private static void AssertSame(Row expected, Row actual)
    {
        Assert.Equal(expected.Version, actual.Version);
        Assert.Equal(expected.Nonce, actual.Nonce);
        Assert.Equal(expected.Tag, actual.Tag);
        Assert.Equal(expected.Ciphertext, actual.Ciphertext);
    }

    private sealed record Row(int Version, byte[] Nonce, byte[] Tag, byte[] Ciphertext);
}
