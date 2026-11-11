using System.Globalization;
using System.Security.Cryptography;

namespace Rechunk;

/// <summary>
/// One scope key: 32 random bytes, not derived from any other key.
/// Printing it shows only the version. The bytes stay with the value and are not copied into text.
/// </summary>
public readonly struct ScopeKey
{
    public const int MaterialLength = 32;

    private readonly byte[] _material;
    private readonly byte[] _ipad;
    private readonly byte[] _opad;

    public ScopeKey(int version, byte[] material)
    {
        if (version < 1)
        {
            throw new ArgumentOutOfRangeException(nameof(version), "Key version starts at 1.");
        }

        if (material is null || material.Length != MaterialLength)
        {
            throw new ArgumentException("A scope key is 32 random bytes.", nameof(material));
        }

        Version = version;
        // Own the bytes. The caller's array can be cleared or reused without changing this key.
        _material = (byte[])material.Clone();
        _ipad = new byte[64];
        _opad = new byte[64];
        _material.CopyTo(_ipad, 0);
        _material.CopyTo(_opad, 0);
        for (var i = 0; i < 64; i++)
        {
            _ipad[i] ^= 0x36;
            _opad[i] ^= 0x5c;
        }
    }

    public int Version { get; }

    public ReadOnlyMemory<byte> Material => _material;

    internal byte[] Bytes => _material;

    /// <summary>HMAC-SHA256 of a short message. The key schedule is built once with the key.</summary>
    internal void Hash(ReadOnlySpan<byte> message, Span<byte> destination)
    {
        if (_material is null || _ipad is null || _opad is null)
        {
            throw new InvalidOperationException("Scope key is not initialized.");
        }

        if (destination.Length < 32)
        {
            throw new ArgumentException("HMAC-SHA256 needs 32 bytes of output.", nameof(destination));
        }

        if (message.Length > 128)
        {
            throw new ArgumentException("This helper only hashes short messages.", nameof(message));
        }

        Span<byte> innerInput = stackalloc byte[64 + message.Length];
        _ipad.CopyTo(innerInput);
        message.CopyTo(innerInput[64..]);
        Span<byte> inner = stackalloc byte[32];
        SHA256.HashData(innerInput, inner);

        Span<byte> outerInput = stackalloc byte[96];
        _opad.CopyTo(outerInput);
        inner.CopyTo(outerInput[64..]);
        SHA256.HashData(outerInput, destination);
    }

    public override string ToString() => "scope-key-v" + Version.ToString(CultureInfo.InvariantCulture);
}
