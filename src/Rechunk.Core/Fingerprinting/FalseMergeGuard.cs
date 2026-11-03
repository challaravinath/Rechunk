namespace Rechunk;

/// <summary>
/// Two chunks can be close in wording and still say different things: "the notice period is 30 days"
/// and "the notice period is 60 days", or "is allowed" and "is not allowed". SimHash cannot see that.
///
/// The guard makes one 64 bit signature per chunk from the words that carry that kind of meaning:
/// every word with a digit in it (30, 2026, v2) and every negation word (not, never, don't, ...),
/// in the order they appear. Two chunks only count as near duplicates when their signatures match.
///
/// It is on the strict side on purpose. An extra number in one of the chunks stops the merge.
/// Missing a duplicate costs one embedding call, a wrong merge serves a wrong fact.
/// </summary>
internal static class FalseMergeGuard
{
    public const string Version = "guard-v1";

    public static ulong Signature(IReadOnlyList<WordToken> tokens)
    {
        ulong signature = 0x6775617264303031UL; // "guard001", so a chunk with nothing to guard is not 0

        foreach (var token in tokens)
        {
            if (token.HasDigit || token.IsNegation)
            {
                signature = StableHash64.Combine(signature, token.Hash);
            }
        }

        return signature;
    }
}
