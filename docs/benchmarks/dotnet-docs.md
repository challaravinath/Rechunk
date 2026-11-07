# Dedup on a sample of dotnet/docs

One run of the real data harness on public markdown from [dotnet/docs](https://github.com/dotnet/docs). The clone is not in this repo.

## Data

| | |
| --- | --- |
| repo | https://github.com/dotnet/docs |
| commit | `82937f4b2a89a5a160719ada30fe4cdab947907a` |
| commit date | 2026-11-06 |
| files | `docs/**/*.md`, 13555 files on disk, 2500 used |
| sample | seed 2026, the shuffle in `benchmarks/Rechunk.RealData` |

The harness lists the markdown files, shuffles them with a fixed generator (seed 2026), keeps 2500, then sorts those paths so the chunk order is stable. Includes are not expanded. Each file is split on blank lines into paragraphs. A fenced code block stays one chunk. A heading with text on the next line and no blank line between them stays one chunk. A YAML block at the top of a file, and HTML comments, are dropped.

The pipeline is the normal one: exact hash, then SimHash, then MinHash. MinHash confirms a SimHash hit. The number and negation guard is on. Language detection is off, and every chunk is in one scope.

The tables under "Counts" and the first pair list are from before the short chunk, code, and identifier guards. The later sections are the same sample after those guards, with the defaults below. The SimHash distance and the MinHash threshold did not change.

## Machine

| | |
| --- | --- |
| OS | Ubuntu 24.04.5 LTS |
| CPU | 4 vCPU, Intel Xeon (KVM), family 6 model 207 |
| RAM | 15 GiB |
| SDK | .NET SDK 8.0.427 |
| build | `dotnet run -c Release` |

Times are one pass of `DeduplicateAsync` over the chunks already in memory. The first setting in the sweep was slower than the later ones. A fresh process that ran only the default setting took 1790 ms (30215 chunks per second) and produced the same counts.

## Counts

54085 chunks.

SimHash distance, with MinHash held at 0.85:

| simhash | minhash | exact | simhash | minhash | saved | saved % | left to embed | ms | chunks/s |
| --- | --- | --- | --- | --- | --- | --- | --- | --- | --- |
| 2 | 0.85 | 11610 | 108 | 155 | 11873 | 22.0% | 42212 | 1860 | 29078 |
| 3 | 0.85 | 11610 | 117 | 146 | 11873 | 22.0% | 42212 | 1310 | 41286 |
| 4 | 0.85 | 11610 | 139 | 125 | 11874 | 22.0% | 42211 | 1196 | 45222 |

MinHash threshold, with SimHash held at 3:

| simhash | minhash | exact | simhash | minhash | saved | saved % | left to embed | ms | chunks/s |
| --- | --- | --- | --- | --- | --- | --- | --- | --- | --- |
| 3 | 0.80 | 11610 | 123 | 321 | 12054 | 22.3% | 42031 | 1210 | 44698 |
| 3 | 0.85 | 11610 | 117 | 146 | 11873 | 22.0% | 42212 | 1310 | 41286 |
| 3 | 0.90 | 11610 | 99 | 48 | 11757 | 21.7% | 42328 | 1254 | 43130 |

"Left to embed" is chunks minus saved. On the default setting the harness also counted 42212 unique chunks, which matches.

Almost all of the saving is the exact stage. 8581 of the 11610 exact hits are shorter than 40 characters. The texts that came up most often, counted as extra copies after the first one:

| copies skipped | text |
| --- | --- |
| 1636 | `## See also` |
| 567 | `## Syntax` |
| 480 | `## Requirements` |
| 396 | `## Remarks` |
| 323 | `## To correct this error` |
| 320 | `## Parameters` |
| 245 | `## Example` |
| 213 | `**Library:** CorGuids.lib` |

Repeated headings are real chunks with this splitter, and skipping them is the right call. They do not say much about the near duplicate thresholds. At the default, SimHash and MinHash together skipped 263 chunks (117 + 146) out of 54085.

## Near duplicate pairs

I took a random 10 of the 263 near duplicate pairs at the default (pair seed 2026) and a random 10 at MinHash 0.80, which is the setting that skipped the most. I called a pair a true duplicate when the second chunk does not add a distinct fact, name, or code sample. A different API, event, snippet id, config location, or a different instruction is a wrong merge.

Default, SimHash 3 and MinHash 0.85. 4 of 10 true, so about 40% on this sample.

| | stage | bits | jaccard | judgment | what I saw |
| --- | --- | --- | --- | --- | --- |
| 1 | MinHash | | 0.906 | true duplicate | same hosting HRESULT table |
| 2 | SimHash | 2 | 0.938 | true duplicate | same hosting HRESULT table |
| 3 | SimHash | 3 | 0.852 | wrong merge | code include, different snippet id |
| 4 | MinHash | | 0.867 | wrong merge | code include, different snippet id |
| 5 | MinHash | | 0.883 | true duplicate | same bullet, different capitalization |
| 6 | SimHash | 0 | 0.859 | wrong merge | config path ends at `security` or continues to `transport` |
| 7 | MinHash | | 0.898 | true duplicate | same paragraph, the relative link differs |
| 8 | MinHash | | 0.922 | wrong merge | same analyzer note, only one says it is safe to suppress |
| 9 | MinHash | | 0.930 | wrong merge | code include, different snippet id |
| 10 | SimHash | 0 | 1.000 | wrong merge | editorconfig category `Style` vs `style` |

MinHash 0.80, SimHash 3. 6 of 10 true, so about 60% on this sample.

| | stage | bits | jaccard | judgment | what I saw |
| --- | --- | --- | --- | --- | --- |
| 1 | MinHash | | 0.945 | true duplicate | "Some of the messages" vs "Some messages" |
| 2 | SimHash | 0 | 1.000 | true duplicate | same sentence, bullet vs bold |
| 3 | MinHash | | 0.859 | wrong merge | code include, different snippet id |
| 4 | MinHash | | 0.836 | wrong merge | Prepared message vs Replay message |
| 5 | MinHash | | 0.898 | wrong merge | Menu control vs MenuItem control |
| 6 | SimHash | 0 | 1.000 | true duplicate | same certificate note, markup differs |
| 7 | SimHash | 0 | 1.000 | true duplicate | same parameter line, `Boolean` markup differs |
| 8 | SimHash | 0 | 1.000 | true duplicate | same footnote sentence, the marker differs |
| 9 | MinHash | | 0.867 | true duplicate | "For information" vs "For more information" |
| 10 | MinHash | | 0.867 | wrong merge | F# sample, one block includes `end` |

Ten pairs is a small sample. Several of the wrong merges are short code includes and config lines. They sit near the threshold because they share a long common prefix and differ in an id. A SimHash distance of 0 with a Jaccard estimate of 1.000 still showed up on a pair whose only difference was the case of `Style`, so a higher threshold would not have split that pair.

## After the guards

Three settings, all on by default:

| option | default | what it does |
| --- | --- | --- |
| `NearDuplicateMinTokens` | 20 | SimHash and MinHash skip a chunk with fewer tokens than this. The exact stage still runs. |
| `SkipCodeLikeChunks` | true | SimHash and MinHash skip a chunk that is mostly code, an include directive (`:::code`, `[!code`, `[!INCLUDE`), or markup of the same kind (a fenced sample, or a config breadcrumb full of backticks and `&nbsp;`). |
| `GuardIdentifierOnlyChanges` | true | A near pair is not merged when the ordinary words match and the only tokens that differ look like identifiers: CamelCase, a dotted name, an acronym, a snippet id, or a capitalized word that is not the start of a sentence. |

20 tokens is from this sample. Near pairs under 20 tokens were one line headings, boilerplate, and short parameter lines. In that range one substituted word (`context` and `thread`, `+=` and `-=`) changes the meaning, and the hash often cannot see an operator at all. The repeated HRESULT tables and the real paragraphs sit well above 20. Identical short lines still match at the exact stage.

On the same 2500 files and the same commit, the default (SimHash 3, MinHash 0.85) is now 54085 chunks, 11675 skipped (21.6%): 11610 exact, 21 SimHash, 44 MinHash, 42410 left to embed. A fresh process that ran only that setting took 1557 ms (34737 chunks per second) and produced the same counts. The sweep below was one process, and the first row is slower than the later ones.

SimHash distance, with MinHash held at 0.85:

| simhash | minhash | exact | simhash | minhash | saved | saved % | left to embed | ms | chunks/s |
| --- | --- | --- | --- | --- | --- | --- | --- | --- | --- |
| 2 | 0.85 | 11610 | 16 | 49 | 11675 | 21.6% | 42410 | 1847 | 29283 |
| 3 | 0.85 | 11610 | 21 | 44 | 11675 | 21.6% | 42410 | 1136 | 47610 |
| 4 | 0.85 | 11610 | 30 | 35 | 11675 | 21.6% | 42410 | 1112 | 48638 |

MinHash threshold, with SimHash held at 3:

| simhash | minhash | exact | simhash | minhash | saved | saved % | left to embed | ms | chunks/s |
| --- | --- | --- | --- | --- | --- | --- | --- | --- | --- |
| 3 | 0.80 | 11610 | 19 | 76 | 11705 | 21.6% | 42380 | 1075 | 50312 |
| 3 | 0.85 | 11610 | 21 | 44 | 11675 | 21.6% | 42410 | 1136 | 47610 |
| 3 | 0.90 | 11610 | 21 | 19 | 11650 | 21.5% | 42435 | 1079 | 50125 |

Exact hits are unchanged (11610, and 8581 of them shorter than 40 characters). Near matches went from 263 to 65. The code includes, fenced samples, and config breadcrumbs that made up most of the old wrong merges are not in the 65.

I took a random 20 of those 65 (pair seed 2026) and used the same rule as before. 16 of 20 true, so 80% on this sample. Before, 4 of 10 were true.

| | stage | bits | jaccard | judgment | what I saw |
| --- | --- | --- | --- | --- | --- |
| 1 | SimHash | 2 | 0.938 | true duplicate | same hosting HRESULT table |
| 2 | SimHash | 0 | 1.000 | true duplicate | same certificate note, the backticks differ |
| 3 | MinHash | | 0.906 | true duplicate | same hosting HRESULT table |
| 4 | MinHash | | 0.945 | true duplicate | same sentence, the note marker differs |
| 5 | MinHash | | 0.953 | true duplicate | "service principal name (SPN)" vs "an SPN" |
| 6 | MinHash | | 0.938 | true duplicate | same paragraph, the relative link differs |
| 7 | SimHash | 3 | 0.953 | true duplicate | same configuration row, the table markup differs |
| 8 | SimHash | 0 | 1.000 | true duplicate | same image sentence, the braces differ |
| 9 | SimHash | 0 | 1.000 | true duplicate | same sentence, a comma differs |
| 10 | MinHash | | 0.883 | wrong merge | menu controls vs slider controls |
| 11 | MinHash | | 0.914 | true duplicate | same hosting HRESULT table |
| 12 | SimHash | 0 | 1.000 | true duplicate | same sentence, `ETag` markup differs |
| 13 | MinHash | | 0.914 | wrong merge | only one says to use administrator credentials |
| 14 | MinHash | | 0.875 | wrong merge | singleton instance vs well-known instance |
| 15 | MinHash | | 0.867 | wrong merge | "than the target" vs "than the source" |
| 16 | SimHash | 0 | 1.000 | true duplicate | same sentence, the relative link differs |
| 17 | SimHash | 0 | 1.000 | true duplicate | "validation-related" vs "validation related" |
| 18 | MinHash | | 0.898 | true duplicate | same hosting HRESULT table |
| 19 | SimHash | 3 | 0.961 | true duplicate | same hosting HRESULT table |
| 20 | MinHash | | 0.859 | true duplicate | "binding" vs "binding configuration" |

The four wrong merges are template sentences. "menu" and "slider" are ordinary words, so the identifier guard does not treat them as names. The other three add or swap a real word. None of the 20 is a code include or a config path.

Embeds skipped fell from 11873 (22.0%) to 11675 (21.6%). The extra 198 chunks go to embedding. They are the short lines, the code and markup, and the pairs that only swap a name.

## Thresholds

I left the defaults at SimHash distance 3 and MinHash 0.85.

On the first run, distance 2 and distance 4 skipped the same number of chunks as distance 3, apart from one extra chunk at distance 4. MinHash has to confirm the SimHash hit, so on this sample the distance barely moves the result. MinHash 0.80 skipped 181 more chunks (22.3% instead of 22.0%). Those extra pairs include template sentences that name a different event. MinHash 0.90 skipped 116 fewer.

After the guards the distance still barely moves the result: 2, 3, and 4 all skip 11675. MinHash 0.80 skips 30 more than 0.85, and 0.90 skips 25 fewer. Neither change is big enough to retune the library default from one sample of this repo.

## Reproduce

```
git clone --depth 1 https://github.com/dotnet/docs.git
git -C dotnet-docs fetch --depth 1 origin 82937f4b2a89a5a160719ada30fe4cdab947907a
git -C dotnet-docs checkout 82937f4b2a89a5a160719ada30fe4cdab947907a

dotnet run -c Release --project benchmarks/Rechunk.RealData -- dotnet-docs/docs --sample 2500 --seed 2026 --sweep
```

The pair lists:

```
dotnet run -c Release --project benchmarks/Rechunk.RealData -- dotnet-docs/docs --sample 2500 --seed 2026 --pairs 10 --pairs-seed 2026
dotnet run -c Release --project benchmarks/Rechunk.RealData -- dotnet-docs/docs --sample 2500 --seed 2026 --simhash 3 --minhash 0.80 --pairs 10 --pairs-seed 2026
```

Any other folder of markdown works the same way. Pass the folder, and `--sample` when you do not want every file.
