# Near duplicate precision on PAWS

One run of the real data harness on the PAWS wiki labeled final test split. The file is not in this repo.

PAWS is built so that many pairs swap or move words and mean something else. Those pairs still share most of their words, which is where a shingle hash can merge the wrong chunks. This is a hard test for a wrong merge.

## Data

| | |
| --- | --- |
| set | PAWS-Wiki Labeled (Final), test split |
| page | https://github.com/google-research-datasets/paws |
| paper | https://arxiv.org/abs/1904.01130 |
| original file | https://storage.googleapis.com/paws/english/paws_wiki_labeled_final.tar.gz |
| copy used | Hugging Face `google-research-datasets/paws`, config `labeled_final`, split `test`, revision `161ece9501cf0a11f3e48bd356eaa82de46d6a09` |
| parquet sha256 | `ae342ff12bb84b84b95f468abf5db6cb7c7bd578271299fe9c99be75b8132f4d` |
| rows | 8000, the whole test split |
| label 1 | 3536 (paraphrase) |
| label 0 | 4464 (not a paraphrase) |

The original tarball is the file the PAWS page links, and it unpacks to `final/test.tsv`. On 7 Nov 2026 an anonymous download of that URL was denied (`storage.objects.get` on the `paws` bucket). The run used the Hugging Face copy of the same split. The parquet has the columns `id`, `sentence1`, `sentence2`, and `label`. I wrote those columns out as a TSV with that header. The harness reads that TSV. Label 1 is the human paraphrase label. Label 0 is not.

## What was measured

Each pair is its own scope. The pipeline sees the first sentence, then the second. A merge means the second sentence comes back as a duplicate of the first. A true merge is a merge whose label is 1. A false merge is a merge whose label is 0. Precision is true merges divided by all merges. Recall is true merges divided by the 3536 pairs labeled 1. Pairs are not compared with each other.

Every row uses the real pipeline: exact hash, SimHash distance 3, MinHash confirming a SimHash hit. The number and negation guard stays on.

Guards on is the library default: `NearDuplicateMinTokens` 20, `SkipCodeLikeChunks` true, `GuardIdentifierOnlyChanges` true.

Guards off sets `NearDuplicateMinTokens` to 8, which is `MinWords`, and sets `SkipCodeLikeChunks` and `GuardIdentifierOnlyChanges` to false.

MinHash 0.80 and MinHash 0.90 keep the guards on. 0.80 is one step looser than 0.85, and 0.90 is one step stricter. Those are the same steps the dotnet/docs sweep uses.

## Machine

| | |
| --- | --- |
| OS | Ubuntu 24.04.5 LTS |
| CPU | 4 vCPU, Intel Xeon, family 6 model 207 |
| RAM | 15 GiB |
| SDK | .NET SDK 8.0.427 |
| build | `dotnet run -c Release` |

The times below are one process. The first setting was slower than the later ones. A second process produced the same merge counts.

## Counts

| setting | minhash | guards | true merges | false merges | precision | recall | exact true | exact false | simhash true | simhash false | minhash true | minhash false | ms |
| --- | --- | --- | --- | --- | --- | --- | --- | --- | --- | --- | --- | --- | --- |
| defaults | 0.85 | on | 34 | 7 | 82.9% | 1.0% | 9 | 4 | 8 | 0 | 17 | 3 | 1029 |
| guards off | 0.85 | off | 101 | 11 | 90.2% | 2.9% | 9 | 4 | 27 | 3 | 65 | 4 | 648 |
| minhash 0.80 | 0.80 | on | 85 | 9 | 90.4% | 2.4% | 9 | 4 | 8 | 0 | 68 | 5 | 530 |
| minhash 0.90 | 0.90 | on | 19 | 4 | 82.6% | 0.5% | 9 | 4 | 7 | 0 | 3 | 0 | 541 |

Percentages are rounded to one decimal from the counts in the same row. The default row is 8000 labeled pairs. The pipeline merged 41 of them, 34 with label 1 and 7 with label 0. Precision 82.9% is 34 / 41: 34 of the 41 merges are pairs PAWS calls paraphrases. Recall 1.0% is 34 / 3536.

4 of those 7 wrong merges are identical strings that PAWS labels as different. 13 pairs have the same text on both sides. 9 of them are labeled 1 and 4 are labeled 0. Every setting merges all 13 at the exact stage. The 4 labeled 0 are false merges, and no threshold can split them, because the two sentences are the same string. That leaves 3 false merges at the default, all MinHash, with estimates 0.859, 0.898, and 0.852. One swaps "current" and "previous" in a forecast sentence. One changes the name Rechter to Righter. One changes "Rechter was married" to "On the right was married".

With the guards off there are 4 more false merges, 11 instead of 7. One moves "Oldham Council" and "Oldham Metropolitan Borough Council" and comes out at SimHash distance 0 and Jaccard 1.000, so the identifier guard is what keeps it apart. The other three have fewer than 20 tokens. Two of those have a Jaccard estimate of 1.000 (a swap of "southeastern" and "southern", and a comma added after "1920s"). A higher MinHash threshold would not split them. The 20 token floor does. The third changes "ridings" to "cattle" at estimate 0.883.

Recall stays low on every row. The default merges 34 of the 3536 paraphrases (1.0%). A PAWS paraphrase is a rewording, not a near copy, so most of those pairs sit below the threshold. The low recall is what that set measures.

## Pairs the near stages could see

Both sentences got a MinHash signature, so the near stages were allowed to look. Under the default that is both sides at or above 20 tokens, and not skipped as code. Recall in this table is true merges divided by the label 1 count in the subset.

| setting | pairs | label 1 | label 0 | true merges | false merges | precision | recall |
| --- | --- | --- | --- | --- | --- | --- | --- |
| defaults | 3256 | 1362 | 1894 | 28 | 4 | 87.5% | 2.1% |
| guards off | 7950 | 3510 | 4440 | 100 | 10 | 90.9% | 2.8% |
| minhash 0.80 | 3256 | 1362 | 1894 | 79 | 6 | 92.9% | 5.8% |
| minhash 0.90 | 3256 | 1362 | 1894 | 13 | 1 | 92.9% | 1.0% |

At the default, 4744 pairs have at least one sentence with no MinHash signature. Of the 7 false merges, 4 are in this subset. The other 3 are exact copies of identical strings that are too short for a near fingerprint.

## Thresholds

I left the defaults at SimHash distance 3, MinHash 0.85, and the three guards.

MinHash 0.80 finds 51 more true merges (85 instead of 34) and 2 more false merges (9 instead of 7). Both extra false merges are MinHash. MinHash 0.90 removes the 3 MinHash false merges, and MinHash true merges fall from 17 to 3. SimHash true merges fall from 8 to 7, because MinHash confirms the SimHash hit. The 4 false merges that remain are the identical strings.

That is 15 fewer true merges to remove 3 wrong near merges, on short sentences. The dotnet/docs sample was left at 0.85 for the same reason: the step was too small to retune from. Three wrong near merges out of 4464 pairs labeled 0 is not a clear reason to move the default.

Turning the guards off finds 67 more true merges and 4 more false merges. Three of those four have a Jaccard estimate of 1.000, so a stricter threshold would not replace the guards. I left the guards on.

## What this does not show

No row here scores answer quality. The sentences are English, from one labeled Wikipedia set. A pair that stays split can still be a paraphrase. Recall is not a rate of missed copies in a document corpus. The docs corpus is a separate run, in [dotnet-docs.md](dotnet-docs.md).

## Reproduce

```
curl -L -o paws_wiki_labeled_final.tar.gz https://storage.googleapis.com/paws/english/paws_wiki_labeled_final.tar.gz
tar -xzf paws_wiki_labeled_final.tar.gz

dotnet run -c Release --project benchmarks/Rechunk.RealData -- --labeled final/test.tsv
```

The run in this doc used the Hugging Face parquet, because the tarball URL was denied:

```
curl -L -o paws-test.parquet https://huggingface.co/datasets/google-research-datasets/paws/resolve/161ece9501cf0a11f3e48bd356eaa82de46d6a09/labeled_final/test-00000-of-00001.parquet
```

sha256 `ae342ff12bb84b84b95f468abf5db6cb7c7bd578271299fe9c99be75b8132f4d`. Write a TSV with the header `id`, `sentence1`, `sentence2`, `label` and pass that path to `--labeled`. `--limit` defaults to 8000, which is the whole test split. Do not commit the file.
