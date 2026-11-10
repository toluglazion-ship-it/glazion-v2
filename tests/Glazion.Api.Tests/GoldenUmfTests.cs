using System.Text.Json;
using System.Text.Json.Nodes;
using System.Text.RegularExpressions;
using Glazion.Api.Chemistry;
using Glazion.Api.Data;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.Logging.Abstractions;

namespace Glazion.Api.Tests;

/// <summary>
/// The frontend's 1,072 golden cases (glazion-umf-handoff, Realdiamond/glazion c28ff2d), checked the way their
/// golden/verify.mjs checks an endpoint: the response serialised as the API sends it, numbers within 1e-9 relative
/// (1e-12 absolute near zero), lists in order, oxide maps with exactly the same oxides.
/// </summary>
public sealed partial class GoldenUmfTests
{
    private static readonly string GoldenFile = Path.Combine(AppContext.BaseDirectory, "ChemistryData", "golden-cases.json");
    private static readonly JsonSerializerOptions Web = new(JsonSerializerDefaults.Web);

    [GeneratedRegex(@"\.(moles|molePct|analysisPct|umf)$")] private static partial Regex OxideVector();

    [RequiresChemistryDataFact]
    public void Every_golden_case_matches_the_apps()
    {
        var cases = JsonNode.Parse(File.ReadAllText(GoldenFile))!.AsArray();
        Assert.Equal(1072, cases.Count);
        var data = ChemistryData.Load(RequiresChemistryDataFactAttribute.ApiData);
        Assert.Equal("materials-sha256:15451918e527", data.MaterialsVersion);   // byte-identical to the apps' materials.json
        using var db = new GlazionDbContext(new DbContextOptionsBuilder<GlazionDbContext>().UseInMemoryDatabase("golden-" + Guid.NewGuid()).Options);
        var umf = new UmfCalculationService(db, new ChemistryDataProvider(data), NullLogger<UmfCalculationService>.Instance);

        var failures = new List<string>();
        foreach (var c in cases)
        {
            var request = new UmfCalculateRequest(c!["rows"].Deserialize<List<RecipeRowInput>>(Web), c["cone"]?.GetValue<string>());
            var actual = JsonSerializer.SerializeToNode(umf.Calculate(request), Web)!["result"];
            var problems = Diff(c["expected"], actual, "result", []);
            if (problems.Count > 0) failures.Add($"{c["id"]} {c["name"]}: {string.Join("; ", problems.Take(3))}");
        }
        Assert.True(failures.Count == 0, $"{cases.Count - failures.Count} of {cases.Count} match.\n" + string.Join("\n", failures.Take(20)));
    }

    private static bool Close(JsonNode? actual, double expected) =>
        actual is JsonValue v && v.TryGetValue<double>(out var a) && Math.Abs(a - expected) <= Math.Max(1e-12, 1e-9 * Math.Abs(expected));

    /// <summary>verify.mjs diff(): null and missing count as equal.</summary>
    private static List<string> Diff(JsonNode? expected, JsonNode? actual, string path, List<string> output)
    {
        if (expected is null)
        {
            if (actual is not null) output.Add($"{path}: expected nothing, got {actual.ToJsonString()}");
            return output;
        }
        switch (expected)
        {
            case JsonValue ev when ev.GetValueKind() == JsonValueKind.Number:
                if (!Close(actual, ev.GetValue<double>())) output.Add($"{path}: expected {ev}, got {actual?.ToJsonString() ?? "nothing"}");
                return output;
            case JsonValue ev:
                if (actual is not JsonValue av || av.ToJsonString() != ev.ToJsonString()) output.Add($"{path}: expected {ev.ToJsonString()}, got {actual?.ToJsonString() ?? "nothing"}");
                return output;
            case JsonArray ea:
                if (actual is not JsonArray aa || aa.Count != ea.Count)
                {
                    output.Add($"{path}: expected {ea.Count} items, got {(actual is JsonArray x ? x.Count.ToString() : actual?.ToJsonString() ?? "nothing")}");
                    return output;
                }
                for (var i = 0; i < ea.Count; i++) Diff(ea[i], aa[i], $"{path}[{i}]", output);
                return output;
            case JsonObject eo:
                if (actual is not JsonObject ao)
                {
                    output.Add($"{path}: expected an object, got {actual?.ToJsonString() ?? "nothing"}");
                    return output;
                }
                if (OxideVector().IsMatch(path))
                {
                    var ek = string.Join(",", eo.Select(p => p.Key).Order(StringComparer.Ordinal));
                    var ak = string.Join(",", ao.Select(p => p.Key).Order(StringComparer.Ordinal));
                    if (ek != ak) output.Add($"{path}: expected oxides [{ek}], got [{ak}]");
                }
                foreach (var (key, value) in eo) Diff(value, ao[key], $"{path}.{key}", output);
                return output;
        }
        return output;
    }

    [RequiresChemistryDataFact]
    public void Colorant_materials_the_calculator_could_not_read_now_count()
    {
        // Chrome Oxide was listed at 131.58% Cr2O3. Manganese Dioxide (MnO2), Copper Oxide Red (Cu2O) and Lead Chromate
        // (CrO3) used oxides the calculator does not know, so their colour was left out of every UMF.
        var data = ChemistryData.Load(RequiresChemistryDataFactAttribute.ApiData);
        Assert.Equal(100, data.Materials.Single(m => m.Name == "Chrome Oxide").OxideAnalysis.Single(kv => kv.Key == "Cr2O3").Value.GetDouble());
        Assert.DoesNotContain(data.Materials, m => m.OxideAnalysis.Any(kv => kv.Key is "MnO2" or "Cu2O" or "CrO3"));

        JsonElement N(double v) => JsonSerializer.SerializeToElement(v);
        foreach (var (name, oxide) in new[] { ("Manganese Dioxide", "MnO"), ("Copper Oxide Red", "CuO"), ("Lead Chromate", "Cr2O3"), ("Chrome Oxide", "Cr2O3") })
        {
            var result = GlazeChemistry.CalculateRecipe([new("Custer Feldspar", N(40)), new("Silica", N(30)), new("Whiting", N(20)), new("EP Kaolin", N(10)), new(name, N(2))], data)!;
            Assert.True(result.Chem.Umf[oxide] > 0, name);
            Assert.DoesNotContain(result.Chem.Warnings, w => w.StartsWith("Unknown oxide", StringComparison.Ordinal));
        }
    }

    [RequiresChemistryDataFact]
    public void A_dropdown_id_resolves_to_the_entry_the_calculator_uses()
    {
        var data = ChemistryData.Load(RequiresChemistryDataFactAttribute.ApiData);
        using var db = new GlazionDbContext(new DbContextOptionsBuilder<GlazionDbContext>().UseInMemoryDatabase("golden-id-" + Guid.NewGuid()).Options);
        var umf = new UmfCalculationService(db, new ChemistryDataProvider(data), NullLogger<UmfCalculationService>.Instance);
        JsonElement N(double v) => JsonSerializer.SerializeToElement(v);

        var byName = umf.Calculate(new UmfCalculateRequest([new("Custer Feldspar", N(40)), new("Silica", N(30)), new("Calcium Carbonate", N(20)), new("EP Kaolin", N(10))], "6"));
        var byId = umf.Calculate(new UmfCalculateRequest([new(null, N(40), "custer-feldspar"), new(null, N(30), "silica"), new("ignored when an id is given", N(20), "calcium-carbonate"), new(null, N(10), "ep-kaolin"),
            new(null, N(5), "no-such-material")], "6"));

        Assert.Equal(JsonSerializer.Serialize(byName.Result, Web), JsonSerializer.Serialize(byId.Result, Web));
        var skip = Assert.Single(byId.SkippedRows);
        Assert.Equal((4, "unknown material", "no-such-material"), (skip.Index, skip.Reason, skip.Material));
        Assert.Equal("materials-sha256:15451918e527", byId.MaterialsVersion);
    }
}
