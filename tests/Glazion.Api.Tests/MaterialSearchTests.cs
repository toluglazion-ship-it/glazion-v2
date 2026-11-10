using Glazion.Api.Chemistry;
using Glazion.Api.Contracts;
using Glazion.Api.Data;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.Logging.Abstractions;

namespace Glazion.Api.Tests;

/// <summary>The materials dropdown against the real materials.json and material-catalog.json.</summary>
public sealed class MaterialSearchTests
{
    private static readonly Lazy<(ChemistryData Data, MaterialCuration Curation)> Real = new(() =>
        (ChemistryData.Load(RequiresChemistryDataFactAttribute.ApiData), MaterialCuration.Load(RequiresChemistryDataFactAttribute.ApiData)));

    private static IReadOnlyList<MaterialHit> Find(string? text, string? category = null, bool common = false, bool withAnalysis = false) =>
        MaterialSearch.Search(Real.Value.Data, Real.Value.Curation, new MaterialQuery(text, category, common, withAnalysis));

    private static UmfCalculationService Umf() =>
        new(new GlazionDbContext(new DbContextOptionsBuilder<GlazionDbContext>().UseInMemoryDatabase("matsearch-" + Guid.NewGuid()).Options),
            new ChemistryDataProvider(Real.Value.Data, Real.Value.Curation), NullLogger<UmfCalculationService>.Instance);

    [RequiresChemistryDataFact]
    public void Every_catalog_entry_names_a_material_in_the_list()
    {
        var (data, curation) = Real.Value;
        Assert.True(curation.ById.Count >= 70);
        Assert.All(curation.ById, kv => Assert.True(data.MaterialsById.ContainsKey(kv.Key), $"material-catalog.json lists '{kv.Value.Name}', which is not in materials.json"));
        Assert.All(curation.ById.Values, c => Assert.Contains(curation.Categories, k => k.Key == c.Category));
    }

    [RequiresChemistryDataTheory]
    [InlineData("epk", "EP Kaolin", "EPK")]
    [InlineData("EPK", "EP Kaolin", "EPK")]
    [InlineData("whiting", "Calcium Carbonate", "Whiting")]
    [InlineData("red iron oxide", "Iron Oxide Red", null)]
    [InlineData("rio", "Iron Oxide Red", "RIO")]
    [InlineData("3134", "Ferro Frit 3134", null)]
    [InlineData("frit 3134", "Ferro Frit 3134", null)]
    [InlineData("shell pink", "Stain 6000", "Shell Pink")]
    [InlineData("neph sy", "Nepheline Syenite", null)]
    [InlineData("custer", "Custer Feldspar", null)]
    [InlineData("silica", "Silica", null)]
    [InlineData("zircopax", "Zircopax", null)]
    [InlineData("cobalt carb", "Cobalt Carbonate", null)]
    public void The_name_a_potter_types_finds_the_right_material_first(string typed, string expected, string? alias)
    {
        var first = Find(typed)[0];
        Assert.Equal(expected, first.Material.Name);
        Assert.Equal(alias, first.MatchedAlias);
        Assert.True(first.Common);
    }

    [RequiresChemistryDataTheory]
    [InlineData("nephaline", "Nepheline Syenite")]
    [InlineData("wolastonite", "Wollastonite")]
    [InlineData("gerstly", "Gerstley Borate")]
    [InlineData("zircopx", "Zircopax")]
    public void One_typo_still_finds_it(string typed, string expected) => Assert.Equal(expected, Find(typed)[0].Material.Name);

    [RequiresChemistryDataFact]
    public void Each_material_appears_once_and_nonsense_finds_nothing()
    {
        Assert.Single(Find("ochre"), h => h.Material.Id == "ochre");
        Assert.Single(Find("stain (yellow)"), h => h.Material.Id == "stain-yellow-");
        Assert.Empty(Find("qqxzz"));
        Assert.Equal(2813, Find(null).Count);
    }

    [RequiresChemistryDataFact]
    public void Common_list_is_grouped_by_category_and_filters_combine()
    {
        var common = Find(null, common: true);
        Assert.Equal(Real.Value.Curation.ById.Count, common.Count);
        Assert.Equal("Silica", common[0].Material.Name);
        var order = Real.Value.Curation.Categories.Select(c => c.Key).ToList();
        Assert.Equal(common.Select(h => order.IndexOf(h.Category)).OrderBy(i => i), common.Select(h => order.IndexOf(h.Category)));

        Assert.All(Find(null, category: "frit"), h => Assert.Equal("frit", h.Category));
        Assert.DoesNotContain(Find("6000", withAnalysis: true), h => h.Material.Name == "Stain 6000");
        Assert.Contains(Find("6000"), h => h.Material.Name == "Stain 6000");
    }

    [RequiresChemistryDataTheory]
    [InlineData("Ferro Frit 1081", "frit")]
    [InlineData("Sparks Kaolin", "clay")]
    [InlineData("Hardwood Ash", "ash")]
    [InlineData("Stain 1051", "stain")]
    [InlineData("Iron Chromate", "colorant")]
    [InlineData("Fortispar", "feldspar")]
    [InlineData("Bangalore Quartz", "silica")]
    [InlineData("Calcium Silicate", "flux")]
    [InlineData("Dextrin", "additive")]
    [InlineData("Zirconium Dioxide", "opacifier")]
    public void Uncatalogued_materials_get_a_sensible_category(string name, string category) =>
        Assert.Equal(category, Find(name)[0].Category);

    [RequiresChemistryDataFact]
    public void Endpoint_returns_categories_aliases_and_refuses_unknown_categories()
    {
        var response = Umf().ListMaterials("epk", 5, 0);
        var first = response.Items[0];
        Assert.Equal(("EP Kaolin", "clay", true, "EPK"), (first.Name, first.Category, first.Common, first.MatchedAlias));
        Assert.Contains("Edgar Plastic Kaolin", first.Aliases!);
        Assert.Equal("silica", response.Categories![0].Key);
        Assert.Equal(400, Assert.Throws<ApiProblemException>(() => Umf().ListMaterials(null, 5, 0, category: "glass")).Status);
    }

    [RequiresChemistryDataFact]
    public void An_unresolved_alias_suggests_the_real_name_first() =>
        Assert.Equal("EP Kaolin", UmfCalculationService.Suggest(Real.Value.Data, "EPK", curation: Real.Value.Curation)[0]);
}

public sealed class RequiresChemistryDataTheoryAttribute : TheoryAttribute
{
    public RequiresChemistryDataTheoryAttribute()
    {
        var fact = new RequiresChemistryDataFactAttribute();
        if (fact.Skip is not null) Skip = fact.Skip;
    }
}
