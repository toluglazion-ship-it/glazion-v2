using System.Text.Json;
using Glazion.Api.Contracts;
using Glazion.Api.Data;
using Glazion.Api.Domain;
using Glazion.Api.Infrastructure;
using Microsoft.EntityFrameworkCore;

namespace Glazion.Api.Chemistry;

/// <summary>
/// Administrators' corrections and additions to the materials list (meeting 2026-10-10: edit materials from the admin
/// instead of the backend). Rows live in MaterialOverrides and are layered over materials.json by the provider; every
/// write reloads the overlay in this process. The API runs as one replica, so that is every process.
/// </summary>
public sealed class MaterialCatalog(GlazionDbContext db, ChemistryDataProvider provider, ILogger<MaterialCatalog> logger)
{
    private static readonly JsonSerializerOptions Json = new(JsonSerializerDefaults.Web);

    /// <summary>Reads every override and hands them to the provider. Called at startup and after each write.</summary>
    public async Task<int> LoadAsync(CancellationToken cancellationToken)
    {
        var rows = await db.MaterialOverrides.AsNoTracking().OrderBy(x => x.MaterialId).ToListAsync(cancellationToken);
        provider.SetOverlay(rows.Select(ToOverlay).ToList());
        return rows.Count;
    }

    public async Task<IReadOnlyList<MaterialOverrideResponse>> ListAsync(CancellationToken cancellationToken)
    {
        var rows = await db.MaterialOverrides.AsNoTracking().OrderBy(x => x.MaterialId).ToListAsync(cancellationToken);
        var (file, version) = Data();
        return rows.Select(x => ToResponse(x, file, version)).ToList();
    }

    /// <summary>
    /// Creates or replaces the analysis for one material id. The name must belong to that id (same slug), so a
    /// correction cannot silently land on a different material.
    /// </summary>
    public async Task<MaterialOverrideResponse> UpsertAsync(string materialId, UpsertMaterialRequest request, Guid actorId, CancellationToken cancellationToken)
    {
        var (file, _) = Data();
        var name = request.Name?.Trim() ?? string.Empty;
        if (name.Length is 0 or > 255) throw ApiProblems.Validation("name", "A material name of at most 255 characters is required.");
        var id = ChemistryData.Slug(name);
        if (id.Trim('-').Length == 0) throw ApiProblems.Validation("name", "The name needs at least one letter or digit.");
        if (!string.Equals(id, materialId, StringComparison.Ordinal))
            throw ApiProblems.Validation("name", $"\"{name}\" has id \"{id}\", not \"{materialId}\". Use PUT /api/v2/admin/materials/{id}.");
        var analysis = ValidateAnalysis(request.OxideAnalysis, file);
        if (request.Loi is { } loi && (!double.IsFinite(loi) || loi < 0 || loi > 100)) throw ApiProblems.Validation("loi", "LOI must be between 0 and 100.");
        var note = string.IsNullOrWhiteSpace(request.Note) ? null : request.Note.Trim();
        if (note is { Length: > 1000 }) throw ApiProblems.Validation("note", "The note must be at most 1000 characters.");

        var row = await db.MaterialOverrides.SingleOrDefaultAsync(x => x.MaterialId == id, cancellationToken);
        if (row is null)
        {
            row = new MaterialOverride { Id = Guid.CreateVersion7(), MaterialId = id, CreatedBy = actorId };
            db.MaterialOverrides.Add(row);
        }
        row.Name = name;
        row.OxideAnalysisJson = JsonSerializer.Serialize(analysis, Json);
        row.Loi = request.Loi;
        row.Note = note;
        row.UpdatedBy = actorId;
        row.UpdatedAt = DateTimeOffset.UtcNow;
        await db.SaveChangesAsync(cancellationToken);
        await LoadAsync(cancellationToken);
        logger.LogInformation("Material {MaterialId} analysis set by {AdminId} ({Source})", id, actorId, file.MaterialsById.ContainsKey(id) ? MaterialSources.Override : MaterialSources.Custom);
        var (_, version) = Data();
        return ToResponse(row, file, version);
    }

    /// <summary>Removes the override: a file material goes back to its materials.json analysis; a custom one disappears.</summary>
    public async Task<bool> DeleteAsync(string materialId, Guid actorId, CancellationToken cancellationToken)
    {
        var row = await db.MaterialOverrides.SingleOrDefaultAsync(x => x.MaterialId == materialId, cancellationToken);
        if (row is null) return false;
        db.MaterialOverrides.Remove(row);
        await db.SaveChangesAsync(cancellationToken);
        await LoadAsync(cancellationToken);
        logger.LogInformation("Material override {MaterialId} removed by {AdminId}", materialId, actorId);
        return true;
    }

    /// <summary>
    /// materials.json entries that share an id. The calculator uses the last of them, so where their analyses differ
    /// an override should settle which one is right.
    /// </summary>
    public async Task<IReadOnlyList<MaterialDuplicateGroup>> DuplicatesAsync(CancellationToken cancellationToken)
    {
        var (file, _) = Data();
        var overridden = await db.MaterialOverrides.AsNoTracking().Select(x => x.MaterialId).ToListAsync(cancellationToken);
        return file.Materials.GroupBy(m => m.Id).Where(g => g.Count() > 1).Select(g =>
        {
            var entries = g.Select(m => new MaterialDuplicateEntry(m.Index + 1, m.Name, Numbers(m.OxideAnalysis), m.Loi, ReferenceEquals(file.MaterialsById[m.Id], m))).ToList();
            var first = JsonSerializer.Serialize(entries[0].OxideAnalysis);
            var identical = entries.All(e => JsonSerializer.Serialize(e.OxideAnalysis) == first && e.Loi == entries[0].Loi);
            return new MaterialDuplicateGroup(g.Key, identical, overridden.Contains(g.Key), entries);
        }).OrderBy(x => x.Identical).ThenBy(x => x.MaterialId, StringComparer.Ordinal).ToList();
    }

    public static IReadOnlyDictionary<string, double> Numbers(IEnumerable<KeyValuePair<string, JsonElement>> analysis)
    {
        var result = new Dictionary<string, double>(StringComparer.Ordinal);
        foreach (var (oxide, value) in analysis) result[oxide] = GlazeChemistry.ParseFloat(value);
        return result;
    }

    private static Dictionary<string, double> ValidateAnalysis(IReadOnlyDictionary<string, double>? analysis, ChemistryData file)
    {
        if (analysis is null || analysis.Count == 0) throw ApiProblems.Validation("oxideAnalysis", "Give the oxide analysis, e.g. {\"SiO2\": 68.5, \"Al2O3\": 18.2}.");
        var result = new Dictionary<string, double>(StringComparer.Ordinal);
        foreach (var (oxide, value) in analysis)
        {
            var symbol = file.MolecularWeights.Keys.FirstOrDefault(k => string.Equals(k, oxide?.Trim(), StringComparison.OrdinalIgnoreCase))
                ?? throw ApiProblems.Validation("oxideAnalysis", $"Unknown oxide \"{oxide}\". Use one of {string.Join(", ", file.MolecularWeights.Keys)}.");
            if (!double.IsFinite(value) || value < 0 || value > 100) throw ApiProblems.Validation("oxideAnalysis", $"{symbol} must be a weight % between 0 and 100.");
            if (value > 0) result[symbol] = value;
        }
        if (result.Count == 0) throw ApiProblems.Validation("oxideAnalysis", "At least one oxide must be above zero.");
        if (result.Values.Sum() > 105) throw ApiProblems.Validation("oxideAnalysis", "The oxides add up to more than 100% (with rounding allowance).");
        return result;
    }

    private (ChemistryData File, string MaterialsVersion) Data()
    {
        if (!provider.TryGetFile(out var file, out var error))
            throw new ApiProblemException(503, "chemistry_data_unavailable", "UMF calculator unavailable", error ?? "Chemistry data is not loaded.");
        provider.TryGet(out var merged, out _);
        return (file, merged.MaterialsVersion);
    }

    private static MaterialOverlayEntry ToOverlay(MaterialOverride row)
    {
        var analysis = JsonSerializer.Deserialize<Dictionary<string, double>>(row.OxideAnalysisJson) ?? [];
        return new MaterialOverlayEntry(row.MaterialId, row.Name, analysis.OrderBy(x => x.Key, StringComparer.Ordinal).Select(x => new KeyValuePair<string, JsonElement>(x.Key, JsonSerializer.SerializeToElement(x.Value))).ToList(), row.Loi);
    }

    private static MaterialOverrideResponse ToResponse(MaterialOverride row, ChemistryData file, string version) =>
        new(row.MaterialId, row.Name, JsonSerializer.Deserialize<Dictionary<string, double>>(row.OxideAnalysisJson) ?? [], row.Loi, row.Note,
            file.MaterialsById.ContainsKey(row.MaterialId), row.CreatedAt, row.UpdatedAt, version);
}
