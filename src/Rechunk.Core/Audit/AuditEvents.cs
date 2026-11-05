namespace Rechunk;

/// <summary>What the events of one ingest or revision have in common.</summary>
internal sealed record AuditContext(
    string Scope,
    DocumentId Document,
    int Revision,
    string Source,
    string Model,
    string Actor,
    DateTimeOffset At,
    DuplicatePolicy OnDuplicate);

/// <summary>Turns the result of an ingest into audit events. No chunk text goes in, only ids, types and spans.</summary>
internal static class AuditEvents
{
    public static List<AuditEvent> FromIngest(AuditContext ctx, IngestResult result, ISet<ChunkId>? replacing = null)
    {
        var events = new List<AuditEvent>();
        foreach (var o in result.Outcomes)
        {
            switch (o.Status)
            {
                case ChunkStatus.Stored when o.Matched is { } matched:
                    events.Add(Make(ctx, o, AuditKind.Flagged, "duplicate, embedded anyway because the policy is Flag", StageOf(o.Stage)) with { Related = matched });
                    break;
                case ChunkStatus.Stored:
                    var replaced = replacing is not null && replacing.Contains(o.Id);
                    events.Add(Make(
                        ctx, o, replaced ? AuditKind.Revised : AuditKind.New,
                        replaced ? "text changed at this position" : "new text", AuditStages.Embed));
                    break;
                case ChunkStatus.Duplicate:
                    var alias = o.Id != o.Target;
                    var kind = ctx.OnDuplicate == DuplicatePolicy.Skip || !alias ? AuditKind.Skipped : AuditKind.Aliased;
                    var reason = !alias ? "text was stored already" : "close to a chunk that is stored";
                    events.Add(Make(ctx, o, kind, reason, StageOf(o.Stage)) with { Related = o.Target });
                    break;
                case ChunkStatus.Blocked:
                    events.Add(Make(ctx, o, AuditKind.Blocked, "kept out by the PII policy", AuditStages.Pii));
                    break;
            }
        }

        foreach (var d in result.DeadLetters)
        {
            events.Add(new AuditEvent(d.Chunk, ctx.Document, ctx.Revision, ctx.Source, ctx.At, ctx.Model, AuditKind.DeadLettered, d.Reason)
            {
                Scope = ctx.Scope,
                Language = d.Language,
                Stage = StageName(d.Stage),
                Actor = ctx.Actor,
            });
        }

        return events;
    }

    public static AuditEvent Make(AuditContext ctx, ChunkOutcome o, AuditKind kind, string reason, string stage) =>
        new(o.Id.Value.Length == 0 ? new ChunkId("blocked:" + o.Ordinal) : o.Id, ctx.Document, ctx.Revision, ctx.Source, ctx.At, ctx.Model, kind, reason)
        {
            Scope = ctx.Scope,
            Language = o.Language,
            Stage = stage,
            Actor = ctx.Actor,
            Findings = o.Findings ?? Array.Empty<PiiHit>(),
        };

    public static string StageOf(MatchStage? stage) => stage switch
    {
        MatchStage.Exact => AuditStages.Exact,
        MatchStage.SimHash => AuditStages.SimHash,
        MatchStage.MinHash => AuditStages.MinHash,
        _ => AuditStages.Dedup,
    };

    public static string StageName(DeadLetterStage stage) => stage switch
    {
        DeadLetterStage.Dedup => AuditStages.Dedup,
        DeadLetterStage.Embed => AuditStages.Embed,
        DeadLetterStage.VectorWrite => AuditStages.VectorWrite,
        _ => AuditStages.Commit,
    };
}
