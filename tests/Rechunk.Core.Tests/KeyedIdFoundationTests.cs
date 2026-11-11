using System.Security.Cryptography;
using System.Text;

namespace Rechunk.Core.Tests;

public class KeyedIdFoundationTests
{
    // HMAC-SHA256(key, SHA-256("hello world")), checked with an independent HMAC implementation.
    // "hello world" has nothing for the normalizer to change, so the digest is the published SHA-256.
    private const string KeyHex = "00112233445566778899aabbccddeeff00112233445566778899aabbccddeeff";

    private const string HelloDigest = "b94d27b9934d3e08a52e52d7da7dabfac484efe37a5380ee9088f7ace2efcde9";

    private const string HelloMac = "fb45efddd96f091d8196fe0753272d3cace7639dadbe0f1811a7632d924e3e24";

    private static readonly byte[] WrappingKey = Convert.FromHexString(KeyHex);

    [Fact]
    public void Hmac_id_matches_the_known_answer_for_cleaned_text()
    {
        var digest = SHA256.HashData(Encoding.UTF8.GetBytes("hello world"));
        Assert.Equal(HelloDigest, Convert.ToHexString(digest).ToLowerInvariant());
        Assert.Equal(HelloDigest, Convert.ToHexString(ExactHasher.Hash("hello world")).ToLowerInvariant());

        var key = new ScopeKey(1, Convert.FromHexString(KeyHex));
        var id = ChunkIds.Create(digest, null, key);

        Assert.Equal("k2." + HelloMac, id.Value);
        Assert.Equal(id, ChunkIds.FromText("hello world", null, key));
        Assert.Equal(id, ChunkIds.FromText("hello  world\r\n", null, key));
        Assert.False(LanguageTags.TryParseId(id, out _, out _));

        var callerBuffer = Convert.FromHexString(KeyHex);
        var owned = new ScopeKey(1, callerBuffer);
        callerBuffer[0] ^= 0xff;
        Assert.Equal(id, ChunkIds.Create(digest, null, owned));
        Assert.Equal(KeyHex, Convert.ToHexString(owned.Material.Span).ToLowerInvariant());
    }

    [Fact]
    public async Task The_same_scope_key_is_stable_and_other_scopes_differ()
    {
        using var keys = Provider();
        var docs = await keys.CreateAsync("docs");
        var again = await keys.CreateAsync("docs");
        var other = await keys.CreateAsync("other");

        Assert.True(docs.Material.Span.SequenceEqual(again.Material.Span));
        Assert.False(docs.Material.Span.SequenceEqual(other.Material.Span));
        Assert.Equal(1, docs.Version);
        Assert.Equal(1, other.Version);

        var left = ChunkIds.FromText("hello world", null, docs);
        var right = ChunkIds.FromText("hello world", null, docs);
        var elsewhere = ChunkIds.FromText("hello world", null, other);
        var french = ChunkIds.FromText("hello world", "fr", docs);

        Assert.Equal(left, right);
        Assert.NotEqual(left, elsewhere);
        Assert.Equal(left.Value + ".fr", french.Value);
        Assert.DoesNotContain(KeyHex, docs.ToString(), StringComparison.OrdinalIgnoreCase);
    }

    [Fact]
    public async Task Wrap_roundtrip_wrong_key_and_tamper_fail_without_leaking_the_key()
    {
        var store = new InMemoryWrappedKeyStore();
        using var first = new LocalWrappedKeyProvider(WrappingKey, store);
        var created = await first.CreateAsync("docs");
        var id = ChunkIds.FromText("hello world", null, created);

        using var again = new LocalWrappedKeyProvider(WrappingKey.ToArray(), store);
        var loaded = await again.GetRequiredAsync("docs");
        Assert.Equal(id, ChunkIds.FromText("hello world", null, loaded));

        var wrong = new LocalWrappedKeyProvider(Enumerable.Repeat((byte)9, 32).ToArray(), store);
        var badKey = await Assert.ThrowsAnyAsync<CryptographicException>(() => wrong.GetRequiredAsync("docs").AsTask());
        Assert.DoesNotContain(KeyHex, badKey.ToString(), StringComparison.OrdinalIgnoreCase);
        Assert.DoesNotContain("hello world", badKey.ToString(), StringComparison.Ordinal);

        var tamper = new FlipStore();
        using var source = new LocalWrappedKeyProvider(WrappingKey, tamper);
        await source.CreateAsync("docs");
        tamper.Flip = true;
        using var reader = new LocalWrappedKeyProvider(WrappingKey, tamper);
        var broken = await Assert.ThrowsAnyAsync<CryptographicException>(() => reader.GetRequiredAsync("docs").AsTask());
        Assert.DoesNotContain(KeyHex, broken.ToString(), StringComparison.OrdinalIgnoreCase);

        using var missing = Provider();
        var absent = await Assert.ThrowsAsync<KeyUnavailableException>(() => missing.GetRequiredAsync("docs").AsTask());
        Assert.DoesNotContain(KeyHex, absent.ToString(), StringComparison.OrdinalIgnoreCase);
        Assert.Equal("docs", absent.Scope);

        var saved = await store.ReadAsync("docs");
        Assert.NotNull(saved);
        Assert.Equal(12, saved!.Nonce.Length);
        Assert.Equal(16, saved.Tag.Length);
        Assert.Equal(32, saved.Ciphertext.Length);
        Assert.False(saved.Ciphertext.AsSpan().SequenceEqual(created.Material.Span));
        Assert.DoesNotContain(KeyHex, saved.ToString(), StringComparison.OrdinalIgnoreCase);

        var moved = new MovedScopeStore(saved);
        using var rebound = new LocalWrappedKeyProvider(WrappingKey, moved);
        var reboundError = await Assert.ThrowsAnyAsync<CryptographicException>(() => rebound.GetRequiredAsync("other").AsTask());
        Assert.DoesNotContain(KeyHex, reboundError.ToString(), StringComparison.OrdinalIgnoreCase);

        first.Dispose();
        Assert.Equal(id, ChunkIds.FromText("hello world", null, created));
        await Assert.ThrowsAsync<ObjectDisposedException>(() => first.GetRequiredAsync("docs").AsTask());
        await Assert.ThrowsAsync<ObjectDisposedException>(() => first.CreateAsync("fresh").AsTask());
    }

    [Fact]
    public async Task Concurrent_create_keeps_one_key_for_the_scope()
    {
        using var keys = Provider();
        var results = await Task.WhenAll(Enumerable.Range(0, 8).Select(_ => keys.CreateAsync("docs").AsTask()));
        var first = results[0].Material.ToArray();
        foreach (var result in results)
        {
            Assert.True(first.AsSpan().SequenceEqual(result.Material.Span));
        }
    }

    private static LocalWrappedKeyProvider Provider() =>
        new(WrappingKey.ToArray(), new InMemoryWrappedKeyStore());

    private sealed class MovedScopeStore : IWrappedKeyStore
    {
        private readonly WrappedKeyRecord _record;

        public MovedScopeStore(WrappedKeyRecord record) => _record = record;

        public ValueTask<WrappedKeyRecord?> ReadAsync(string scope, CancellationToken ct = default) =>
            ValueTask.FromResult<WrappedKeyRecord?>(new WrappedKeyRecord(
                scope, _record.Version, _record.Nonce, _record.Tag, _record.Ciphertext));

        public ValueTask<bool> TryAddAsync(WrappedKeyRecord record, CancellationToken ct = default) =>
            ValueTask.FromResult(false);
    }

    private sealed class FlipStore : IWrappedKeyStore
    {
        private WrappedKeyRecord? _record;

        public bool Flip { get; set; }

        public ValueTask<WrappedKeyRecord?> ReadAsync(string scope, CancellationToken ct = default)
        {
            if (_record is null || _record.Scope != scope)
            {
                return ValueTask.FromResult<WrappedKeyRecord?>(null);
            }

            var cipher = (byte[])_record.Ciphertext.Clone();
            if (Flip)
            {
                cipher[0] ^= 0x01;
            }

            return ValueTask.FromResult<WrappedKeyRecord?>(
                new WrappedKeyRecord(scope, _record.Version, (byte[])_record.Nonce.Clone(), (byte[])_record.Tag.Clone(), cipher));
        }

        public ValueTask<bool> TryAddAsync(WrappedKeyRecord record, CancellationToken ct = default)
        {
            if (_record is not null)
            {
                return ValueTask.FromResult(false);
            }

            _record = record;
            return ValueTask.FromResult(true);
        }
    }
}
