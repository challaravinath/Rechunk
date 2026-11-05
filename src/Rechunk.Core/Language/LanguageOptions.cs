namespace Rechunk;

/// <summary>
/// How the language of a chunk is found and how text without spaces is cut up. Off by default: chunks without a tag
/// keep matching each other exactly like before.
/// </summary>
public sealed class LanguageOptions
{
    /// <summary>
    /// Used for chunks that have no tag. Setting a detector turns detection on. Leave it null together with
    /// AutoDetect false to treat untagged chunks as one group.
    /// </summary>
    public ILanguageDetector? Detector { get; set; }

    /// <summary>Use the built-in script and stop word detector for chunks that have no tag and no custom detector.</summary>
    public bool AutoDetect { get; set; }

    /// <summary>
    /// Cut tags down to the primary language, so "en-US" and "en-GB" are one language. On by default, because the same
    /// text in two regional variants is the case you want to catch as a duplicate.
    /// </summary>
    public bool PrimarySubtagOnly { get; set; } = true;

    /// <summary>
    /// Chinese, Japanese and Thai (and Lao, Khmer, Myanmar) are not written with spaces, so a whole sentence would be one
    /// word. With this on (the default) those runs are cut into single characters, and shingles are made from characters.
    /// </summary>
    public bool CharacterShingles { get; set; } = true;

    /// <summary>Characters per shingle in a run of text without spaces. Two or three works well for Chinese and Japanese.</summary>
    public int CharShingleSize { get; set; } = 3;

    /// <summary>
    /// How many characters count as one word when MinWords is checked. A chunk of Chinese needs MinWords times this many
    /// characters before SimHash and MinHash look at it.
    /// </summary>
    public int CharsPerWord { get; set; } = 3;

    internal ILanguageDetector? ActiveDetector => Detector ?? (AutoDetect ? ScriptLanguageDetector.Default : null);

    /// <summary>Tag from the caller first, then the detector. Null means untagged.</summary>
    internal string? Resolve(string? tag, ReadOnlySpan<char> text)
    {
        var chosen = LanguageTags.Normalize(tag, PrimarySubtagOnly);
        if (chosen is not null)
        {
            return chosen;
        }

        var detector = ActiveDetector;
        return detector is null ? null : LanguageTags.Normalize(detector.Detect(text), PrimarySubtagOnly);
    }

    internal void Validate()
    {
        if (CharShingleSize < 1)
        {
            throw new ArgumentOutOfRangeException(nameof(CharShingleSize), "CharShingleSize has to be at least 1.");
        }

        if (CharsPerWord < 1)
        {
            throw new ArgumentOutOfRangeException(nameof(CharsPerWord), "CharsPerWord has to be at least 1.");
        }
    }
}
