# PII and secret scanning

Rechunk can look at every chunk for personal data and secrets before the chunk is fingerprinted and embedded, and then flag it, mask it or block it. It is off by default.

```csharp
services.AddRechunk(o =>
{
    o.Pii.Enabled = true;
    o.Pii.Set(PiiType.ApiKey, PiiPolicy.Block);
    o.Pii.Set(PiiType.Card, PiiPolicy.Mask);
    o.Pii.Set(PiiType.Email, PiiPolicy.Flag);   // the default for every type
});
```

## What the built in detector finds

| Type | How it decides |
| --- | --- |
| `Card` | 13 to 19 digits, with or without spaces and dashes, that pass the Luhn check, start like a known card issuer and have the right length. A number near words like "order" or "invoice" and no card word is left alone |
| `ApiKey` | known token formats (AWS access key ids, GitHub tokens, `sk-` style keys, Stripe, Slack, Google, SendGrid, npm, JWTs, private key blocks, Azure storage keys), and a key name followed by a random looking value (the entropy has to be high enough, placeholders like `your-api-key` do not count) |
| `Email` | a normal address with a real looking domain |
| `Phone` | international numbers with a `+`, North American numbers with separators, and Indian mobile numbers. A bare digit run needs a word like "phone", "mobile" or "call" next to it |
| `Aadhaar` | 12 digits that pass the Verhoeff check. Grouped as 4 4 4 it is enough, a bare run of 12 needs the word "aadhaar" or "uid" near it |
| `Pan` | the Indian PAN format with a valid holder type letter |
| `Ssn` | `123-45-6789` with the area, group and serial rules (no 000, 666, 9xx), and a bare nine digits only next to "ssn" or "social security" |
| `Iban` | the country length and the mod 97 check |

It is a set of rules, not a model. It only reports a number when more than its shape matches, so order numbers, versions, dates and random digit runs are left alone. There are near miss tests for every type. What it does not find: names, addresses, free text, and numbers that have no checksum and no context. A bare Aadhaar, phone or SSN without context is missed, because reporting every nine digit number would flag a lot of order ids. Use a service for names and addresses (below).

Findings carry a confidence, 1 for the built in detector, and 0.8 for the ones that rely on a nearby word. `Pii.MinConfidence` drops findings below it.

## Policies

| Policy | What happens |
| --- | --- |
| `Flag` (default) | The finding is recorded in the audit log and the chunk goes on as it is |
| `Mask` | The found text is replaced with a placeholder (`[CARD]`, `[EMAIL]`, ...) before anything else. The chunk is fingerprinted, embedded and stored with the placeholder |
| `Block` | The chunk is not fingerprinted, claimed, embedded or stored. It is audited as `Blocked` and, in a revision, left out of the manifest |

Set a policy for one type with `Pii.Set(type, policy)`, change the default with `Pii.DefaultPolicy`, and stop reporting a type with `Pii.Ignore(type)`. When a chunk has findings with different policies, the strictest wins: one Block finding blocks the chunk, so nothing is masked into a second chance. `Pii.Placeholder` changes the placeholder text.

**Masking happens before fingerprinting.** The dedup stages see the masked text. Two chunks that differ only in an email address become the same chunk, and the second one is a duplicate of the first. That is usually what you want (the text that is embedded is the same), but it means the chunk id is the hash of the masked text. The exact text of the original is not stored anywhere.

## What the audit log gets

A finding is stored as a type and a span (start and length in the original text, in UTF-16 characters) and the policy that was applied. The value is never stored, not in the audit log, not in a dead letter and not in a log line.

A scan that fails (for example a service that is down) fails closed: the chunk is dead lettered, with the id `unscanned-<hash>` and no text, even when `Failures.StoreTextInDeadLetters` is on. It is not embedded and not stored as if it were clean. Replay tries it again, and the scan runs again.

## Using a service: Presidio or Azure AI Language

For names, addresses, and the types the rules do not cover, put an outside service behind `IPiiAnalysisClient` and wrap it in `PiiServiceDetector`. Rechunk ships the interface and the mapping only. It makes no network calls of its own and has no dependency on either service.

```csharp
public sealed class PresidioClient : IPiiAnalysisClient
{
    private readonly HttpClient _http;   // BaseAddress = your Presidio analyzer

    public PresidioClient(HttpClient http) => _http = http;

    public async ValueTask<IReadOnlyList<IReadOnlyList<PiiEntity>>> AnalyzeAsync(
        IReadOnlyList<PiiRequest> requests, CancellationToken ct = default)
    {
        var all = new List<IReadOnlyList<PiiEntity>>();
        foreach (var request in requests)
        {
            // POST /analyze with { "text": ..., "language": "en" }
            // The answer is a list of { "entity_type", "start", "end", "score" }.
            var found = await AnalyzeOneAsync(request, ct);
            all.Add(found.Select(r => new PiiEntity(r.EntityType, r.Start, r.End - r.Start, r.Score)).ToList());
        }

        return all;
    }
}

services.AddRechunk(o =>
{
    o.Pii.Enabled = true;
    o.Pii.Detector = new CompositePiiDetector(
        new BuiltInPiiDetector(),
        new PiiServiceDetector(new PresidioClient(http), minScore: 0.6, offsets: PiiOffsetUnit.CodePoints));
    o.Pii.Set(new PiiType("PERSON"), PiiPolicy.Mask);
});
```

For Azure AI Language the same shape works with the `RecognizePiiEntities` call of the Azure.AI.TextAnalytics client. Its result has `Category`, `Offset`, `Length` and `ConfidenceScore` per entity, so build `new PiiEntity(entity.Category.ToString(), entity.Offset, entity.Length, entity.ConfidenceScore)`. Those offsets are UTF-16 code units, which is the default (`PiiOffsetUnit.Utf16`).

Things to know:

- **Offsets.** .NET strings count UTF-16 code units. Presidio is written in Python and counts Unicode code points, so after an emoji or another character outside the basic plane the numbers differ. Pass `PiiOffsetUnit.CodePoints` for Presidio and the detector converts them. Entities with offsets outside the text are dropped.
- **Names.** `PiiServiceDetector.DefaultTypeMap` maps the common entity names (`CREDIT_CARD`, `EMAIL_ADDRESS`, `PHONE_NUMBER`, `IBAN_CODE`, `US_SSN`, `IN_AADHAAR`, `IN_PAN` and the Azure names) to the Rechunk types. Other names keep their own name as the type, so you can give them a policy. Pass your own map to change that.
- **Batches.** The scanner sends all the chunks of one call in one `AnalyzeAsync`, so a client can make one request. The result must have one list per text, in order, or the detector throws.
- **Privacy.** Using a service sends the chunk text to it. That is a data protection decision, not a library one. The built in detector never leaves the process.
- This was written against the documented shape of both services, with a fake client in the tests. It has not been run against a live Presidio or Azure resource.

## Your own detector

Implement `IPiiDetector.DetectAsync(text, language)` and return `PiiFinding(type, start, length, confidence)`. Return spans only. `PiiChecks.Luhn`, `PiiChecks.Verhoeff`, `PiiChecks.Iban` and `PiiChecks.Entropy` are public if you want the same checks. The scanner drops findings with an out of range span, and findings below `MinConfidence`.

## Limits

- Rules miss things. Treat this as one layer, not a guarantee that no personal data reaches the vector store.
- Existing data is not rescanned. Turning masking on does not change chunks that are already stored. Use [erasure](erasure.md) to remove them, or revise the documents again.
- Masking changes the chunk id. A document that was ingested before masking was on, and is revised after, sees its masked chunks as changed.
- `Pii.Enabled` is false by default. A library that scans everything by default would change what existing users store.
