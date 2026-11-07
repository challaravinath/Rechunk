namespace Rechunk;

public sealed class RechunkOptions
{
    /// <summary>Scope used when the caller does not pass one. Dedup never matches across scopes.</summary>
    public string DefaultScope { get; set; } = "default";

    public SimHashOptions SimHash { get; } = new();

    public MinHashOptions MinHash { get; } = new();

    /// <summary>Claims and leases, used when the pipeline has a shared fingerprint store.</summary>
    public SharedStoreOptions Store { get; } = new();

    /// <summary>Retries and dead letters, used by the ChunkIngestor.</summary>
    public FailureOptions Failures { get; } = new();

    /// <summary>Language tags, detection and the handling of text that has no spaces.</summary>
    public LanguageOptions Language { get; } = new();

    /// <summary>Settings for revision tracking and deletes.</summary>
    public RevisionOptions Revisions { get; } = new();

    /// <summary>Scanning for personal data and secrets before dedup and embedding.</summary>
    public PiiOptions Pii { get; } = new();

    /// <summary>Retries and the outbox for audit writes.</summary>
    public AuditOptions Audit { get; } = new();

    public DuplicatePolicy OnDuplicate { get; set; } = DuplicatePolicy.Alias;

    public RevisionPolicy OnRevision { get; set; } = RevisionPolicy.ReEmbedChanged;

    /// <summary>Do not merge chunks that only differ in numbers, dates or a "not".</summary>
    public bool GuardNumbersAndNegations { get; set; } = true;

    /// <summary>
    /// SimHash and MinHash skip a chunk with fewer tokens than this. The exact stage still runs.
    /// 20 is from the dotnet/docs sample: near pairs under 20 tokens were one line headings,
    /// boilerplate and short parameter lines, and one different word there was often a different fact.
    /// A token is a word from the tokenizer. In Chinese, Japanese or Thai each character is a token.
    /// </summary>
    public int NearDuplicateMinTokens { get; set; } = 20;

    /// <summary>
    /// SimHash and MinHash skip a chunk that is mostly code, an include directive, or the same kind of markup.
    /// The exact stage still runs. On by default, because those chunks share a long prefix and differ in an id.
    /// </summary>
    public bool SkipCodeLikeChunks { get; set; } = true;

    /// <summary>
    /// Do not merge a near duplicate pair when the only tokens that differ look like identifiers:
    /// CamelCase, a dotted name, an acronym, a snippet id, or a capitalized word that is not the start of a sentence.
    /// </summary>
    public bool GuardIdentifierOnlyChanges { get; set; } = true;

    /// <summary>Report what would happen but do not remember anything.</summary>
    public bool DryRun { get; set; }

    public void Validate()
    {
        if (string.IsNullOrWhiteSpace(DefaultScope))
        {
            throw new ArgumentException("DefaultScope can not be empty.", nameof(DefaultScope));
        }

        if (NearDuplicateMinTokens < 1)
        {
            throw new ArgumentOutOfRangeException(nameof(NearDuplicateMinTokens), "NearDuplicateMinTokens has to be at least 1.");
        }

        if (SimHash.MaxHammingDistance is < 0 or > 63)
        {
            throw new ArgumentOutOfRangeException(nameof(SimHash), "MaxHammingDistance has to be between 0 and 63.");
        }

        if (SimHash.ShingleSize < 1)
        {
            throw new ArgumentOutOfRangeException(nameof(SimHash), "ShingleSize has to be at least 1.");
        }

        if (SimHash.MinWords < 1)
        {
            throw new ArgumentOutOfRangeException(nameof(SimHash), "MinWords has to be at least 1.");
        }

        Language.Validate();
        Revisions.Validate();
        Pii.Validate();
        Audit.Validate();

        if (Store.LeaseDuration <= TimeSpan.Zero)
        {
            throw new ArgumentOutOfRangeException(nameof(Store), "LeaseDuration has to be above zero.");
        }

        if (Failures.MaxAttempts < 1)
        {
            throw new ArgumentOutOfRangeException(nameof(Failures), "MaxAttempts has to be at least 1.");
        }

        if (Failures.RetryDelay < TimeSpan.Zero || Failures.RetryBackoff < 1)
        {
            throw new ArgumentOutOfRangeException(nameof(Failures), "RetryDelay can not be negative and RetryBackoff has to be at least 1.");
        }

        if (MinHash.Permutations <= 0)
        {
            throw new ArgumentOutOfRangeException(nameof(MinHash), "Permutations has to be above zero.");
        }

        if (MinHash.Threshold is <= 0 or > 1)
        {
            throw new ArgumentOutOfRangeException(nameof(MinHash), "Threshold has to be above 0 and at most 1.");
        }

        if (MinHash.ShingleSize < 1)
        {
            throw new ArgumentOutOfRangeException(nameof(MinHash), "ShingleSize has to be at least 1.");
        }

        if (MinHash.MinWords < 1)
        {
            throw new ArgumentOutOfRangeException(nameof(MinHash), "MinWords has to be at least 1.");
        }

        if (MinHash.Bands.HasValue != MinHash.Rows.HasValue)
        {
            throw new ArgumentException("Set both Bands and Rows, or neither.", nameof(MinHash));
        }

        if (MinHash.Bands is { } bands && MinHash.Rows is { } rows
            && (bands < 1 || rows < 1 || (long)bands * rows > MinHash.Permutations))
        {
            throw new ArgumentException("Bands and Rows have to be at least 1, and Bands times Rows can not be more than Permutations.", nameof(MinHash));
        }
    }
}

public sealed class SimHashOptions
{
    /// <summary>Stage 2. Off by default.</summary>
    public bool Enabled { get; set; }

    /// <summary>
    /// How many of the 64 bits may differ for two chunks to count as near duplicates. Starting value, to be
    /// tuned on the benchmark. Keep it small: the index splits the hash into MaxHammingDistance + 1 slices,
    /// and above about 7 the slices get so short that lookups turn into long scans.
    /// </summary>
    public int MaxHammingDistance { get; set; } = 3;

    /// <summary>Words per shingle.</summary>
    public int ShingleSize { get; set; } = 3;

    /// <summary>
    /// Chunks with fewer words than this are only matched exactly. SimHash on a handful of words says very little.
    /// </summary>
    public int MinWords { get; set; } = 8;

    /// <summary>
    /// Only matters when MinHash is on as well. When true (the default), a SimHash match only counts if
    /// MinHash agrees that the two chunks are at least Threshold similar. When false, SimHash is trusted
    /// on its own. SimHash alone is quick but rough: a few changed words in a short chunk can look close
    /// or far by chance.
    /// </summary>
    public bool ConfirmWithMinHash { get; set; } = true;
}

public sealed class MinHashOptions
{
    /// <summary>Stage 3. Off by default.</summary>
    public bool Enabled { get; set; }

    /// <summary>Slots in the signature. More is more exact and uses more memory (4 bytes per slot per chunk).</summary>
    public int Permutations { get; set; } = 128;

    /// <summary>
    /// Jaccard similarity of the word shingles at or above which two chunks count as near duplicates.
    /// Starting value, to be tuned on your own data.
    /// </summary>
    public double Threshold { get; set; } = 0.85;

    /// <summary>Words per shingle.</summary>
    public int ShingleSize { get; set; } = 3;

    /// <summary>
    /// Chunks with fewer words than this are only matched exactly. Same idea as the SimHash setting.
    /// </summary>
    public int MinWords { get; set; } = 8;

    /// <summary>
    /// Set both Bands and Rows to pick them by hand, or leave them null to derive them from Threshold.
    /// Bands times Rows can not be more than Permutations.
    /// </summary>
    public int? Bands { get; set; }

    public int? Rows { get; set; }
}

public sealed class SharedStoreOptions
{
    /// <summary>
    /// How long a claimed chunk stays reserved. Make it longer than the slowest embedding plus vector write you
    /// expect, and much longer than the clock difference between your workers. If a worker dies, other workers can
    /// take the chunk over once this has passed.
    /// </summary>
    public TimeSpan LeaseDuration { get; set; } = TimeSpan.FromMinutes(5);
}

public sealed class FailureOptions
{
    /// <summary>Tries for one chunk, the first one included. After that it goes to the dead letter store.</summary>
    public int MaxAttempts { get; set; } = 3;

    /// <summary>Wait before the second try. Zero means no wait.</summary>
    public TimeSpan RetryDelay { get; set; } = TimeSpan.FromSeconds(1);

    /// <summary>The wait is multiplied by this for every further try.</summary>
    public double RetryBackoff { get; set; } = 2;

    /// <summary>
    /// Off by default, so a dead letter says which chunk failed and why but never holds its text.
    /// Switch it on if you want to replay without going back to the source.
    /// </summary>
    public bool StoreTextInDeadLetters { get; set; }

    /// <summary>Longest reason that is saved with a dead letter. Longer ones are cut.</summary>
    public int MaxReasonLength { get; set; } = 300;
}

public sealed class RevisionOptions
{
    /// <summary>
    /// How many times a revision is planned again when another worker changed the same document, or a chunk it wanted
    /// to reuse was being deleted, before it gives up and reports the conflict.
    /// </summary>
    public int MaxConflictRetries { get; set; } = 3;

    /// <summary>Wait before planning again after a conflict. Zero means no wait.</summary>
    public TimeSpan ConflictDelay { get; set; } = TimeSpan.FromMilliseconds(200);

    /// <summary>How long a worker owns a chunk it is deleting. If it dies, another one finishes the job after this.</summary>
    public TimeSpan RetireLease { get; set; } = TimeSpan.FromMinutes(2);

    /// <summary>Most chunks that one sweep removes in a go.</summary>
    public int SweepBatchSize { get; set; } = 100;

    internal void Validate()
    {
        if (MaxConflictRetries < 0)
        {
            throw new ArgumentOutOfRangeException(nameof(MaxConflictRetries), "MaxConflictRetries can not be negative.");
        }

        if (ConflictDelay < TimeSpan.Zero || RetireLease <= TimeSpan.Zero)
        {
            throw new ArgumentOutOfRangeException(nameof(RetireLease), "RetireLease has to be above zero and ConflictDelay can not be negative.");
        }

        if (SweepBatchSize < 1)
        {
            throw new ArgumentOutOfRangeException(nameof(SweepBatchSize), "SweepBatchSize has to be at least 1.");
        }
    }
}
