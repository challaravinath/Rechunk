# Using the stages

```csharp
var pipeline = RechunkPipeline.CreateBuilder()
    .UseSimHash()                                  // stage 2, options can be changed in a lambda
    .UseMinHash(o => o.Threshold = 0.85)           // stage 3
    .Build();

var report = await pipeline.DeduplicateAsync("handbook", chunks);
```

`UseSimHash` and `UseMinHash` switch a stage on and let you change its options. `UseSimHashIndex` and `UseMinHashIndex` are different: they hand the pipeline your own index (for example one backed by a database) instead of the in memory one. With dependency injection set `options.SimHash.Enabled` and `options.MinHash.Enabled` inside `AddRechunk`.

A few things worth knowing:

- SimHash is quick but rough. A one word edit in a long chunk can be 2 bits away, and in a short chunk it can be 5 or more. That is why MinHash confirms SimHash by default.
- MinHash compares the word triples of two chunks. Its estimate is the share of equal slots in the two signatures, so with 128 slots it is off by about 0.04 either way. It is a good fit for "is this about 90 percent the same", not for hairline calls around the threshold.
- LSH only looks at chunks that share a band. Bands and rows are worked out from the threshold when you do not set them (9 bands of 14 rows for 128 slots and 0.85). A pair at similarity s becomes a candidate with probability 1 - (1 - s^14)^9, and the curve is steepest near (1/9)^(1/14) = 0.855. Every candidate is then checked with the real estimate, so the index can miss a pair but does not add false ones.
- A chunk that is skipped as a near duplicate is not added to the near indexes. The chunk it matched stands for it. So a long chain of small edits (A, then B close to A, then C close to B but not to A) is not followed.
- Chunks with fewer than 20 tokens (`NearDuplicateMinTokens`) are only matched exactly, and so are chunks that are mostly code, an include directive, or the same kind of markup (`SkipCodeLikeChunks`, on by default). `SimHash.MinWords` and `MinHash.MinWords` (8 words) still apply if you lower the token minimum. Case is not folded, so "Notice" and "notice" differ for the exact stage.
- A near pair is kept apart when the only tokens that differ look like identifiers (`GuardIdentifierOnlyChanges`, on by default). The number and negation guard is separate and still on.
- Two callers sending nearly the same chunk at the same moment can both be treated as new, because a chunk only becomes visible to the near duplicate stages once it is committed. Exact repeats are always settled correctly, also with many workers, when you use a shared store. The store itself is in [shared-store.md](shared-store.md).
