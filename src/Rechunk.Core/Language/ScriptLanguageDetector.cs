namespace Rechunk;

/// <summary>
/// A small detector with no dependencies. It looks at the writing system first, then, for Latin text, counts common
/// function words.
///
/// It is meant as a sensible default and not as a replacement for a real detector:
///  - Scripts that map to one main language give that language: Hangul is ko, Thai is th, Greek is el, Hebrew is he,
///    Arabic is ar, Devanagari is hi, Cyrillic is ru. Han with kana is ja, Han alone is zh.
///  - Latin text is scored against short stop word lists for en, fr, de, es, it, pt and nl. It needs at least two
///    hits and a clear winner, and gives null otherwise.
///  - Mixed text takes the script that has most of the letters, as long as it has about a third of them.
///
/// Languages that share a script (Ukrainian or Bulgarian in Cyrillic, Persian or Urdu in Arabic script) come out as
/// the main one. If that matters to you, tag the chunks yourself or plug in a better detector.
/// </summary>
public sealed class ScriptLanguageDetector : ILanguageDetector
{
    private const double ScriptShare = 0.3;
    private const int MinStopWordHits = 2;

    private static readonly Dictionary<string, string[]> StopWords = new()
    {
        ["en"] = ["the", "and", "of", "to", "in", "is", "that", "for", "with", "this", "are", "was", "not", "you", "it", "on", "as", "be", "have", "from"],
        ["fr"] = ["le", "la", "les", "des", "du", "et", "est", "en", "un", "une", "que", "qui", "dans", "pour", "pas", "sur", "au", "avec", "ce", "sont", "nous", "vous", "ne"],
        ["de"] = ["der", "die", "das", "und", "ist", "nicht", "ein", "eine", "zu", "mit", "den", "von", "auf", "für", "sich", "dem", "auch", "im", "wir", "sie"],
        ["es"] = ["el", "la", "los", "las", "de", "que", "y", "en", "un", "una", "es", "por", "con", "para", "no", "se", "del", "al", "su", "como", "más"],
        ["it"] = ["il", "lo", "la", "gli", "di", "che", "e", "è", "un", "una", "per", "con", "non", "del", "della", "sono", "nel", "alla", "come", "più"],
        ["pt"] = ["o", "os", "as", "de", "que", "e", "do", "da", "em", "um", "uma", "para", "com", "não", "se", "por", "mais", "dos", "das", "são", "você"],
        ["nl"] = ["de", "het", "een", "en", "van", "is", "dat", "niet", "te", "op", "voor", "met", "zijn", "ook", "aan", "bij", "als", "maar", "wij", "we"],
    };

    private static readonly Dictionary<string, HashSet<string>> StopSets =
        StopWords.ToDictionary(p => p.Key, p => new HashSet<string>(p.Value, StringComparer.Ordinal));

    /// <summary>A shared instance, the detector keeps no state.</summary>
    public static ScriptLanguageDetector Default { get; } = new();

    public string? Detect(ReadOnlySpan<char> text)
    {
        Span<int> counts = stackalloc int[Enum.GetValues<ScriptClass>().Length];
        var letters = 0;

        for (var i = 0; i < text.Length; i++)
        {
            int cp = text[i];
            if (char.IsHighSurrogate(text[i]) && i + 1 < text.Length && char.IsLowSurrogate(text[i + 1]))
            {
                cp = char.ConvertToUtf32(text[i], text[i + 1]);
                i++;
            }

            var script = Scripts.Of(cp);
            if (script != ScriptClass.Other)
            {
                counts[(int)script]++;
                letters++;
            }
        }

        if (letters == 0)
        {
            return null;
        }

        // the dominant script, with kana counted together with Han as the Japanese writing system
        var kana = counts[(int)ScriptClass.Hiragana] + counts[(int)ScriptClass.Katakana];
        var han = counts[(int)ScriptClass.Han];
        var best = ScriptClass.Other;
        var bestCount = 0;
        foreach (var script in Enum.GetValues<ScriptClass>())
        {
            if (script == ScriptClass.Other)
            {
                continue;
            }

            var count = script switch
            {
                ScriptClass.Han => han + kana,
                ScriptClass.Hiragana or ScriptClass.Katakana => 0,
                _ => counts[(int)script],
            };

            if (count > bestCount)
            {
                best = script;
                bestCount = count;
            }
        }

        if (best == ScriptClass.Other || bestCount < letters * ScriptShare)
        {
            return null;
        }

        return best switch
        {
            ScriptClass.Latin => DetectLatin(text),
            ScriptClass.Han => kana > 0 ? "ja" : "zh",
            ScriptClass.Hangul => "ko",
            ScriptClass.Thai => "th",
            ScriptClass.Lao => "lo",
            ScriptClass.Khmer => "km",
            ScriptClass.Myanmar => "my",
            ScriptClass.Cyrillic => "ru",
            ScriptClass.Greek => "el",
            ScriptClass.Hebrew => "he",
            ScriptClass.Arabic => "ar",
            ScriptClass.Devanagari => "hi",
            _ => null,
        };
    }

    private static string? DetectLatin(ReadOnlySpan<char> text)
    {
        var scores = new Dictionary<string, int>(StopSets.Count);
        var word = new char[24];
        var length = 0;

        for (var i = 0; i <= text.Length; i++)
        {
            var c = i < text.Length ? text[i] : ' ';
            if (char.IsLetter(c))
            {
                if (length < word.Length)
                {
                    word[length++] = char.ToLowerInvariant(c);
                }
                else
                {
                    length = word.Length + 1;
                }

                continue;
            }

            if (length is > 0 and <= 24)
            {
                var token = new string(word, 0, length);
                foreach (var (language, set) in StopSets)
                {
                    if (set.Contains(token))
                    {
                        scores[language] = scores.GetValueOrDefault(language) + 1;
                    }
                }
            }

            length = 0;
        }

        if (scores.Count == 0)
        {
            return null;
        }

        var ranked = scores.OrderByDescending(p => p.Value).ThenBy(p => p.Key, StringComparer.Ordinal).ToList();
        var top = ranked[0];
        var next = ranked.Count > 1 ? ranked[1].Value : 0;

        // need a few hits and a clear lead, short texts that fit two languages stay untagged
        return top.Value >= MinStopWordHits && top.Value > next * 1.3 ? top.Key : null;
    }
}
