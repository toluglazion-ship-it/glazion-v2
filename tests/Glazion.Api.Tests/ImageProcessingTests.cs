using Microsoft.EntityFrameworkCore;
using Glazion.Api.Infrastructure;
using SkiaSharp;

namespace Glazion.Api.Tests;

public sealed class ImageProcessingTests
{
    private static readonly DerivativeOptions Options = new();

    internal static byte[] Jpeg(int width, int height, Func<int, int, SKColor> pixel, int quality = 92)
    {
        using var bitmap = new SKBitmap(width, height);
        var pixels = new SKColor[width * height];
        for (var y = 0; y < height; y++)
            for (var x = 0; x < width; x++)
                pixels[y * width + x] = pixel(x, y);
        bitmap.Pixels = pixels;
        using var image = SKImage.FromBitmap(bitmap);
        return image.Encode(SKEncodedImageFormat.Jpeg, quality).ToArray();
    }

    /// <summary>A textured mid-grey tile: plenty of edges, normal exposure.</summary>
    internal static byte[] SharpTile(int width = 1600, int height = 1200) =>
        Jpeg(width, height, (x, y) => ((x / 6) + (y / 6)) % 2 == 0 ? new SKColor(70, 90, 110) : new SKColor(180, 170, 160));

    /// <summary>Inserts an EXIF APP1 segment with the given orientation right after SOI.</summary>
    private static byte[] WithOrientation(byte[] jpeg, ushort orientation)
    {
        byte[] tiff = [0x4D, 0x4D, 0x00, 0x2A, 0x00, 0x00, 0x00, 0x08, 0x00, 0x01, 0x01, 0x12, 0x00, 0x03, 0x00, 0x00, 0x00, 0x01, (byte)(orientation >> 8), (byte)orientation, 0x00, 0x00, 0x00, 0x00, 0x00, 0x00];
        var payload = new List<byte>("Exif\0\0"u8.ToArray());
        payload.AddRange(tiff);
        var length = payload.Count + 2;
        var segment = new List<byte> { 0xFF, 0xE1, (byte)(length >> 8), (byte)length };
        segment.AddRange(payload);
        return [.. jpeg[..2], .. segment, .. jpeg[2..]];
    }

    private static bool IsWebP(byte[] b) => b.Length > 12 && b[0] == 'R' && b[1] == 'I' && b[2] == 'F' && b[3] == 'F' && b[8] == 'W' && b[9] == 'E' && b[10] == 'B' && b[11] == 'P';

    /// <summary>A lossless WebP has a VP8L chunk; a lossy one VP8 (either may follow a VP8X header).</summary>
    private static bool IsLossless(byte[] webp) => System.Text.Encoding.ASCII.GetString(webp, 0, Math.Min(webp.Length, 64)).Contains("VP8L");

    private static SKSizeI SizeOf(byte[] encoded) { using var codec = SKCodec.Create(SKData.CreateCopy(encoded)); return new SKSizeI(codec.Info.Width, codec.Info.Height); }

    [Fact]
    public void Large_originals_get_bounded_webp_derivatives()
    {
        var result = ImageProcessor.Process(SharpTile(3000, 2000), Options);

        Assert.True(IsWebP(result.Display) && IsWebP(result.Thumbnail));
        Assert.Equal(new SKSizeI(2048, 1365), SizeOf(result.Display));
        Assert.Equal(new SKSizeI(400, 267), SizeOf(result.Thumbnail));
        Assert.Equal((3000, 2000), (result.Metrics.Width, result.Metrics.Height));
    }

    [Fact]
    public void Small_originals_keep_every_pixel_and_large_ones_stay_lossy()
    {
        // The size of the imported corpus: a 113 px PNG. Lossy re-encoding blurred what little detail it had.
        using var bitmap = new SKBitmap(113, 113);
        bitmap.Pixels = Enumerable.Range(0, 113 * 113).Select(i => new SKColor((byte)(i * 7), (byte)(i / 113 * 2), (byte)(i % 113 * 2))).ToArray();
        using var png = SKImage.FromBitmap(bitmap).Encode(SKEncodedImageFormat.Png, 100);
        var small = ImageProcessor.Process(png.ToArray(), Options);
        Assert.True(IsLossless(small.Display) && IsLossless(small.Thumbnail));
        using var decoded = SKBitmap.Decode(small.Display);
        Assert.Equal(bitmap.Pixels, decoded.Pixels);

        var large = ImageProcessor.Process(SharpTile(3000, 2000), Options);
        Assert.False(IsLossless(large.Display));
        Assert.False(IsLossless(large.Thumbnail));                                                           // a 400 px thumbnail of a big photo stays small
    }

    [Fact]
    public void Small_originals_are_never_scaled_up()
    {
        var result = ImageProcessor.Process(SharpTile(300, 200), Options);
        Assert.Equal(new SKSizeI(300, 200), SizeOf(result.Display));
        Assert.Equal(new SKSizeI(300, 200), SizeOf(result.Thumbnail));
    }

    [Fact]
    public void A_sharp_well_exposed_tile_has_no_review_reasons()
    {
        var result = ImageProcessor.Process(SharpTile(), Options);
        Assert.Empty(result.ReviewReasons);
        Assert.True(result.Metrics.Sharpness > Options.MinSharpness * 10, $"sharpness {result.Metrics.Sharpness}");
    }

    [Fact]
    public void A_featureless_gradient_is_flagged_blurry()
    {
        var blurred = Jpeg(1600, 1200, (x, y) => { var v = (byte)(80 + x * 100 / 1600); return new SKColor(v, v, v); });
        var result = ImageProcessor.Process(blurred, Options);
        Assert.Contains(ImageProcessor.BlurryReason, result.ReviewReasons);
    }

    [Fact]
    public void Dark_and_blown_out_photos_are_flagged()
    {
        var dark = ImageProcessor.Process(Jpeg(800, 600, (x, y) => ((x / 6) + (y / 6)) % 2 == 0 ? new SKColor(2, 2, 2) : new SKColor(30, 30, 30)), Options);
        var bright = ImageProcessor.Process(Jpeg(800, 600, (x, y) => x < 700 ? SKColors.White : new SKColor(120, 120, 120)), Options);

        Assert.Contains(ImageProcessor.UnderexposedReason, dark.ReviewReasons);
        Assert.Contains(ImageProcessor.OverexposedReason, bright.ReviewReasons);
        Assert.DoesNotContain(ImageProcessor.OverexposedReason, dark.ReviewReasons);
    }

    [Fact]
    public void Exif_orientation_is_applied_so_derivatives_are_upright()
    {
        // Stored landscape 300×200, red on the left edge; orientation 6 means "rotate 90° clockwise to view".
        var jpeg = WithOrientation(Jpeg(300, 200, (x, y) => x < 30 ? SKColors.Red : SKColors.Gray), 6);

        var result = ImageProcessor.Process(jpeg, Options);

        Assert.Equal(new SKSizeI(200, 300), SizeOf(result.Display));
        Assert.Equal((200, 300), (result.Metrics.Width, result.Metrics.Height));
        Assert.Equal(nameof(SKEncodedOrigin.RightTop), result.Metrics.Orientation);
        using var shown = SKBitmap.Decode(result.Display);
        Assert.True(shown.GetPixel(100, 10).Red > 200, "the stored left edge must be at the top after rotating clockwise");
        Assert.True(shown.GetPixel(100, 290).Red < 200);
    }

    [Fact]
    public void Bytes_that_are_not_an_image_cannot_be_decoded()
    {
        Assert.Throws<ImageDecodeException>(() => ImageProcessor.Process("\xFF\xD8 not really a jpeg"u8.ToArray(), Options));
    }
}

public sealed class ImageDerivativesHandlerTests
{
    private sealed class MemoryStorage : IR2ObjectStorage
    {
        public Dictionary<string, (byte[] Bytes, string Type)> Objects { get; } = new();
        public Task<PresignedUpload> CreatePutUrlAsync(string k, string t, IReadOnlyDictionary<string, string>? m, CancellationToken ct) => throw new NotSupportedException();
        public Task<Uri> CreateGetUrlAsync(string k, CancellationToken ct) => CreateGetUrlAsync(k, null, ct);
        public Task<Uri> CreateGetUrlAsync(string k, string? n, CancellationToken ct) => Task.FromResult(new Uri($"https://signed.example/{k}"));
        public Task<StoredObject?> HeadAsync(string k, CancellationToken ct) => Task.FromResult(Objects.TryGetValue(k, out var o) ? new StoredObject(o.Bytes.Length, "etag", o.Type) : null);
        public Task<Stream> OpenReadAsync(string k, CancellationToken ct) => Task.FromResult<Stream>(new MemoryStream(Objects[k].Bytes));
        public Task CopyAsync(string s, string d, CancellationToken ct) { Objects[d] = Objects[s]; return Task.CompletedTask; }
        public Task DeleteAsync(string k, CancellationToken ct) { Objects.Remove(k); return Task.CompletedTask; }
        public Task PutAsync(string k, byte[] c, string t, CancellationToken ct) { Objects[k] = (c, t); return Task.CompletedTask; }
    }

    private static Glazion.Api.Data.GlazionDbContext NewDb() => new(new Microsoft.EntityFrameworkCore.DbContextOptionsBuilder<Glazion.Api.Data.GlazionDbContext>().UseInMemoryDatabase("deriv-" + Guid.NewGuid()).Options);

    private static async Task<(Glazion.Api.Data.GlazionDbContext Db, MemoryStorage Storage, Glazion.Api.Domain.ImageAsset Asset)> SetupAsync(byte[] original, Glazion.Api.Domain.QualityStatus quality, Glazion.Api.Domain.ApprovalStatus approval)
    {
        var db = NewDb();
        var storage = new MemoryStorage();
        var asset = new Glazion.Api.Domain.ImageAsset
        {
            Id = Guid.NewGuid(), OriginalKey = "glazion/originals/x.jpg", DeclaredContentType = "image/jpeg", TileVariantKey = "white",
            QualityStatus = quality, ApprovalStatus = approval, QualityReasonsJson = "[]"
        };
        storage.Objects[asset.OriginalKey] = (original, "image/jpeg");
        db.ImageAssets.Add(asset);
        await db.SaveChangesAsync();
        return (db, storage, asset);
    }

    private static Task<JobResult> RunAsync(Glazion.Api.Data.GlazionDbContext db, MemoryStorage storage, Guid imageId) =>
        new ImageDerivativesHandler(storage, Microsoft.Extensions.Options.Options.Create(new DerivativeOptions()), Microsoft.Extensions.Logging.Abstractions.NullLogger<ImageDerivativesHandler>.Instance)
            .HandleAsync(new Glazion.Api.Domain.ProcessingJob { EntityId = imageId, JobType = JobTypes.ImageDerivativesCreate }, db, default);

    private static readonly byte[] Blurry = ImageProcessingTests.Jpeg(1600, 1200, (x, y) => { var v = (byte)(80 + x * 100 / 1600); return new SKColor(v, v, v); });

    [Fact]
    public async Task Backfill_writes_both_derivatives_and_metrics()
    {
        var (db, storage, asset) = await SetupAsync(ImageProcessingTests.SharpTile(), Glazion.Api.Domain.QualityStatus.Passed, Glazion.Api.Domain.ApprovalStatus.Pending);

        Assert.Equal(JobResultKind.Succeeded, (await RunAsync(db, storage, asset.Id)).Kind);

        Assert.Equal(ObjectKeyPolicy.Display(asset.Id), asset.DisplayKey);
        Assert.Equal(ObjectKeyPolicy.Thumbnail(asset.Id), asset.ThumbnailKey);
        Assert.Equal("image/webp", storage.Objects[asset.DisplayKey!].Type);
        Assert.Equal("image/webp", storage.Objects[asset.ThumbnailKey!].Type);
        Assert.Contains("\"sharpness\"", asset.QualityMetricsJson);
        Assert.NotNull(asset.DerivativesCreatedAt);
        Assert.Equal(Glazion.Api.Domain.QualityStatus.Passed, asset.QualityStatus);
    }

    [Fact]
    public async Task A_blurry_unreviewed_image_moves_to_review()
    {
        var (db, storage, asset) = await SetupAsync(Blurry, Glazion.Api.Domain.QualityStatus.Passed, Glazion.Api.Domain.ApprovalStatus.Pending);
        await RunAsync(db, storage, asset.Id);
        Assert.Equal(Glazion.Api.Domain.QualityStatus.NeedsReview, asset.QualityStatus);
        Assert.Contains(ImageProcessor.BlurryReason, asset.QualityReasonsJson);
    }

    [Fact]
    public async Task An_approved_image_keeps_its_status_but_records_the_reason()
    {
        var (db, storage, asset) = await SetupAsync(Blurry, Glazion.Api.Domain.QualityStatus.Passed, Glazion.Api.Domain.ApprovalStatus.Approved);
        await RunAsync(db, storage, asset.Id);
        Assert.Equal(Glazion.Api.Domain.QualityStatus.Passed, asset.QualityStatus);
        Assert.Contains(ImageProcessor.BlurryReason, asset.QualityReasonsJson);
        Assert.NotNull(asset.DisplayKey);
    }

    [Fact]
    public async Task Unvalidated_images_are_left_to_validation()
    {
        var (db, storage, asset) = await SetupAsync(ImageProcessingTests.SharpTile(), Glazion.Api.Domain.QualityStatus.Pending, Glazion.Api.Domain.ApprovalStatus.Pending);
        Assert.Equal(JobResultKind.Succeeded, (await RunAsync(db, storage, asset.Id)).Kind);
        Assert.Null(asset.DisplayKey);
        Assert.Single(storage.Objects);
    }

    [Fact]
    public async Task Corrupt_originals_fail_the_job_without_touching_the_asset()
    {
        var (db, storage, asset) = await SetupAsync("not an image at all"u8.ToArray(), Glazion.Api.Domain.QualityStatus.Passed, Glazion.Api.Domain.ApprovalStatus.Pending);
        var result = await RunAsync(db, storage, asset.Id);
        Assert.Equal((JobResultKind.Fail, ImageDerivatives.UndecodableReason), (result.Kind, result.ErrorCode));
        Assert.Null(asset.DisplayKey);
    }
}
