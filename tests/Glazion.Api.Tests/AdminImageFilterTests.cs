using Glazion.Api.Contracts;
using Glazion.Api.Data;
using Glazion.Api.Domain;
using Glazion.Api.Infrastructure;
using Microsoft.EntityFrameworkCore;

namespace Glazion.Api.Tests;

/// <summary>GET /admin/images filters: each narrows the list, and they combine.</summary>
public sealed class AdminImageFilterTests
{
    private static readonly IngestionOptions Ingestion = new();

    private sealed record Seeded(GlazionDbContext Db, ImageAsset Uploaded, ImageAsset NoStain, ImageAsset Glazy, Guid NoStainBatch);

    /// <summary>
    /// P01's upload (cone 6, porcelain, thumbnail, recipe P01-B1-001); a NO STAIN import for A03 (cone 06,
    /// stoneware, recipe A03-B2-009); a Glazy import with no clay body or cone.
    /// </summary>
    private static async Task<Seeded> SeedAsync()
    {
        var db = new GlazionDbContext(new DbContextOptionsBuilder<GlazionDbContext>().UseInMemoryDatabase("admin-images-" + Guid.NewGuid()).Options);
        var p01 = new AppUser { Id = Guid.NewGuid(), Username = "pilot_p01", ParticipantCode = "P01", Role = UserRole.User };
        var code = new CaptureCode { Id = Guid.NewGuid(), AssignedUserId = p01.Id, CodeHash = "h", MaskedSuffix = "ABCD" };
        var noStain = new ImportBatch { Id = Guid.NewGuid(), Name = "NO STAIN", CreatedBy = Guid.NewGuid() };
        var glazy = new ImportBatch { Id = Guid.NewGuid(), Name = "Glazy corpus", CreatedBy = Guid.NewGuid() };
        var upload = new Upload { Id = Guid.NewGuid(), OwnerUserId = p01.Id, CaptureCodeId = code.Id, ObjectKey = "u", OriginalFileName = "IMG_2041.jpg", ContentType = "image/jpeg" };
        var noStainRecord = new ImportRecord { Id = Guid.NewGuid(), BatchId = noStain.Id, ExternalRecordId = "NS-0117", ObjectKey = "k1", FileName = "ns_0117.jpg", DeclaredContentType = "image/jpeg" };
        var glazyRecord = new ImportRecord { Id = Guid.NewGuid(), BatchId = glazy.Id, ExternalRecordId = "glazy-555", ObjectKey = "k2", FileName = "g555.jpg", DeclaredContentType = "image/jpeg" };
        var pink = new Recipe { Id = Guid.NewGuid(), Name = "Pink satin", Code = "P01-B1-001" };
        var celadon = new Recipe { Id = Guid.NewGuid(), Name = "Celadon", Code = "A03-B2-009" };
        var glazyRecipe = new Recipe { Id = Guid.NewGuid(), Name = "Glazy 555", SourceType = RecipeSourceType.Imported };
        ImageAsset Asset(Guid? uploadId, Guid? recordId) => new()
        {
            Id = Guid.CreateVersion7(), SourceType = uploadId is null ? ImageSourceType.Import : ImageSourceType.UserUpload, UploadId = uploadId, ImportRecordId = recordId,
            OriginalKey = Guid.NewGuid().ToString("N"), DeclaredContentType = "image/jpeg", ApprovalStatus = ApprovalStatus.Approved
        };
        var uploaded = Asset(upload.Id, null);
        uploaded.ConeMin = 6; uploaded.ConeMax = 6; uploaded.ClayBody = "porcelain"; uploaded.ThumbnailKey = "thumb";
        var stained = Asset(null, noStainRecord.Id);
        stained.ConeMin = -6; stained.ClayBody = "stoneware"; stained.ApprovalStatus = ApprovalStatus.Pending;
        var glazyImage = Asset(null, glazyRecord.Id);
        db.AddRange(p01, code, noStain, glazy, upload, noStainRecord, glazyRecord, pink, celadon, glazyRecipe, uploaded, stained, glazyImage);
        db.RecipeImages.AddRange(new RecipeImage { RecipeId = pink.Id, ImageId = uploaded.Id }, new RecipeImage { RecipeId = celadon.Id, ImageId = stained.Id },
            new RecipeImage { RecipeId = glazyRecipe.Id, ImageId = glazyImage.Id });
        await db.SaveChangesAsync();
        return new Seeded(db, uploaded, stained, glazyImage, noStain.Id);
    }

    private static async Task<Guid[]> FindAsync(GlazionDbContext db, AdminImageQuery query) =>
        (await AdminImageFilter.Apply(db.ImageAssets.AsNoTracking(), query, Ingestion).Select(x => x.Id).ToListAsync()).Order().ToArray();

    private static Guid[] Ids(params ImageAsset[] assets) => assets.Select(x => x.Id).Order().ToArray();

    [Fact]
    public async Task Each_filter_narrows_the_list()
    {
        var s = await SeedAsync();
        await using var db = s.Db;

        Assert.Equal(Ids(s.Uploaded, s.NoStain, s.Glazy), await FindAsync(db, new()));
        Assert.Equal(Ids(s.NoStain), await FindAsync(db, new(RecipeCode: "a03-b2-009")));          // full lab code, any case
        Assert.Equal(Ids(s.NoStain), await FindAsync(db, new(RecipeCode: "A03-B2")));              // prefix
        Assert.Equal(Ids(s.Uploaded), await FindAsync(db, new(RecipeCode: "P01")));
        Assert.Empty(await FindAsync(db, new(RecipeCode: "A03-B2-010")));
        Assert.Equal(Ids(s.NoStain), await FindAsync(db, new(ClayBody: "Stoneware")));
        Assert.Equal(Ids(s.Glazy), await FindAsync(db, new(ClayBody: "none")));
        Assert.Equal(Ids(s.NoStain), await FindAsync(db, new(ImportBatchId: s.NoStainBatch)));
        Assert.Equal(Ids(s.Uploaded), await FindAsync(db, new(ParticipantCode: "p01")));
        Assert.Equal(Ids(s.Uploaded), await FindAsync(db, new(Cone: "6")));
        Assert.Equal(Ids(s.NoStain), await FindAsync(db, new(Cone: "06")));                         // "06" is cone 06, not 6
        Assert.Equal(Ids(s.NoStain), await FindAsync(db, new(Cone: "-6")));
        Assert.Equal(Ids(s.Uploaded), await FindAsync(db, new(HasThumbnail: true)));
        Assert.Equal(Ids(s.NoStain, s.Glazy), await FindAsync(db, new(HasThumbnail: false)));
        Assert.Equal(Ids(s.NoStain), await FindAsync(db, new(Approval: ApprovalStatus.Pending)));
    }

    [Fact]
    public async Task Text_search_looks_in_file_names_record_ids_recipe_names_and_codes()
    {
        var s = await SeedAsync();
        await using var db = s.Db;

        Assert.Equal(Ids(s.Uploaded), await FindAsync(db, new(Q: "img_2041")));       // upload file name
        Assert.Equal(Ids(s.NoStain), await FindAsync(db, new(Q: "ns-0117")));         // import record id
        Assert.Equal(Ids(s.NoStain), await FindAsync(db, new(Q: "celadon")));         // recipe name
        Assert.Equal(Ids(s.Uploaded), await FindAsync(db, new(Q: "p01-b1")));         // lab code
        Assert.Equal(Ids(s.Glazy), await FindAsync(db, new(Q: "glazy")));
    }

    [Fact]
    public async Task Filters_combine()
    {
        var s = await SeedAsync();
        await using var db = s.Db;

        Assert.Equal(Ids(s.NoStain), await FindAsync(db, new(ClayBody: "stoneware", Cone: "06", RecipeCode: "A03")));
        Assert.Empty(await FindAsync(db, new(ClayBody: "porcelain", Cone: "06")));
        Assert.Empty(await FindAsync(db, new(ParticipantCode: "P01", ImportBatchId: s.NoStainBatch)));
    }

    [Theory]
    [InlineData("glass", null, null)]
    [InlineData(null, "hot", null)]
    [InlineData(null, "30", null)]
    [InlineData(null, null, "P1")]
    public async Task Bad_filters_are_refused(string? clayBody, string? cone, string? participant)
    {
        var s = await SeedAsync();
        await using var db = s.Db;
        var ex = await Assert.ThrowsAsync<ApiProblemException>(() => FindAsync(db, new(ClayBody: clayBody, Cone: cone, ParticipantCode: participant)));
        Assert.Equal(400, ex.Status);
    }

    [Theory]
    [InlineData("6", 6)]
    [InlineData("06", -6)]
    [InlineData("-6", -6)]
    [InlineData("10", 10)]
    [InlineData("05.5", -5.5)]
    [InlineData("0.5", 0.5)]
    public void Cones_are_read_as_the_apps_write_them(string text, double expected) =>
        Assert.Equal((decimal)expected, AdminImageFilter.ParseCone(text));
}
