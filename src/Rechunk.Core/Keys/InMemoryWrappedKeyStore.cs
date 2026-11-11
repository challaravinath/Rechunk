namespace Rechunk;

/// <summary>AES-GCM wrapper for one scope key. The fields are ciphertext, not the scope key.</summary>
public sealed record WrappedKeyRecord(string Scope, int Version, byte[] Nonce, byte[] Tag, byte[] Ciphertext)
{
    public override string ToString() => "wrapped-key-v" + Version.ToString(System.Globalization.CultureInfo.InvariantCulture);
}

/// <summary>
/// Where wrapped scope keys live. The in-memory store lasts for one process.
/// Rechunk.Storage.Sqlite can keep the same wrapped records in the SQLite file.
/// The store keeps copies, so a caller cannot change a record after it is saved.
/// </summary>
public interface IWrappedKeyStore
{
    ValueTask<WrappedKeyRecord?> ReadAsync(string scope, CancellationToken ct = default);

    /// <summary>Saves the record when the scope has none. False when a record was already there.</summary>
    ValueTask<bool> TryAddAsync(WrappedKeyRecord record, CancellationToken ct = default);
}

/// <summary>Wrapped keys for this process only. They go away when the process exits.</summary>
public sealed class InMemoryWrappedKeyStore : IWrappedKeyStore
{
    private readonly object _gate = new();
    private readonly Dictionary<string, WrappedKeyRecord> _records = new(StringComparer.Ordinal);

    public ValueTask<WrappedKeyRecord?> ReadAsync(string scope, CancellationToken ct = default)
    {
        ct.ThrowIfCancellationRequested();
        lock (_gate)
        {
            return ValueTask.FromResult(_records.TryGetValue(scope, out var record) ? Copy(record) : null);
        }
    }

    public ValueTask<bool> TryAddAsync(WrappedKeyRecord record, CancellationToken ct = default)
    {
        ArgumentNullException.ThrowIfNull(record);
        ct.ThrowIfCancellationRequested();
        lock (_gate)
        {
            return ValueTask.FromResult(_records.TryAdd(record.Scope, Copy(record)));
        }
    }

    private static WrappedKeyRecord Copy(WrappedKeyRecord record) =>
        new(record.Scope, record.Version, Clone(record.Nonce), Clone(record.Tag), Clone(record.Ciphertext));

    private static byte[] Clone(byte[] value) => value is null ? Array.Empty<byte>() : (byte[])value.Clone();
}
