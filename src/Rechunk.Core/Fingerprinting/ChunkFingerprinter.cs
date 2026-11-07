namespace Rechunk;

/// <summary>
/// Default fingerprinter. The exact hash is always made. SimHash and MinHash are made when they are
/// switched on in the options, and only for chunks with enough words. The text is normalized and split
/// into tokens once and shared by all of them.
/// </summary>
public sealed class ChunkFingerprinter : IChunkFingerprinter
{
    private readonly RechunkOptions _options;
    private readonly SimHasher _simHasher;
    private readonly MinHasher _minHasher;

    public ChunkFingerprinter(RechunkOptions options)
    {
        _options = options ?? throw new ArgumentNullException(nameof(options));
        _simHasher = new SimHasher(options.SimHash.ShingleSize, SimHasher.DefaultSeed, options.Language.CharShingleSize);
        _minHasher = new MinHasher(options.MinHash, options.Language.CharShingleSize);
    }

    /// <summary>
    /// Names every step that can change a stored value. Store it next to the fingerprints, and
    /// treat fingerprints with a different version as not comparable.
    /// </summary>
    public string AlgorithmVersion
    {
        get
        {
            var version = $"rechunk-fp-v1+{TextNormalizer.Version}+{ExactHasher.Version}";

            if (_options.SimHash.Enabled)
            {
                version += $"+{SimHasher.Version}-s{_options.SimHash.ShingleSize}-m{_options.SimHash.MinWords}";
            }

            if (_options.MinHash.Enabled)
            {
                var m = _options.MinHash;
                version += $"+{MinHasher.Version}-s{m.ShingleSize}-p{m.Permutations}-m{m.MinWords}";
            }

            if (_options.SimHash.Enabled || _options.MinHash.Enabled)
            {
                var language = _options.Language;
                version += language.CharacterShingles ? $"+tok-v2-c{language.CharShingleSize}-w{language.CharsPerWord}" : "+tok-v2-nochars";
            }

            if ((_options.SimHash.Enabled || _options.MinHash.Enabled) && _options.GuardNumbersAndNegations)
            {
                version += $"+{FalseMergeGuard.Version}";
            }

            if (_options.SimHash.Enabled || _options.MinHash.Enabled)
            {
                version += $"+near-t{_options.NearDuplicateMinTokens}";
                if (_options.SkipCodeLikeChunks)
                {
                    version += $"+{CodeLikeText.Version}";
                }

                if (_options.GuardIdentifierOnlyChanges)
                {
                    version += $"+{IdentifierGuard.Version}";
                }
            }

            return version;
        }
    }

    public ChunkFingerprint Compute(ReadOnlySpan<char> text)
    {
        var normalized = TextNormalizer.Normalize(text);
        var exact = ExactHasher.HashNormalized(normalized);

        ulong simHash = 0;
        ulong guard = 0;
        ulong content = 0;
        ulong identifiers = 0;
        ReadOnlyMemory<uint> minHash = default;

        var nearStage = _options.SimHash.Enabled || _options.MinHash.Enabled;
        if (nearStage && _options.SkipCodeLikeChunks && CodeLikeText.IsMostlyCodeOrMarkup(text))
        {
            return new ChunkFingerprint(exact, 0, default, 0);
        }

        if (nearStage)
        {
            var tokens = WordTokenizer.Tokenize(normalized, _options.Language.CharacterShingles);
            var units = Shingler.Units(tokens, _options.Language.CharsPerWord);
            var eligible = tokens.Count >= _options.NearDuplicateMinTokens;
            var simHashOn = _options.SimHash.Enabled && eligible && units >= _options.SimHash.MinWords;
            var minHashOn = _options.MinHash.Enabled && eligible && units >= _options.MinHash.MinWords;

            if (simHashOn)
            {
                simHash = _simHasher.Compute(tokens);
            }

            if (minHashOn)
            {
                minHash = _minHasher.Compute(tokens);
            }

            // the guards only matter for chunks that a near duplicate stage will look at
            if (simHashOn || minHashOn)
            {
                if (_options.GuardNumbersAndNegations)
                {
                    guard = FalseMergeGuard.Signature(tokens);
                }

                if (_options.GuardIdentifierOnlyChanges)
                {
                    IdentifierGuard.Signatures(tokens, out content, out identifiers);
                }
            }
        }

        return new ChunkFingerprint(exact, simHash, minHash, guard, content, identifiers);
    }
}
