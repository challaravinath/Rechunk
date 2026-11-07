# Synthetic measurement

This is made up text, not real documents, so read it as a check that the stages do what they should and not as a result you can expect on your data. It does not score answer quality, and the words are not an English document corpus. The corpus is 4000 chunks of 60 to 150 pseudo words, plus 1500 planted variants (5500 chunks in all), 300 of each kind: the same text with extra whitespace, one word changed, about 2 percent of the words changed, about 5 percent changed, and a changed number or an added "not". A planted variant should be merged when its exact Jaccard to the original is at least 0.85 and the guard does not apply, which is 900 of them. Fixed seed, so the numbers repeat.

| setting | embedding calls | saved | exact / SimHash / MinHash | precision | recall | wrong merges |
| --- | --- | --- | --- | --- | --- | --- |
| exact only | 5200 | 300 | 300 / 0 / 0 | 100.0% | 33.3% | 0 |
| exact + SimHash (3 bits) | 5098 | 402 | 300 / 102 / 0 | 99.8% | 44.6% | 1 |
| exact + SimHash (6 bits) | 4814 | 686 | 300 / 386 / 0 | 97.5% | 74.3% | 17 |
| exact + MinHash | 4666 | 834 | 300 / 0 / 534 | 99.5% | 92.2% | 4 |
| all three, SimHash trusted | 4660 | 840 | 300 / 102 / 438 | 99.4% | 92.8% | 5 |
| all three, MinHash confirms SimHash | 4663 | 837 | 300 / 99 / 438 | 99.5% | 92.6% | 4 |

What I take from it:

- SimHash with a strict distance catches few of the planted edits (80 of 300 single word edits, 21 of 300 two percent edits). Loosening it to 6 bits catches more but starts to merge things it should not.
- MinHash does most of the work: 295 of 300 single word edits and about 235 to 240 of 300 two percent edits. The misses are mostly pairs whose true similarity sits close to 0.85.
- The guard held: none of the 300 changed numbers or added "not" variants were merged in any setting.
- The wrong merges for the MinHash settings are loose variants (about 5 percent edited) whose true Jaccard is a little under 0.85 and whose estimate came out above it.
- Adding SimHash on top of MinHash saves very little here (3 more skipped chunks) and its confirmation step costs a bit of time. On this corpus MinHash alone is nearly as good. It may be different on long documents with many chunks, which this does not test.
- Timing for the whole run on the machine I used was well under a second per setting (roughly 15 to 80 microseconds per chunk including the pipeline). That is a rough figure from one run, not a benchmark.

To repeat it, from the repo root: `dotnet run -c Release --project benchmarks/Rechunk.Benchmarks -- --synthetic`

The same defaults on real markdown are in [dotnet-docs.md](dotnet-docs.md). The PAWS numbers are in [paws.md](paws.md).
