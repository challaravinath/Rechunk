using System.Text;
using StackExchange.Redis;

namespace Rechunk.Storage.Redis;

/// <summary>
/// Key names. The scope sits in a hash tag, so every key of one scope lands on the same Redis Cluster slot and the
/// scripts can touch several of them at once. Braces and percent signs in a scope are escaped so the tag stays intact.
/// </summary>
internal sealed class RedisKeys(string prefix)
{
    public RedisKey Exact(string scope, ReadOnlySpan<byte> hash) => $"{prefix}:{{{Tag(scope)}}}:x:{Convert.ToHexString(hash).ToLowerInvariant()}";

    public RedisKey Entry(string scope, BandKind kind, string id) => $"{prefix}:{{{Tag(scope)}}}:e{(int)kind}:{id}";

    public RedisKey Band(string scope, BandKind kind, BandKey key) =>
        $"{prefix}:{{{Tag(scope)}}}:b{(int)kind}:{key.Band}:{key.Key:x16}";

    public RedisKey Doc(string scope, string document) => $"{prefix}:{{{Tag(scope)}}}:d:{document}";

    public RedisKey Ref(string scope, string chunk) => $"{prefix}:{{{Tag(scope)}}}:r:{chunk}";

    public RedisKey Aliases(string scope, string chunk) => $"{prefix}:{{{Tag(scope)}}}:a:{chunk}";

    // the documents of a scope, a sorted set with equal scores so it can be paged by name
    public RedisKey Docs(string scope) => $"{prefix}:{{{Tag(scope)}}}:ds";

    // the documents of one source
    public RedisKey SourceDocs(string scope, string source) => $"{prefix}:{{{Tag(scope)}}}:ss:{source}";

    // the documents that have a chunk in their manifest, as the chunk itself or as the target of an alias
    public RedisKey ChunkDocs(string scope, string chunk) => $"{prefix}:{{{Tag(scope)}}}:cd:{chunk}";

    // the audit trail has one tag, it is read across scopes
    public RedisKey AuditEvent(string id) => $"{prefix}:{{audit}}:e:{id}";

    public RedisKey AuditAll => $"{prefix}:{{audit}}:a";

    public RedisKey AuditScope(string scope) => $"{prefix}:{{audit}}:sc:{scope}";

    public RedisKey AuditDocument(string document) => $"{prefix}:{{audit}}:d:{document}";

    public RedisKey AuditChunk(string chunk) => $"{prefix}:{{audit}}:c:{chunk}";

    public RedisKey AuditSource(string source) => $"{prefix}:{{audit}}:s:{source}";

    public RedisKey AuditFinding(string type) => $"{prefix}:{{audit}}:f:{type}";

    public RedisKey AuditOutbox => $"{prefix}:{{audit}}:ob";

    public RedisKey AuditOutboxQueue => $"{prefix}:{{audit}}:obq";

    public RedisKey AuditOutboxSeq => $"{prefix}:{{audit}}:obn";

    // every chunk the revision store knows
    public RedisKey RefSet(string scope) => $"{prefix}:{{{Tag(scope)}}}:rs";

    // chunks waiting for a sweep, scored by when their lease ends (0 for chunks nobody owns)
    public RedisKey Pending(string scope) => $"{prefix}:{{{Tag(scope)}}}:pend";

    // dead letters share one tag, they are listed across scopes
    public RedisKey DeadLetter(string id) => $"{prefix}:{{dl}}:r:{id}";

    public RedisKey DeadLetterAll => $"{prefix}:{{dl}}:all";

    public RedisKey DeadLetterScope(string scope) => $"{prefix}:{{dl}}:s:{Tag(scope)}";

    private static string Tag(string scope)
    {
        if (scope.IndexOfAny(['{', '}', '%']) < 0)
        {
            return scope;
        }

        var sb = new StringBuilder(scope.Length + 8);
        foreach (var c in scope)
        {
            switch (c)
            {
                case '%': sb.Append("%25"); break;
                case '{': sb.Append("%7B"); break;
                case '}': sb.Append("%7D"); break;
                default: sb.Append(c); break;
            }
        }

        return sb.ToString();
    }
}
