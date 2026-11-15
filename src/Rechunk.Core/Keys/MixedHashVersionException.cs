namespace Rechunk;

/// <summary>
/// A scope already has fingerprint rows of another hash version or key version.
/// Nothing was written. Old rows are left as they are. There is no automatic migration.
/// </summary>
public sealed class MixedHashVersionException : InvalidOperationException
{
    public MixedHashVersionException(string scope, int storedHashVersion, int storedKeyVersion, int hashVersion, int keyVersion)
        : base(
            "Scope '" + scope + "' already uses hash version " + storedHashVersion.ToString(System.Globalization.CultureInfo.InvariantCulture)
            + " and key version " + storedKeyVersion.ToString(System.Globalization.CultureInfo.InvariantCulture)
            + ". This write uses hash version " + hashVersion.ToString(System.Globalization.CultureInfo.InvariantCulture)
            + " and key version " + keyVersion.ToString(System.Globalization.CultureInfo.InvariantCulture) + ".")
    {
        Scope = scope ?? string.Empty;
        StoredHashVersion = storedHashVersion;
        StoredKeyVersion = storedKeyVersion;
        HashVersion = hashVersion;
        KeyVersion = keyVersion;
    }

    public string Scope { get; }

    public int StoredHashVersion { get; }

    public int StoredKeyVersion { get; }

    public int HashVersion { get; }

    public int KeyVersion { get; }
}
