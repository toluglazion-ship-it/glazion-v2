using System.Collections.Concurrent;

namespace Glazion.Api.Indexing;

public sealed record VectorRecord(string Id, float[] Values, IReadOnlyDictionary<string, object>? Metadata = null);
public sealed record VectorMatch(string Id, double Score, IReadOnlyDictionary<string, object>? Metadata);
public sealed record VectorQuery(string Namespace, float[] Vector, int TopK, VectorFilter? Filter = null, bool IncludeMetadata = true);
/// <summary>Dimension is 0 when the store spans several dimensions (one per modality index).</summary>
public sealed record VectorIndexStats(int Dimension, long TotalVectorCount, IReadOnlyDictionary<string, long> NamespaceCounts);

public enum VectorIndexErrorCode { InvalidRequest, Unauthorized, NotFound, RateLimited, Unavailable, Unknown }

public sealed class VectorIndexException(VectorIndexErrorCode code, string message, Exception? inner = null) : Exception(message, inner)
{
    public VectorIndexErrorCode Code { get; } = code;
    /// <summary>Rate limits and outages are worth retrying with backoff; everything else is a bug or bad input.</summary>
    public bool IsTransient => Code is VectorIndexErrorCode.RateLimited or VectorIndexErrorCode.Unavailable;
}

/// <summary>Provider-neutral metadata filter. Deliberately small: equality, membership, bounded ranges, conjunction.</summary>
public abstract record VectorFilter
{
    public sealed record EqualTo(string Field, object Value) : VectorFilter;
    public sealed record In(string Field, IReadOnlyList<string> Values) : VectorFilter;
    public sealed record GreaterOrEqual(string Field, double Value) : VectorFilter;
    public sealed record LessOrEqual(string Field, double Value) : VectorFilter;
    public sealed record And(IReadOnlyList<VectorFilter> Filters) : VectorFilter;

    public static VectorFilter Eq(string field, object value) => new EqualTo(field, value);
    public static VectorFilter AnyOf(string field, IEnumerable<string> values) => new In(field, values.ToList());
    public static VectorFilter Gte(string field, double value) => new GreaterOrEqual(field, value);
    public static VectorFilter Lte(string field, double value) => new LessOrEqual(field, value);
    public static VectorFilter All(params VectorFilter[] filters) => filters.Length == 1 ? filters[0] : new And(filters);

    /// <summary>Evaluates the filter against a metadata map. Used by the in-memory index and by tests of filter semantics.</summary>
    public bool Matches(IReadOnlyDictionary<string, object>? metadata)
    {
        metadata ??= new Dictionary<string, object>();
        switch (this)
        {
            case EqualTo eq:
                return metadata.TryGetValue(eq.Field, out var v) && ValueEquals(v, eq.Value);
            case In @in:
                if (!metadata.TryGetValue(@in.Field, out var iv) || iv is null) return false;
                // A list-valued field matches when any element is one of the values (Pinecone's $in on a list).
                if (iv is IEnumerable<string> list) return list.Any(x => @in.Values.Contains(x, StringComparer.Ordinal));
                return @in.Values.Contains(Convert.ToString(iv, System.Globalization.CultureInfo.InvariantCulture)!, StringComparer.Ordinal);
            case GreaterOrEqual ge:
                return metadata.TryGetValue(ge.Field, out var gv) && TryNumber(gv, out var gn) && gn >= ge.Value;
            case LessOrEqual le:
                return metadata.TryGetValue(le.Field, out var lv) && TryNumber(lv, out var ln) && ln <= le.Value;
            case And and:
                return and.Filters.All(f => f.Matches(metadata));
            default:
                throw new NotSupportedException(GetType().Name);
        }
    }

    private static bool ValueEquals(object stored, object expected)
    {
        if (stored is bool sb && expected is bool eb) return sb == eb;
        if (TryNumber(stored, out var sn) && TryNumber(expected, out var en)) return sn == en;
        return string.Equals(Convert.ToString(stored, System.Globalization.CultureInfo.InvariantCulture), Convert.ToString(expected, System.Globalization.CultureInfo.InvariantCulture), StringComparison.Ordinal);
    }

    private static bool TryNumber(object value, out double number)
    {
        switch (value)
        {
            case int i: number = i; return true;
            case long l: number = l; return true;
            case float f: number = f; return true;
            case double d: number = d; return true;
            case decimal m: number = (double)m; return true;
            default: number = 0; return false;
        }
    }
}

/// <summary>
/// The only surface the application uses to talk to a vector store (PRD §7.2 "Pinecone adapter behind IVectorIndex").
/// Namespaces separate modalities and versions; IDs are deterministic so re-running an upsert overwrites the same vector.
/// Results are candidates only: the caller re-authorises every match against PostgreSQL before returning it.
/// </summary>
public interface IVectorIndex
{
    string ProviderName { get; }
    Task UpsertAsync(string @namespace, IReadOnlyList<VectorRecord> records, CancellationToken cancellationToken);
    Task DeleteAsync(string @namespace, IReadOnlyList<string> ids, CancellationToken cancellationToken);
    Task<IReadOnlyList<VectorMatch>> QueryAsync(VectorQuery query, CancellationToken cancellationToken);
    Task<IReadOnlyDictionary<string, VectorRecord>> FetchAsync(string @namespace, IReadOnlyList<string> ids, CancellationToken cancellationToken);
    Task<VectorIndexStats> DescribeAsync(CancellationToken cancellationToken);
}

public static class VectorNamespaces
{
    public static string ImageChemistry(string chemistrySchemaVersion) => "images-chemistry-" + Safe(chemistrySchemaVersion);
    public static string ImageText(string embeddingVersion) => "images-text-" + Safe(embeddingVersion);
    public static string ImageVisual(string embeddingVersion) => "images-visual-" + Safe(embeddingVersion);

    /// <summary>Namespace names stay within [A-Za-z0-9._-] so every provider accepts them; a version like "model:2026-09" becomes "model-2026-09".</summary>
    public static string Safe(string value) => string.Concat(value.Select(c => char.IsAsciiLetterOrDigit(c) || c is '.' or '_' or '-' ? c : '-'));
}

/// <summary>Vector IDs are "{entityType}:{entityId}:{version}" so the same source/version always maps to one vector (plan §12.3).</summary>
public static class VectorIds
{
    public static string Image(Guid imageId, string version) => $"image:{imageId:D}:{version}";

    public static bool TryParse(string id, out string entityType, out Guid entityId, out string version)
    {
        entityType = string.Empty; entityId = Guid.Empty; version = string.Empty;
        var parts = id.Split(':', 3);
        if (parts.Length != 3 || !Guid.TryParse(parts[1], out entityId)) return false;
        entityType = parts[0]; version = parts[2];
        return entityType.Length > 0 && version.Length > 0;
    }
}

/// <summary>
/// Exact-search in-memory index with cosine scoring. Used for unit tests and local development without a Pinecone
/// account; it implements the same contract so application code is identical in both environments.
/// </summary>
public sealed class InMemoryVectorIndex : IVectorIndex
{
    private readonly ConcurrentDictionary<string, ConcurrentDictionary<string, VectorRecord>> _namespaces = new(StringComparer.Ordinal);
    private readonly ConcurrentDictionary<string, int> _dimensions = new(StringComparer.Ordinal);

    public string ProviderName => "InMemory";

    public Task UpsertAsync(string @namespace, IReadOnlyList<VectorRecord> records, CancellationToken cancellationToken)
    {
        var ns = _namespaces.GetOrAdd(@namespace, _ => new ConcurrentDictionary<string, VectorRecord>(StringComparer.Ordinal));
        foreach (var record in records)
        {
            if (record.Values.Length == 0) throw new VectorIndexException(VectorIndexErrorCode.InvalidRequest, $"Vector '{record.Id}' is empty.");
            // Dimension is fixed per namespace (each modality lives in its own namespace/index), not per store.
            var dimension = _dimensions.GetOrAdd(@namespace, record.Values.Length);
            if (dimension != record.Values.Length) throw new VectorIndexException(VectorIndexErrorCode.InvalidRequest, $"Vector '{record.Id}' has dimension {record.Values.Length}; namespace '{@namespace}' dimension is {dimension}.");
            ns[record.Id] = record with { Values = (float[])record.Values.Clone(), Metadata = record.Metadata is null ? null : new Dictionary<string, object>(record.Metadata, StringComparer.Ordinal) };
        }
        return Task.CompletedTask;
    }

    public Task DeleteAsync(string @namespace, IReadOnlyList<string> ids, CancellationToken cancellationToken)
    {
        if (_namespaces.TryGetValue(@namespace, out var ns)) foreach (var id in ids) ns.TryRemove(id, out _);
        return Task.CompletedTask;
    }

    public Task<IReadOnlyList<VectorMatch>> QueryAsync(VectorQuery query, CancellationToken cancellationToken)
    {
        if (query.TopK <= 0) throw new VectorIndexException(VectorIndexErrorCode.InvalidRequest, "topK must be positive.");
        if (!_namespaces.TryGetValue(query.Namespace, out var ns)) return Task.FromResult<IReadOnlyList<VectorMatch>>([]);
        var matches = ns.Values
            .Where(r => query.Filter is null || query.Filter.Matches(r.Metadata))
            .Select(r => new VectorMatch(r.Id, ChemistryVectorBuilder.CosineSimilarity(query.Vector, r.Values), query.IncludeMetadata ? r.Metadata : null))
            .OrderByDescending(m => m.Score).ThenBy(m => m.Id, StringComparer.Ordinal)
            .Take(query.TopK).ToList();
        return Task.FromResult<IReadOnlyList<VectorMatch>>(matches);
    }

    public Task<IReadOnlyDictionary<string, VectorRecord>> FetchAsync(string @namespace, IReadOnlyList<string> ids, CancellationToken cancellationToken)
    {
        var result = new Dictionary<string, VectorRecord>(StringComparer.Ordinal);
        if (_namespaces.TryGetValue(@namespace, out var ns)) foreach (var id in ids) if (ns.TryGetValue(id, out var record)) result[id] = record;
        return Task.FromResult<IReadOnlyDictionary<string, VectorRecord>>(result);
    }

    public Task<VectorIndexStats> DescribeAsync(CancellationToken cancellationToken)
    {
        var counts = _namespaces.ToDictionary(x => x.Key, x => (long)x.Value.Count, StringComparer.Ordinal);
        var dimension = _dimensions.Values.Distinct().Count() == 1 ? _dimensions.Values.First() : 0;
        return Task.FromResult(new VectorIndexStats(dimension, counts.Values.Sum(), counts));
    }
}
