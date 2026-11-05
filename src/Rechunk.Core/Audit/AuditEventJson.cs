using System.Text.Json;
using System.Text.Json.Serialization;

namespace Rechunk;

/// <summary>
/// The JSON form of an audit event, used by the SQLite and Redis logs and the outboxes. It is public so a log of your own
/// can store events the same way. There is no field for chunk text, so there is nothing to leak by accident.
/// </summary>
public static class AuditEventJson
{
    public static string Serialize(AuditEvent e)
    {
        ArgumentNullException.ThrowIfNull(e);
        var dto = new AuditDto
        {
            Id = e.Id,
            Chunk = e.Chunk.Value ?? string.Empty,
            Document = e.Document.Value ?? string.Empty,
            Revision = e.Revision,
            Source = e.Source,
            At = e.At,
            Model = e.EmbeddingModel,
            Kind = (int)e.Kind,
            Reason = e.Reason,
            Scope = e.Scope,
            Language = e.Language,
            Stage = e.Stage,
            Actor = e.Actor,
            Related = e.Related?.Value,
            Findings = e.Findings.Count == 0 ? null : e.Findings.Select(f => new AuditFindingDto { T = f.Type.Name, S = f.Start, L = f.Length, P = (int)f.Applied }).ToList(),
            Counts = e.Counts.Count == 0 ? null : e.Counts.ToDictionary(kv => kv.Key, kv => kv.Value),
        };
        return JsonSerializer.Serialize(dto, AuditJsonContext.Default.AuditDto);
    }

    public static AuditEvent Deserialize(string json)
    {
        ArgumentNullException.ThrowIfNull(json);
        var dto = JsonSerializer.Deserialize(json, AuditJsonContext.Default.AuditDto) ?? throw new JsonException("Empty audit event.");
        return new AuditEvent(
            new ChunkId(dto.Chunk ?? string.Empty),
            new DocumentId(dto.Document ?? string.Empty),
            dto.Revision,
            dto.Source ?? string.Empty,
            dto.At,
            dto.Model ?? string.Empty,
            (AuditKind)dto.Kind,
            dto.Reason ?? string.Empty)
        {
            Id = dto.Id ?? throw new JsonException("Audit event without an id."),
            Scope = dto.Scope ?? string.Empty,
            Language = dto.Language,
            Stage = dto.Stage ?? string.Empty,
            Actor = dto.Actor ?? string.Empty,
            Related = dto.Related is null ? null : new ChunkId(dto.Related),
            Findings = dto.Findings is null
                ? Array.Empty<PiiHit>()
                : dto.Findings.Select(f => new PiiHit(new PiiType(f.T ?? string.Empty), f.S, f.L, (PiiPolicy)f.P)).ToList(),
            Counts = dto.Counts is null ? AuditEvent.EmptyCounts : dto.Counts,
        };
    }
}

[JsonSourceGenerationOptions(DefaultIgnoreCondition = JsonIgnoreCondition.WhenWritingNull)]
[JsonSerializable(typeof(AuditDto))]
internal sealed partial class AuditJsonContext : JsonSerializerContext
{
}

internal sealed class AuditDto
{
    public string? Id { get; set; }

    public string? Chunk { get; set; }

    public string? Document { get; set; }

    public int Revision { get; set; }

    public string? Source { get; set; }

    public DateTimeOffset At { get; set; }

    public string? Model { get; set; }

    public int Kind { get; set; }

    public string? Reason { get; set; }

    public string? Scope { get; set; }

    public string? Language { get; set; }

    public string? Stage { get; set; }

    public string? Actor { get; set; }

    public string? Related { get; set; }

    public List<AuditFindingDto>? Findings { get; set; }

    public Dictionary<string, int>? Counts { get; set; }
}

internal sealed class AuditFindingDto
{
    public string? T { get; set; }

    public int S { get; set; }

    public int L { get; set; }

    public int P { get; set; }
}
