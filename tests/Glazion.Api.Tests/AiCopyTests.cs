using System.Net;
using System.Text;
using Glazion.Api.Contracts;
using Glazion.Api.Data;
using Glazion.Api.Domain;
using Glazion.Api.Infrastructure;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.Logging.Abstractions;
using Microsoft.Extensions.Options;
using SkiaSharp;

namespace Glazion.Api.Tests;

public sealed class AiCopyTests
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

    /// <summary>Answers with a fixed picture, or throws what it is told to; records what it was sent.</summary>
    private sealed class FakeModel : IAiImageModel
    {
        public string Model => "fake-image-model";
        public List<(byte[] Source, string ContentType, string Prompt, string Quality)> Calls { get; } = new();
        public Exception? Throw { get; set; }
        public Task<GeneratedImage> EditAsync(byte[] source, string sourceContentType, string prompt, string quality, CancellationToken cancellationToken)
        {
            Calls.Add((source, sourceContentType, prompt, quality));
            if (Throw is not null) throw Throw;
            return Task.FromResult(new GeneratedImage(ImageProcessingTests.Jpeg(1024, 1024, (x, y) => new SKColor(40, 90, (byte)(150 + x % 50))), """{"input_tokens":1200,"output_tokens":4160,"total_tokens":5360}"""));
        }
    }

    private sealed class Fixture : IAsyncDisposable
    {
        public GlazionDbContext Db { get; } = new(new DbContextOptionsBuilder<GlazionDbContext>().UseInMemoryDatabase("ai-copies-" + Guid.NewGuid()).Options);
        public MemoryStorage Storage { get; } = new();
        public FakeModel Model { get; } = new();
        public AiCopyOptions Options { get; } = new() { ApiKey = "test-key", Model = "fake-image-model" };
        public ImageAsset Asset { get; private set; } = null!;
        public Guid AdminId { get; } = Guid.NewGuid();

        public AiCopyService Service() => new(Db, Storage, Microsoft.Extensions.Options.Options.Create(Options));

        public async Task<Fixture> InitAsync()
        {
            var recipe = new Recipe { Id = Guid.NewGuid(), Name = "B1 + 2% Cobalt Carbonate", Code = "P01-B1-002", AdditionsJson = """[{"rawName":"Cobalt Carbonate","amount":2,"unit":"%"}]""" };
            Asset = new ImageAsset
            {
                Id = Guid.NewGuid(), OriginalKey = "prod/originals/x.jpg", DeclaredContentType = "image/jpeg", TileVariantKey = "white",
                QualityStatus = QualityStatus.Passed, ApprovalStatus = ApprovalStatus.Approved, QualityReasonsJson = "[]",
                ConeMin = 6, ConeMax = 6, Atmosphere = "Oxidation", ClayBody = "porcelain", DerivativesCreatedAt = DateTimeOffset.UtcNow
            };
            Asset.DisplayKey = ObjectKeyPolicy.Display(Asset.Id);
            Asset.ThumbnailKey = ObjectKeyPolicy.Thumbnail(Asset.Id);
            Storage.Objects[Asset.OriginalKey] = (ImageProcessingTests.SharpTile(), "image/jpeg");
            Storage.Objects[Asset.DisplayKey] = (Encoding.UTF8.GetBytes("display-of-the-original"), "image/webp");
            Db.Recipes.Add(recipe);
            Db.ImageAssets.Add(Asset);
            Db.RecipeImages.Add(new RecipeImage { RecipeId = recipe.Id, ImageId = Asset.Id, Verified = true });
            await Db.SaveChangesAsync();
            return this;
        }

        /// <summary>Runs the copy's job as the worker would (attempt counted first).</summary>
        public async Task<JobResult> RunAsync(Guid copyId, int attempt = 1)
        {
            var handler = new AiCopyHandler(Storage, Model, Microsoft.Extensions.Options.Options.Create(new DerivativeOptions()), NullLogger<AiCopyHandler>.Instance);
            var result = await handler.HandleAsync(new ProcessingJob { EntityId = copyId, JobType = AiCopyService.JobType, Attempts = attempt, MaxAttempts = 3 }, Db, default);
            Db.ChangeTracker.Clear();
            return result;
        }

        public ValueTask DisposeAsync() => Db.DisposeAsync();
    }

    [Fact]
    public void The_prompt_keeps_the_glaze_and_changes_only_the_photography()
    {
        var asset = new ImageAsset { ConeMin = -6, Atmosphere = "Reduction", ClayBody = "red_body" };
        var recipe = new Recipe { AdditionsJson = """[{"rawName":"Iron Oxide Red","amount":1.5,"unit":"%"}]""" };

        var prompt = AiCopyPrompt.Build(asset, recipe, "  keep the speckles  ");

        Assert.Contains("exactly the same glaze", prompt);
        Assert.Contains("Do not change the colours", prompt);
        Assert.Contains("Change only the photography", prompt);
        Assert.Contains("For reference only (the photo decides how it looks): Its colorants: 1.5% Iron Oxide Red. Fired cone 06, reduction, on red clay.", prompt);
        Assert.EndsWith("Also: keep the speckles", prompt);
        Assert.DoesNotContain("For reference", AiCopyPrompt.Build(new ImageAsset(), null, null));
    }

    [Fact]
    public async Task A_copy_is_made_in_the_background_then_approved_in_place_of_the_photo_and_undone()
    {
        await using var f = await new Fixture().InitAsync();

        var queued = await f.Service().MakeAsync(f.Asset.Id, new MakeAiCopyRequest("less shine", "medium"), f.AdminId, default);
        Assert.Equal((AiCopyStatus.Queued, "medium", "less shine"), (queued.Status, queued.Quality, queued.Instruction));
        Assert.Single(f.Db.ProcessingJobs, j => j.JobType == AiCopyService.JobType && j.EntityId == queued.Id);
        // One at a time per photo, so a double click costs one copy.
        var twice = await Assert.ThrowsAsync<ApiProblemException>(() => f.Service().MakeAsync(f.Asset.Id, new MakeAiCopyRequest(null, null), f.AdminId, default));
        Assert.Equal("ai_copy_in_progress", twice.Code);

        Assert.Equal(JobResultKind.Succeeded, (await f.RunAsync(queued.Id)).Kind);
        // The model was sent the original's upright display derivative, and the prompt with the instruction.
        var call = Assert.Single(f.Model.Calls);
        Assert.Equal(("display-of-the-original", "image/webp", "medium"), (Encoding.UTF8.GetString(call.Source), call.ContentType, call.Quality));
        Assert.Contains("Cobalt Carbonate", call.Prompt);
        Assert.EndsWith("Also: less shine", call.Prompt);

        var ready = Assert.Single(await f.Service().ListAsync(f.Asset.Id, default));
        Assert.Equal((AiCopyStatus.Ready, 1024, 4160), (ready.Status, ready.Width, ready.Usage!.OutputTokens));
        Assert.Contains("/ai/", ready.DisplayUrl!.ToString());
        Assert.True(f.Storage.Objects.ContainsKey(ObjectKeyPolicy.AiCopy(f.Asset.Id, ready.Id)));

        // Approved: every app now shows the copy; the original and its derivatives are untouched.
        await f.Service().ApproveAsync(f.Asset.Id, ready.Id, f.AdminId, default);
        f.Db.ChangeTracker.Clear();
        var shown = await f.Db.ImageAssets.SingleAsync(x => x.Id == f.Asset.Id);
        Assert.Equal((ObjectKeyPolicy.AiCopy(f.Asset.Id, ready.Id), ObjectKeyPolicy.AiCopyThumbnail(f.Asset.Id, ready.Id), (Guid?)ready.Id, "prod/originals/x.jpg"),
            (shown.DisplayKey, shown.ThumbnailKey, shown.DisplayCopyId, shown.OriginalKey));
        Assert.True(f.Storage.Objects.ContainsKey(ObjectKeyPolicy.Display(f.Asset.Id)));

        // Remaking the photo's derivatives does not take the copy away.
        var rebuild = new ImageDerivativesHandler(f.Storage, Microsoft.Extensions.Options.Options.Create(new DerivativeOptions()), NullLogger<ImageDerivativesHandler>.Instance);
        Assert.Equal(JobResultKind.Succeeded, (await rebuild.HandleAsync(new ProcessingJob { EntityId = f.Asset.Id, JobType = JobTypes.ImageDerivativesCreate }, f.Db, default)).Kind);
        f.Db.ChangeTracker.Clear();
        Assert.Equal(ObjectKeyPolicy.AiCopy(f.Asset.Id, ready.Id), (await f.Db.ImageAssets.SingleAsync(x => x.Id == f.Asset.Id)).DisplayKey);

        // Undone: the photo is shown again and the copy can be approved later.
        await f.Service().RevertAsync(f.Asset.Id, default);
        f.Db.ChangeTracker.Clear();
        var original = await f.Db.ImageAssets.SingleAsync(x => x.Id == f.Asset.Id);
        Assert.Equal((ObjectKeyPolicy.Display(f.Asset.Id), (Guid?)null), (original.DisplayKey, original.DisplayCopyId));
        Assert.Equal(AiCopyStatus.Ready, (await f.Db.AiImageCopies.SingleAsync()).Status);

        // Discarding the copy that is shown also brings the photo back.
        await f.Service().ApproveAsync(f.Asset.Id, ready.Id, f.AdminId, default);
        await f.Service().RejectAsync(f.Asset.Id, ready.Id, f.AdminId, default);
        f.Db.ChangeTracker.Clear();
        Assert.Null((await f.Db.ImageAssets.SingleAsync(x => x.Id == f.Asset.Id)).DisplayCopyId);
        Assert.Equal(AiCopyStatus.Rejected, (await f.Db.AiImageCopies.SingleAsync()).Status);
    }

    [Fact]
    public async Task A_refusal_fails_the_copy_with_the_reason_and_an_outage_is_retried_until_the_last_try()
    {
        await using var f = await new Fixture().InitAsync();
        var copy = await f.Service().MakeAsync(f.Asset.Id, new MakeAiCopyRequest(null, null), f.AdminId, default);

        f.Model.Throw = new AiImageException("image_model_busy", "The image model returned 429: slow down", transient: true);
        Assert.Equal(JobResultKind.Retry, (await f.RunAsync(copy.Id, attempt: 1)).Kind);
        Assert.Equal(AiCopyStatus.Queued, (await f.Db.AiImageCopies.SingleAsync()).Status);
        Assert.Equal(JobResultKind.Fail, (await f.RunAsync(copy.Id, attempt: 3)).Kind);
        var failed = await f.Db.AiImageCopies.SingleAsync();
        Assert.Equal((AiCopyStatus.Failed, "The image model returned 429: slow down"), (failed.Status, failed.Error));

        var again = await f.Service().MakeAsync(f.Asset.Id, new MakeAiCopyRequest(null, null), f.AdminId, default);
        f.Model.Throw = new AiImageException("image_model_rejected", "The image model returned 400: Your request was rejected by the safety system.", transient: false);
        Assert.Equal(JobResultKind.Fail, (await f.RunAsync(again.Id, attempt: 1)).Kind);
        Assert.Equal(AiCopyStatus.Failed, (await f.Db.AiImageCopies.SingleAsync(x => x.Id == again.Id)).Status);
    }

    [Fact]
    public async Task Without_a_key_copies_are_refused_and_bad_input_is_named()
    {
        await using var f = await new Fixture().InitAsync();
        f.Options.ApiKey = "";
        Assert.Equal(503, (await Assert.ThrowsAsync<ApiProblemException>(() => f.Service().MakeAsync(f.Asset.Id, new MakeAiCopyRequest(null, null), f.AdminId, default))).Status);
        f.Options.ApiKey = "test-key";
        Assert.Equal(400, (await Assert.ThrowsAsync<ApiProblemException>(() => f.Service().MakeAsync(f.Asset.Id, new MakeAiCopyRequest(null, "ultra"), f.AdminId, default))).Status);
        Assert.Equal(404, (await Assert.ThrowsAsync<ApiProblemException>(() => f.Service().MakeAsync(Guid.NewGuid(), new MakeAiCopyRequest(null, null), f.AdminId, default))).Status);
    }

    private sealed class RecordingHandler(HttpStatusCode status, string body) : HttpMessageHandler
    {
        public string? Path { get; private set; }
        public string? Form { get; private set; }
        public string? Auth { get; private set; }
        protected override async Task<HttpResponseMessage> SendAsync(HttpRequestMessage request, CancellationToken cancellationToken)
        {
            Path = request.RequestUri!.AbsolutePath;
            Auth = request.Headers.Authorization?.ToString();
            Form = await request.Content!.ReadAsStringAsync(cancellationToken);
            return new HttpResponseMessage(status) { Content = new StringContent(body, Encoding.UTF8, "application/json") };
        }
    }

    private static OpenAiImageModel Model(RecordingHandler handler) =>
        new(new HttpClient(handler), Microsoft.Extensions.Options.Options.Create(new AiCopyOptions { ApiKey = "sk-test", Model = "gpt-image-2.5-sunburst" }), NullLogger<OpenAiImageModel>.Instance);

    [Fact]
    public async Task The_openai_client_sends_the_photo_to_the_edit_endpoint_and_reads_the_picture_back()
    {
        var picture = Convert.ToBase64String([1, 2, 3, 4]);
        var handler = new RecordingHandler(HttpStatusCode.OK, $$$"""{"created":1,"data":[{"b64_json":"{{{picture}}}"}],"usage":{"input_tokens":10,"output_tokens":20,"total_tokens":30}}""");

        var result = await Model(handler).EditAsync([9, 9, 9], "image/webp", "Same glaze, new photo.", "high", default);

        Assert.Equal([1, 2, 3, 4], result.Bytes);
        Assert.Contains("\"output_tokens\":20", result.UsageJson);
        Assert.Equal("/v1/images/edits", handler.Path);
        Assert.Equal("Bearer sk-test", handler.Auth);
        foreach (var part in new[] { "name=model", "gpt-image-2.5-sunburst", "name=image; filename=tile.webp", "name=quality", "high", "name=output_format", "webp", "Same glaze, new photo." })
            Assert.Contains(part, handler.Form);
        Assert.DoesNotContain("input_fidelity", handler.Form);
    }

    [Theory]
    [InlineData(HttpStatusCode.TooManyRequests, "image_model_busy", true)]
    [InlineData(HttpStatusCode.BadGateway, "image_model_unavailable", true)]
    [InlineData(HttpStatusCode.BadRequest, "image_model_rejected", false)]
    [InlineData(HttpStatusCode.Forbidden, "image_model_refused_key", false)]
    public async Task The_openai_client_says_whether_trying_again_could_help(HttpStatusCode status, string code, bool transient)
    {
        var handler = new RecordingHandler(status, """{"error":{"message":"Organization must be verified to use this model."}}""");
        var ex = await Assert.ThrowsAsync<AiImageException>(() => Model(handler).EditAsync([1], "image/jpeg", "p", "high", default));
        Assert.Equal((code, transient), (ex.Code, ex.Transient));
        Assert.Contains("Organization must be verified", ex.Message);
    }
}
