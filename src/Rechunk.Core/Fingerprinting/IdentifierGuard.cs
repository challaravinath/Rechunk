namespace Rechunk;

/// <summary>
/// Two chunks can share every ordinary word and still name different things: "the Menu control"
/// and "the MenuItem control", or two includes whose snippet ids differ. The hashes cannot see that
/// the changed token is a name.
///
/// Each chunk gets two signatures. One is the ordinary words, in order. The other is the identifier
/// tokens, in order, with their original case. A pair is blocked only when the ordinary words match
/// and the identifiers do not, so a real wording change still merges.
/// </summary>
internal static class IdentifierGuard
{
    public const string Version = "idguard-v1";

    // A chunk with no ordinary words, or no identifiers, still has a signature that is not zero.
    private const ulong ContentSalt = 0x636f6e74656e7431UL;
    private const ulong IdentifierSalt = 0x6964656e74733121UL;

    public static void Signatures(IReadOnlyList<WordToken> tokens, out ulong content, out ulong identifiers)
    {
        content = ContentSalt;
        identifiers = IdentifierSalt;

        foreach (var token in tokens)
        {
            if (token.IsIdentifier)
            {
                identifiers = StableHash64.Combine(identifiers, token.IdentifierHash);
            }
            else
            {
                content = StableHash64.Combine(content, token.Hash);
            }
        }
    }
}
