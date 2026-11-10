using System.Globalization;
using System.Security.Cryptography;
using System.Text.Json;
using System.Text.RegularExpressions;
using Microsoft.Extensions.Options;

namespace Glazion.Api.Chemistry;

public sealed class ChemistryOptions
{
    /// <summary>Directory holding oxides.json, materials.json and glaze-limits.json, relative to the app's base directory unless rooted.</summary>
    public string DataDirectory { get; set; } = "ChemistryData";
}

/// <summary>One entry of materials.json. Oxide figures are weight % of the raw material; LOI is stored but not used.</summary>
public sealed record ChemMaterial(int Index, string Name, string Id, IReadOnlyList<KeyValuePair<string, JsonElement>> OxideAnalysis, double? Loi);

public sealed record GlazeLimit(string Cone, string Oxide, double MinUmf, double MaxUmf, string? Notes);

/// <summary>An administrator's analysis for one material id (from MaterialOverrides), applied over the file at load time.</summary>
public sealed record MaterialOverlayEntry(string MaterialId, string Name, IReadOnlyList<KeyValuePair<string, JsonElement>> OxideAnalysis, double? Loi);

public static class MaterialSources
{
    public const string File = "file";
    /// <summary>A materials.json id whose analysis an administrator replaced.</summary>
    public const string Override = "override";
    /// <summary>A material that is not in materials.json at all.</summary>
    public const string Custom = "custom";
}

/// <summary>
/// The calculator's reference data, loaded verbatim from the client's packages/chemistry files so results match the
/// browser and app to 10 decimal places. Each file is versioned by its SHA-256, and those versions are stored with
/// every calculation so a snapshot can always be traced to the exact data that produced it (UMF-03).
/// </summary>
public sealed partial class ChemistryData
{
    public const string OxidesFile = "oxides.json";
    public const string MaterialsFile = "materials.json";
    public const string LimitsFile = "glaze-limits.json";

    /// <summary>Oxide symbol → molecular weight, in oxides.ts order.</summary>
    public IReadOnlyList<KeyValuePair<string, double>> Oxides { get; }
    public IReadOnlyDictionary<string, double> MolecularWeights { get; }
    /// <summary>Materials in list order, entries with an empty name dropped.</summary>
    public IReadOnlyList<ChemMaterial> Materials { get; }
    /// <summary>Id → material. When two materials share an id, the later one in the list wins (today's behaviour).</summary>
    public IReadOnlyDictionary<string, ChemMaterial> MaterialsById { get; }
    public IReadOnlyList<GlazeLimit> Limits { get; }
    public string OxidesVersion { get; }
    public string MaterialsVersion { get; }
    public string LimitsVersion { get; }
    /// <summary>Material id → MaterialSources.Override or Custom; ids not listed come from the file.</summary>
    public IReadOnlyDictionary<string, string> OverlaySources { get; }

    public ChemistryData(IReadOnlyList<KeyValuePair<string, double>> oxides, IReadOnlyList<(string Name, IReadOnlyList<KeyValuePair<string, JsonElement>> Analysis, double? Loi)> materials, IReadOnlyList<GlazeLimit> limits,
        string oxidesVersion = "inline", string materialsVersion = "inline", string limitsVersion = "inline", IReadOnlyDictionary<string, string>? overlaySources = null)
    {
        OverlaySources = overlaySources ?? new Dictionary<string, string>();
        Oxides = oxides;
        MolecularWeights = oxides.ToDictionary(x => x.Key, x => x.Value, StringComparer.Ordinal);
        var list = new List<ChemMaterial>();
        foreach (var (name, analysis, loi) in materials)
        {
            var trimmed = (name ?? string.Empty).Trim(); // normaliseMaterials trims the name before using it anywhere
            if (trimmed.Length == 0) continue;            // entries with an empty name are dropped
            list.Add(new ChemMaterial(list.Count, trimmed, Slug(trimmed), analysis, loi));
        }
        Materials = list;
        var byId = new Dictionary<string, ChemMaterial>(StringComparer.Ordinal);
        foreach (var m in list) byId[m.Id] = m;   // later entry wins
        MaterialsById = byId;
        Limits = limits;
        OxidesVersion = oxidesVersion;
        MaterialsVersion = materialsVersion;
        LimitsVersion = limitsVersion;
    }

    public string SourceOf(string materialId) => OverlaySources.GetValueOrDefault(materialId, MaterialSources.File);

    /// <summary>
    /// The file's materials with administrators' corrections applied. An entry whose id is in the file replaces the
    /// analysis of every file entry with that id (names are kept, so recipes keep resolving and conflicting duplicates
    /// agree); other entries are appended as new materials. The materials version gains a hash of the overlay, so a
    /// stored calculation always names the exact analysis it used.
    /// </summary>
    public static ChemistryData WithOverlay(ChemistryData file, IReadOnlyList<MaterialOverlayEntry> overlay)
    {
        if (overlay.Count == 0) return file;
        var byId = overlay.ToDictionary(x => x.MaterialId, StringComparer.Ordinal);
        var sources = new Dictionary<string, string>(StringComparer.Ordinal);
        var materials = new List<(string, IReadOnlyList<KeyValuePair<string, JsonElement>>, double?)>(file.Materials.Count + overlay.Count);
        foreach (var m in file.Materials)
        {
            if (byId.TryGetValue(m.Id, out var entry)) { materials.Add((m.Name, entry.OxideAnalysis, entry.Loi)); sources[m.Id] = MaterialSources.Override; }
            else materials.Add((m.Name, m.OxideAnalysis, m.Loi));
        }
        foreach (var entry in overlay.OrderBy(x => x.MaterialId, StringComparer.Ordinal))
        {
            if (file.MaterialsById.ContainsKey(entry.MaterialId)) continue;
            materials.Add((entry.Name, entry.OxideAnalysis, entry.Loi));
            sources[entry.MaterialId] = MaterialSources.Custom;
        }
        var canonical = JsonSerializer.Serialize(overlay.OrderBy(x => x.MaterialId, StringComparer.Ordinal)
            .Select(x => new { x.MaterialId, x.Name, analysis = x.OxideAnalysis.OrderBy(a => a.Key, StringComparer.Ordinal).Select(a => new[] { a.Key, a.Value.GetRawText() }), x.Loi }));
        return new ChemistryData(file.Oxides, materials, file.Limits, file.OxidesVersion, $"{file.MaterialsVersion}+{Version("overrides", canonical)}", file.LimitsVersion, sources);
    }

    [GeneratedRegex("[^a-z0-9]+")] private static partial Regex NonAlphanumericRun();

    /// <summary>Name trimmed, lower-cased, every run of other characters turned into "-" ("Custer Feldspar" → "custer-feldspar").</summary>
    public static string Slug(string name) => NonAlphanumericRun().Replace(name.Trim().ToLowerInvariant(), "-");

    public static ChemistryData Load(string directory)
    {
        string Read(string file) => File.ReadAllText(Path.Combine(directory, file));
        var oxidesText = Read(OxidesFile);
        var materialsText = Read(MaterialsFile);
        var limitsText = Read(LimitsFile);

        var oxides = new List<KeyValuePair<string, double>>();
        using (var doc = JsonDocument.Parse(oxidesText))
        {
            if (doc.RootElement.ValueKind == JsonValueKind.Object)
                foreach (var p in doc.RootElement.EnumerateObject()) oxides.Add(new(p.Name, p.Value.GetDouble()));
            else
                foreach (var e in doc.RootElement.EnumerateArray()) oxides.Add(new(e.GetProperty("symbol").GetString()!, e.GetProperty("molecularWeight").GetDouble()));
        }

        var materials = new List<(string, IReadOnlyList<KeyValuePair<string, JsonElement>>, double?)>();
        using (var doc = JsonDocument.Parse(materialsText))
        {
            foreach (var e in doc.RootElement.EnumerateArray())
            {
                var name = e.TryGetProperty("name", out var n) && n.ValueKind == JsonValueKind.String ? n.GetString()! : string.Empty;
                var analysis = new List<KeyValuePair<string, JsonElement>>();
                if (e.TryGetProperty("oxideAnalysis", out var a) && a.ValueKind == JsonValueKind.Object)
                    foreach (var p in a.EnumerateObject()) analysis.Add(new(p.Name, p.Value.Clone())); // keeps JSON key order
                double? loi = e.TryGetProperty("loi", out var l) && l.ValueKind == JsonValueKind.Number ? l.GetDouble() : null;
                materials.Add((name, analysis, loi));
            }
        }

        var limits = new List<GlazeLimit>();
        using (var doc = JsonDocument.Parse(limitsText))
        {
            foreach (var e in doc.RootElement.EnumerateArray())
            {
                var cone = e.GetProperty("cone");
                limits.Add(new GlazeLimit(
                    cone.ValueKind == JsonValueKind.String ? cone.GetString()! : cone.GetDouble().ToString(CultureInfo.InvariantCulture),
                    e.GetProperty("oxide").GetString()!, e.GetProperty("minUmf").GetDouble(), e.GetProperty("maxUmf").GetDouble(),
                    e.TryGetProperty("notes", out var notes) && notes.ValueKind == JsonValueKind.String ? notes.GetString() : null));
            }
        }
        return new ChemistryData(oxides, materials, limits, Version("oxides", oxidesText), Version("materials", materialsText), Version("limits", limitsText));
    }

    private static string Version(string prefix, string content) => $"{prefix}-sha256:{Convert.ToHexString(SHA256.HashData(System.Text.Encoding.UTF8.GetBytes(content)))[..12].ToLowerInvariant()}";
}

/// <summary>Loads the data once. When files are missing the calculator reports itself unavailable instead of guessing.</summary>
public sealed class ChemistryDataProvider
{
    private readonly Lazy<(ChemistryData? Data, string? Error)> _data;
    private readonly Lazy<MaterialCuration> _curation;
    public string Directory { get; }
    /// <summary>material-catalog.json (categories, common list, aliases) for the dropdown; empty when absent or unreadable.</summary>
    public MaterialCuration Curation => _curation.Value;

    public ChemistryDataProvider(IOptions<ChemistryOptions> options, ILogger<ChemistryDataProvider> logger)
    {
        var dir = options.Value.DataDirectory;
        Directory = Path.IsPathRooted(dir) ? dir : Path.Combine(AppContext.BaseDirectory, dir);
        _curation = new(() =>
        {
            try { return MaterialCuration.Load(Directory); }
            catch (Exception ex) when (ex is JsonException or IOException)
            {
                logger.LogError(ex, "{File} in {Directory} could not be read; the materials list has no categories or aliases", MaterialCuration.FileName, Directory);
                return MaterialCuration.Empty;
            }
        });
        _data = new(() =>
        {
            var missing = new[] { ChemistryData.OxidesFile, ChemistryData.MaterialsFile, ChemistryData.LimitsFile }.Where(f => !File.Exists(Path.Combine(Directory, f))).ToList();
            if (missing.Count > 0)
            {
                var error = $"Chemistry data missing from {Directory}: {string.Join(", ", missing)}. Copy them from the client's packages/chemistry (tools/sync-chemistry-data.mjs).";
                logger.LogWarning("{Error}", error);
                return (null, error);
            }
            try
            {
                var data = ChemistryData.Load(Directory);
                logger.LogInformation("Chemistry data loaded: {Oxides} oxides ({OxidesVersion}), {Materials} materials ({MaterialsVersion}), {Limits} limits", data.Oxides.Count, data.OxidesVersion, data.Materials.Count, data.MaterialsVersion, data.Limits.Count);
                return (data, null);
            }
            catch (Exception ex) when (ex is JsonException or KeyNotFoundException or InvalidOperationException or FormatException)
            {
                logger.LogError(ex, "Chemistry data in {Directory} could not be parsed", Directory);
                return (null, $"Chemistry data in {Directory} could not be parsed: {ex.Message}");
            }
        });
    }

    public ChemistryDataProvider(ChemistryData data, MaterialCuration? curation = null)
    {
        Directory = "inline";
        _data = new(() => (data, null));
        _curation = new(() => curation ?? MaterialCuration.Empty);
    }

    private sealed record Merged(IReadOnlyList<MaterialOverlayEntry> Overlay, ChemistryData Data);
    private volatile IReadOnlyList<MaterialOverlayEntry> _overlay = [];
    private volatile Merged? _merged;

    /// <summary>Replaces the administrators' material overlay (MaterialOverrides). Calculations after this use it.</summary>
    public void SetOverlay(IReadOnlyList<MaterialOverlayEntry> overlay) => _overlay = overlay.ToList();

    /// <summary>The data calculations use: materials.json with the current overlay applied.</summary>
    public bool TryGet(out ChemistryData data, out string? error)
    {
        if (!TryGetFile(out var file, out error)) { data = null!; return false; }
        var overlay = _overlay;
        if (overlay.Count == 0) { data = file; return true; }
        var merged = _merged;
        if (merged is null || !ReferenceEquals(merged.Overlay, overlay))
        {
            merged = new Merged(overlay, ChemistryData.WithOverlay(file, overlay));
            _merged = merged;
        }
        data = merged.Data;
        return true;
    }

    /// <summary>materials.json exactly as loaded, without the overlay (for reviewing the file's own entries).</summary>
    public bool TryGetFile(out ChemistryData data, out string? error)
    {
        var (d, e) = _data.Value;
        data = d!;
        error = e;
        return d is not null;
    }
}
