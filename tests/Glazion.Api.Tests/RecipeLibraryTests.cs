using Glazion.Api.Contracts;
using Glazion.Api.Data;
using Glazion.Api.Domain;
using Glazion.Api.Infrastructure;
using Glazion.Api.Retrieval;
using Microsoft.EntityFrameworkCore;

namespace Glazion.Api.Tests;

public sealed class RecipeLibraryTests
{
    private const string Base = """[{"rawName":"Minspar 200","amount":16,"unit":"%"},{"rawName":"Silica","amount":84,"unit":"%"}]""";

    private sealed class FakeStorage : IR2ObjectStorage
    {
        public Task<PresignedUpload> CreatePutUrlAsync(string objectKey, string contentType, IReadOnlyDictionary<string, string>? metadata, CancellationToken ct) => throw new NotSupportedException();
        public Task<Uri> CreateGetUrlAsync(string objectKey, CancellationToken ct) => CreateGetUrlAsync(objectKey, null, ct);
        public Task<Uri> CreateGetUrlAsync(string objectKey, string? name, CancellationToken ct) => Task.FromResult(new Uri($"https://signed.example/{objectKey}?name={name}"));
        public Task<StoredObject?> HeadAsync(string objectKey, CancellationToken ct) => throw new NotSupportedException();
        public Task<Stream> OpenReadAsync(string objectKey, CancellationToken ct) => throw new NotSupportedException();
        public Task CopyAsync(string s, string d, CancellationToken ct) => throw new NotSupportedException();
        public Task DeleteAsync(string k, CancellationToken ct) => throw new NotSupportedException();
        public Task PutAsync(string k, byte[] c, string t, CancellationToken ct) => throw new NotSupportedException();
    }

    private sealed class Fixture : IAsyncDisposable
    {
        public GlazionDbContext Db { get; } = new(new DbContextOptionsBuilder<GlazionDbContext>().UseInMemoryDatabase("library-" + Guid.NewGuid()).Options);
        public BaseGlaze P01B1 { get; private set; } = null!;
        public RecipeLibraryService Service => new(Db, new FakeStorage());

        /// <summary>Base P01-B1 with variations 001 (two photos), 002 (none) and 003 (retired), plus an uncoded admin recipe.</summary>
        public async Task InitAsync()
        {
            P01B1 = new BaseGlaze { Id = Guid.CreateVersion7(), OwnerUserId = Guid.NewGuid(), ParticipantCode = "P01", BaseNumber = 1, Code = "P01-B1", Name = "Stain Spectrum ∆6 base", FormulationJson = Base };
            Db.BaseGlazes.Add(P01B1);
            Db.BaseGlazes.Add(new BaseGlaze { Id = Guid.CreateVersion7(), OwnerUserId = Guid.NewGuid(), ParticipantCode = "A03", BaseNumber = 2, Code = "A03-B2", Name = "Celadon base", FormulationJson = "[]" });
            Variation(1, "6001 Alpine Rose 2.5% ∆6", "Mason Stain 6001", 2.5m, full: true);
            Variation(2, "6001 Alpine Rose 5% ∆6", "Mason Stain 6001", 5m, full: false);
            Variation(3, "6002 retired", "Mason Stain 6002", 2.5m, full: true).IsActive = false;
            Db.Recipes.Add(new Recipe { Id = Guid.CreateVersion7(), Name = "Admin blue", RecipeJson = """{"materials":[{"material":"Silica","amount":"60"}]}""" });
            await Db.SaveChangesAsync();
        }

        public Recipe Variation(int number, string name, string stain, decimal amount, bool full)
        {
            var additions = $$"""[{"rawName":"{{stain}}","amount":{{amount}},"unit":"%"}]""";
            var recipe = new Recipe
            {
                Id = Guid.CreateVersion7(), Name = name, BaseGlazeId = P01B1.Id, RecipeNumber = number, Code = $"P01-B1-{number:000}", AdditionsJson = additions,
                RecipeJson = full ? $$"""{"full":[{"rawName":"Minspar 200","amount":16,"unit":"%"},{"rawName":"Silica","amount":84,"unit":"%"},{"rawName":"{{stain}}","amount":{{amount}},"unit":"%"}],"baseCode":"P01-B1"}""" : "{}"
            };
            Db.Recipes.Add(recipe);
            return recipe;
        }

        public ImageAsset Photo(Recipe recipe, ApprovalStatus approval, QualityStatus quality, bool verified, string atmosphere)
        {
            var asset = new ImageAsset
            {
                Id = Guid.CreateVersion7(), SourceType = ImageSourceType.Import, OriginalKey = $"prod/originals/{Guid.NewGuid():N}/source.png", DeclaredContentType = "image/png",
                DisplayKey = verified ? $"prod/derivatives/{Guid.NewGuid():N}/display.webp" : null,
                TileVariantKey = "white", ConeMin = 6, ConeMax = 6, Atmosphere = atmosphere, ClayBody = "porcelain", ApprovalStatus = approval, QualityStatus = quality
            };
            Db.ImageAssets.Add(asset);
            Db.RecipeImages.Add(new RecipeImage { RecipeId = recipe.Id, ImageId = asset.Id, Verified = verified, CreatedAt = DateTimeOffset.UtcNow.AddMinutes(verified ? 1 : 0) });
            return asset;
        }

        public ValueTask DisposeAsync() => Db.DisposeAsync();
    }

    [Fact]
    public async Task A_recipe_opens_with_its_full_materials_base_chemistry_and_approved_photos()
    {
        await using var f = new Fixture(); await f.InitAsync();
        var recipe = await f.Db.Recipes.SingleAsync(x => x.Code == "P01-B1-001");
        var oxidation = f.Photo(recipe, ApprovalStatus.Approved, QualityStatus.Passed, verified: false, "oxidation");
        var reduction = f.Photo(recipe, ApprovalStatus.Approved, QualityStatus.NeedsReview, verified: true, "reduction");
        f.Photo(recipe, ApprovalStatus.Pending, QualityStatus.Passed, verified: true, "oxidation");
        f.Photo(recipe, ApprovalStatus.Approved, QualityStatus.Failed, verified: true, "oxidation");
        f.Db.RecipeCalculationResults.Add(new RecipeCalculationResult { Id = Guid.CreateVersion7(), RecipeId = recipe.Id, CalculationProfile = "glazy-compatible-v1", CalculationVersion = "1", UmfJson = """{"SiO2":3.1}""", CreatedAt = DateTimeOffset.UtcNow.AddDays(-1) });
        f.Db.RecipeCalculationResults.Add(new RecipeCalculationResult { Id = Guid.CreateVersion7(), RecipeId = recipe.Id, CalculationProfile = "glazy-compatible-v1", CalculationVersion = "2", UmfJson = """{"SiO2":3.2,"Al2O3":0.35}""" });
        await f.Db.SaveChangesAsync();

        var detail = await f.Service.GetRecipeByCodeAsync(" p01-b1-001 ", CancellationToken.None);

        Assert.Equal(("P01-B1-001", 1), (detail.Code, detail.RecipeNumber));
        Assert.Equal(["Minspar 200", "Silica", "Mason Stain 6001"], detail.Materials.Select(x => x.RawName));
        Assert.Equal(new MaterialRow("Mason Stain 6001", 2.5m, "%"), Assert.Single(detail.Additions));
        Assert.Equal(("P01-B1", "Stain Spectrum ∆6 base", 2), (detail.Base!.Code, detail.Base.Name, detail.Base.VariationCount)); // the retired 003 is not counted
        Assert.Equal(2, detail.Base.Formulation.Count);
        Assert.Equal(("2", 3.2m), (detail.Chemistry!.CalculationVersion, detail.Chemistry.Umf["SiO2"]));                       // the latest snapshot
        Assert.Equal([reduction.Id, oxidation.Id], detail.Photos.Select(x => x.ImageId));                                        // approved only, verified first
        Assert.Equal(("reduction", "porcelain", true), (detail.Photos[0].Atmosphere, detail.Photos[0].ClayBody, detail.Photos[0].Verified));
        Assert.Contains("name=P01-B1-001.webp", detail.Photos[0].DisplayUrl.ToString());                                         // derivative, named by lab code
        Assert.Contains("name=P01-B1-001.png", detail.Photos[1].DisplayUrl.ToString());                                          // no derivative yet: the original

        Assert.Equal(detail.Id, (await f.Service.GetRecipeAsync(recipe.Id, CancellationToken.None)).Id);
    }

    [Fact]
    public async Task A_variation_without_stored_rows_shows_base_plus_additions_and_a_recipe_outside_the_register_has_no_base()
    {
        await using var f = new Fixture(); await f.InitAsync();
        var second = await f.Service.GetRecipeByCodeAsync("P01-B1-002", CancellationToken.None);
        Assert.Equal([("Minspar 200", 16m), ("Silica", 84m), ("Mason Stain 6001", 5m)], second.Materials.Select(x => (x.RawName, x.Amount)));
        Assert.Null(second.Chemistry);
        Assert.Empty(second.Photos);

        var admin = await f.Db.Recipes.SingleAsync(x => x.Name == "Admin blue");
        var detail = await f.Service.GetRecipeAsync(admin.Id, CancellationToken.None);
        Assert.Null(detail.Base);
        Assert.Equal(new MaterialRow("Silica", 60m, "%"), Assert.Single(detail.Materials));
    }

    [Fact]
    public async Task All_active_recipes_list_by_code_and_a_base_lists_its_variations_with_a_cover_photo()
    {
        await using var f = new Fixture(); await f.InitAsync();
        var first = await f.Db.Recipes.SingleAsync(x => x.Code == "P01-B1-001");
        f.Photo(first, ApprovalStatus.Approved, QualityStatus.Passed, verified: true, "reduction");
        f.Photo(first, ApprovalStatus.Approved, QualityStatus.Passed, verified: false, "oxidation");
        await f.Db.SaveChangesAsync();

        var all = await f.Service.ListRecipesAsync(null, null, null, null, CancellationToken.None);
        Assert.Equal(["P01-B1-001", "P01-B1-002", null], all.Items.Select(x => x.Code));                        // retired 003 hidden, uncoded last
        Assert.Equal(3, all.TotalCount);

        var variations = await f.Service.ListRecipesAsync(null, "p01-b1", null, 1, CancellationToken.None);
        var one = Assert.Single(variations.Items);
        Assert.Equal(("P01-B1", 2, 2), (one.BaseCode, one.PhotoCount, variations.TotalCount));
        Assert.Contains("name=P01-B1-001-thumb.png", one.CoverThumbnailUrl!.ToString());
        var next = await f.Service.ListRecipesAsync(null, "P01-B1", variations.NextCursor, 1, CancellationToken.None);
        Assert.Equal(("P01-B1-002", 0, null), (next.Items[0].Code, next.Items[0].PhotoCount, next.Items[0].CoverThumbnailUrl));
        Assert.Null(next.NextCursor);

        Assert.Equal(["P01-B1-002"], (await f.Service.ListRecipesAsync("rose 5%", null, null, null, CancellationToken.None)).Items.Select(x => x.Code));
        Assert.Equal(["P01-B1-002"], (await f.Service.ListRecipesAsync("b1-002", null, null, null, CancellationToken.None)).Items.Select(x => x.Code));
        Assert.Empty((await f.Service.ListRecipesAsync(null, "A03-B2", null, null, CancellationToken.None)).Items);
        Assert.Equal("validation_error", (await Assert.ThrowsAsync<ApiProblemException>(() => f.Service.ListRecipesAsync(null, "rose", null, null, CancellationToken.None))).Code);
    }

    [Fact]
    public async Task Bases_list_and_open_by_code()
    {
        await using var f = new Fixture(); await f.InitAsync();
        var bases = await f.Service.ListBasesAsync(null, null, null, null, CancellationToken.None);
        Assert.Equal(["A03-B2", "P01-B1"], bases.Items.Select(x => x.Code));
        Assert.Equal([0, 2], bases.Items.Select(x => x.VariationCount));
        Assert.Equal(["P01-B1"], (await f.Service.ListBasesAsync("spectrum", null, null, null, CancellationToken.None)).Items.Select(x => x.Code));
        Assert.Equal(["A03-B2"], (await f.Service.ListBasesAsync(null, "a03", null, null, CancellationToken.None)).Items.Select(x => x.Code));

        var p01 = await f.Service.GetBaseAsync("p01-b1", CancellationToken.None);
        Assert.Equal(("Stain Spectrum ∆6 base", 2, 2), (p01.Name, p01.VariationCount, p01.Formulation.Count));
    }

    [Fact]
    public async Task Retired_and_unknown_recipes_and_unknown_bases_are_not_found()
    {
        await using var f = new Fixture(); await f.InitAsync();
        var retired = await f.Db.Recipes.SingleAsync(x => x.Code == "P01-B1-003");
        Assert.Equal("recipe_code_not_found", (await Assert.ThrowsAsync<ApiProblemException>(() => f.Service.GetRecipeByCodeAsync("P01-B1-003", CancellationToken.None))).Code);
        Assert.Equal(404, (await Assert.ThrowsAsync<ApiProblemException>(() => f.Service.GetRecipeAsync(retired.Id, CancellationToken.None))).Status);
        Assert.Equal("recipe_code_not_found", (await Assert.ThrowsAsync<ApiProblemException>(() => f.Service.GetRecipeByCodeAsync("not-a-code", CancellationToken.None))).Code);
        Assert.Equal("base_not_found", (await Assert.ThrowsAsync<ApiProblemException>(() => f.Service.GetBaseAsync("P09-B9", CancellationToken.None))).Code);
        Assert.Equal("base_not_found", (await Assert.ThrowsAsync<ApiProblemException>(() => f.Service.GetBaseAsync("rose", CancellationToken.None))).Code);
    }
}
