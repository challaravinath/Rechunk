# Languages

Rechunk can tag every chunk with a language. Chunks of different languages are never duplicates of each other, and text in scripts that are written without spaces (Chinese, Japanese, Thai and a few others) is fingerprinted by characters, so near duplicates are found there too.

## Why

Two things went wrong without this.

The first is that the same sentence in two languages can end up merged. An English chunk and its French translation have different text, so the exact stage keeps them apart, but a boilerplate line that is the same in both (a product name, a legal code, a table of numbers) matches exactly, and one of the two languages loses its vector.

The second is that SimHash and MinHash work on words. A Chinese or Thai sentence has no spaces, so the whole sentence was a single word, and one changed character changed all of it. Nothing near was ever found.

## Tagging chunks

The simplest way is to say it yourself:

```csharp
new ChunkInput(0, text, Language: "fr")
```

A tag is a short code such as `en`, `fr`, `zh` or `ja`. It is cut to the primary language, so `en-US`, `en_GB` and `EN` are all `en`. If you need regions to stay apart, set `Language.PrimarySubtagOnly = false` and `zh-Hans` and `zh-Hant` become two languages. An empty tag and `und` mean untagged. Something that is not a code (a name with spaces, for example) throws an `ArgumentException`.

If you do not tag your chunks you can let Rechunk find the language:

```csharp
services.AddRechunk(o =>
{
    o.Language.AutoDetect = true;                  // the built in detector
    // o.Language.Detector = new MyDetector();     // or your own, this turns detection on
});
```

The order is always the same: the tag on the chunk, then the detector, then untagged. With none of them set (the default) chunks stay untagged and match each other exactly as before this feature existed. Existing data keeps working.

### The built in detector

`ScriptLanguageDetector` is small and has no dependencies. It looks at the writing system first: Hangul is Korean, Thai is Thai, Greek is Greek, Hebrew, Arabic, Devanagari (Hindi) and Cyrillic (Russian) the same way, Han alone is Chinese and Han with kana is Japanese. For Latin text it counts common function words and picks English, French, German, Spanish, Italian, Portuguese or Dutch. It needs at least two hits and a clear winner, and says null when it is not sure, which leaves the chunk untagged.

It is a default and not a replacement for a real detector. Languages that share a script come out as the main one (Ukrainian as Russian, Persian as Arabic), and short Latin text is often not recognized. If that matters, tag the chunks from your document metadata, or plug in a better detector by implementing `ILanguageDetector`:

```csharp
public interface ILanguageDetector
{
    string? Detect(ReadOnlySpan<char> text);
}
```

It runs once for every untagged chunk, so keep it fast and free of side effects. Return null when you can not tell.

## What the language changes

- **Scope.** All three stages and the claims in the shared store use the scope plus the language. A French chunk is only ever compared with other French chunks. An untagged chunk is only compared with other untagged chunks.
- **Chunk id.** An untagged chunk keeps the plain hex id. A tagged one gets the language added, like `3fa9...c1.fr`. This means the id of a chunk changes once you start tagging it: the same text under `fr` is a different chunk from the untagged one, and it is embedded again the first time. The language is also stored in the manifest and in dead letters, and can be read back from the id with `LanguageTags.FromId`.
- **Revisions.** Deleting a chunk forgets its fingerprints in its own language scope, and the same text in another language is not touched.

## Scripts without spaces

The tokenizer splits text into words as before. A run of Han, Hiragana, Katakana, Thai, Lao, Khmer or Myanmar letters is cut into single characters instead, and SimHash and MinHash build shingles from those characters (3 in a row by default). Words and characters are shingled separately, so a Chinese sentence inside an English paragraph gives character shingles for the sentence and word shingles for the rest.

Text that only has words (Latin, Cyrillic, Greek and so on) is tokenized exactly as before, and the numbers that the tests pin for it have not moved.

Options:

| Option | Default | What it does |
| --- | --- | --- |
| `Language.CharacterShingles` | true | Cut runs of unspaced scripts into characters. Turn it off to get the old behavior. |
| `Language.CharShingleSize` | 3 | Characters per shingle. 2 or 3 works for Chinese and Japanese. |
| `Language.CharsPerWord` | 3 | How many characters count as one word for `MinWords`. A Chinese chunk also has to reach `NearDuplicateMinTokens`, and each character counts as one token there. |
| `Language.PrimarySubtagOnly` | true | Cut tags down to the primary language. |

The false merge guard knows Chinese and Japanese numerals (一, 二, 十, 百, 万 and so on) and a few common negations (不, 没, 无, 非, 未), so "三十天" and "六十天" are kept apart like "30 days" and "60 days".

## Limits

- Shingles of characters are a rough fit. Japanese and Chinese words are usually two or three characters, so a size of 3 is reasonable, but there is no word segmentation. A proper segmenter would find near duplicates a little better, and would be a separate package.
- Thai is cut into characters including its combining marks. That works for the near duplicate stages, which is all this is used for.
- The detector says one language per chunk. A chunk that is half English and half Chinese is tagged by the script with the most letters, as long as it has about a third of them, and otherwise left untagged. Tag the chunk yourself when you know better.
- The same text in two languages is never merged, also when you would want it to be (a bilingual document that repeats a disclaimer in both). That is the point, but it means you pay for both embeddings.
