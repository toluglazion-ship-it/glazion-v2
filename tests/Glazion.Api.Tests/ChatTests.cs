using System.Text.Json;
using Glazion.Api.Chat;
using Glazion.Api.Chemistry;
using Glazion.Api.Contracts;
using Glazion.Api.Data;
using Glazion.Api.Domain;
using Glazion.Api.Indexing;
using Glazion.Api.Infrastructure;
using Glazion.Api.Retrieval;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.Logging.Abstractions;
using Microsoft.Extensions.Options;

namespace Glazion.Api.Tests;

public sealed class ChatServiceTests
{
    private const string BlueUmf = """{"Na2O":0.18,"K2O":0.12,"CaO":0.52,"MgO":0.18,"Al2O3":0.35,"B2O3":0.2,"SiO2":3.2}""";

    /// <summary>Plays the model: returns queued replies in order and records every request.</summary>
    private sealed class ScriptedModel(params ModelReply[] replies) : IChatModel
    {
        private readonly Queue<ModelReply> _replies = new(replies);
        public List<(IReadOnlyList<ChatTurn> Turns, IReadOnlyList<ToolSpec> Tools)> Requests { get; } = [];
        public string Model => "scripted";
        public Func<ModelReply>? Fallback { get; init; }

        public Task<ModelReply> CompleteAsync(IReadOnlyList<ChatTurn> turns, IReadOnlyList<ToolSpec> tools, CancellationToken cancellationToken)
        {
            Requests.Add((turns.ToList(), tools));
            return Task.FromResult(_replies.Count > 0 ? _replies.Dequeue() : Fallback?.Invoke() ?? Text("Done."));
        }
    }

    private sealed class Factory(IChatModel? model) : IChatModelFactory
    {
        public IChatModel Create() => model ?? throw new ApiProblemException(503, "chat_unavailable", "Chat is not available", "The assistant is not set up yet.");
    }

    private sealed class Knowledge(params KnowledgeMatch[] matches) : IKnowledgeSource
    {
        public bool IsConfigured => true;
        public Task<IReadOnlyList<KnowledgeMatch>> SearchAsync(string question, CancellationToken cancellationToken) => Task.FromResult<IReadOnlyList<KnowledgeMatch>>(matches);
    }

    private sealed class FakeStorage : IR2ObjectStorage
    {
        public Task<PresignedUpload> CreatePutUrlAsync(string objectKey, string contentType, IReadOnlyDictionary<string, string>? metadata, CancellationToken ct) => throw new NotSupportedException();
        public Task<Uri> CreateGetUrlAsync(string objectKey, CancellationToken ct) => CreateGetUrlAsync(objectKey, null, ct);
        public Task<Uri> CreateGetUrlAsync(string objectKey, string? name, CancellationToken ct) => Task.FromResult(new Uri($"https://signed.example/{objectKey}"));
        public Task<StoredObject?> HeadAsync(string objectKey, CancellationToken ct) => throw new NotSupportedException();
        public Task<Stream> OpenReadAsync(string objectKey, CancellationToken ct) => throw new NotSupportedException();
        public Task CopyAsync(string s, string d, CancellationToken ct) => throw new NotSupportedException();
        public Task DeleteAsync(string k, CancellationToken ct) => throw new NotSupportedException();
        public Task PutAsync(string k, byte[] c, string t, CancellationToken ct) => throw new NotSupportedException();
    }

    private sealed class Fixture : IAsyncDisposable
    {
        public GlazionDbContext Db { get; } = new(new DbContextOptionsBuilder<GlazionDbContext>().UseInMemoryDatabase("chat-" + Guid.NewGuid()).Options);
        public InMemoryVectorIndex Index { get; } = new();
        public HashingTextEmbeddingProvider Embeddings { get; } = new(Options.Create(new EmbeddingOptions()));
        public AppUser Potter { get; } = new() { Id = Guid.NewGuid(), Username = "potter", Role = UserRole.User, Status = AccountStatus.Active };
        public AppUser Other { get; } = new() { Id = Guid.NewGuid(), Username = "other", Role = UserRole.User, Status = AccountStatus.Active };
        public FeatureOptions Features { get; } = new() { RecipeImageRetrieval = true };
        public SearchRequester AsPotter => new(Potter.Id, false);
        /// <summary>The same account with admin rights: the chemistry tools (UMF calculator, search by UMF) are theirs.</summary>
        public SearchRequester AsAdmin => new(Potter.Id, true);

        public async Task InitAsync() { Db.Users.AddRange(Potter, Other); await Db.SaveChangesAsync(); }

        public ChatService Service(IChatModel? model, IKnowledgeSource? knowledge = null, ChatOptions? options = null)
        {
            var search = new RecipeImageSearchService(Db, Index, Embeddings, new FakeStorage(), Options.Create(new RetrievalOptions()), Options.Create(new IngestionOptions()),
                new ChemistryDataProvider(TestChemistry.Data()), NullLogger<RecipeImageSearchService>.Instance);
            var umf = new UmfCalculationService(Db, new ChemistryDataProvider(TestChemistry.Data()), NullLogger<UmfCalculationService>.Instance);
            return new ChatService(Db, new Factory(model), knowledge ?? new NoKnowledgeSource(), new ChatTools(Db, umf, search, Options.Create(Features)),
                search, Options.Create(options ?? new ChatOptions()), NullLogger<ChatService>.Instance);
        }

        public async Task<ImageAsset> AddIndexedTileAsync(string recipeCode)
        {
            var asset = new ImageAsset
            {
                Id = Guid.CreateVersion7(), SourceType = ImageSourceType.Import, ImportRecordId = Guid.NewGuid(), OriginalKey = $"prod/originals/default/imports/b/{Guid.NewGuid():N}/source.png",
                DeclaredContentType = "image/png", DeclaredSizeBytes = 1, TileVariantKey = "white", ConeMin = 6, ConeMax = 6, Atmosphere = "oxidation",
                LabelsJson = """{"surface":"glossy","colourFamily":"blue"}""", QualityStatus = QualityStatus.Passed, ApprovalStatus = ApprovalStatus.Approved, SourceVersion = 2
            };
            var recipe = new Recipe { Id = Guid.CreateVersion7(), Name = "Blue celadon", SourceType = RecipeSourceType.Imported, Code = recipeCode };
            var snapshot = new RecipeCalculationResult { Id = Guid.CreateVersion7(), RecipeId = recipe.Id, CalculationProfile = ChemistrySchema.ProvidedUmfProfile, CalculationVersion = "import-1.0.0",
                MaterialAnalysisVersion = "n/a", ChemistrySchemaVersion = ChemistrySchema.Version, InputHash = Guid.NewGuid().ToString("N"), UmfJson = BlueUmf };
            Db.Recipes.Add(recipe); Db.RecipeCalculationResults.Add(snapshot);
            Db.RecipeImages.Add(new RecipeImage { RecipeId = recipe.Id, ImageId = asset.Id, Verified = true });
            asset.ChemistryStatus = ChemistryStatus.Linked; asset.CalculationResultId = snapshot.Id;
            Db.ImageAssets.Add(asset);
            await Db.SaveChangesAsync();
            Assert.Equal(JobResultKind.Succeeded, (await new ImageIndexer(Db, Index, Embeddings, NullLogger<ImageIndexer>.Instance).SyncAsync(asset.Id, CancellationToken.None)).Kind);
            return asset;
        }

        public ValueTask DisposeAsync() => Db.DisposeAsync();
    }

    private static ModelReply Text(string content) => new(content, [], "scripted");
    private static ModelReply Tool(string name, object args) => new(null, [new ModelToolCall("call_" + name, name, JsonSerializer.Serialize(args))], "scripted");
    private static readonly KnowledgeMatch Crazing = new("kb-12", "Crazing and thermal expansion", "A glaze under tension crazes. Raise silica or swap sodium and potassium for lower-expansion fluxes.", 0.82);

    [Fact]
    public async Task A_new_conversation_starts_empty_and_is_listed()
    {
        await using var f = new Fixture(); await f.InitAsync();
        var chat = f.Service(new ScriptedModel());

        var created = await chat.CreateAsync(f.Potter.Id, null, CancellationToken.None);
        var page = await chat.ListAsync(f.Potter.Id, null, null, CancellationToken.None);

        Assert.Equal("New conversation", created.Title);
        Assert.Empty(created.Messages);
        Assert.Equal(created.Id, Assert.Single(page.Items).Id);
        Assert.Empty((await chat.ListAsync(f.Other.Id, null, null, CancellationToken.None)).Items);
    }

    [Fact]
    public async Task An_answer_grounded_in_knowledge_is_stored_with_sources_high_confidence_and_a_title()
    {
        await using var f = new Fixture(); await f.InitAsync();
        var model = new ScriptedModel(Text("Crazing means the glaze is under tension. Raise silica first."), Text("Thinner application also helps."));
        var chat = f.Service(model, new Knowledge(Crazing));
        var conversation = await chat.CreateAsync(f.Potter.Id, null, CancellationToken.None);

        var first = await chat.SendAsync(f.AsPotter, conversation.Id, "  Why does my cone 6 glaze craze on porcelain?  ", null, CancellationToken.None);
        var second = await chat.SendAsync(f.AsPotter, conversation.Id, "Anything else?", null, CancellationToken.None);

        Assert.Equal("user", first.UserMessage.Role);
        Assert.Equal("Why does my cone 6 glaze craze on porcelain?", first.UserMessage.Content);
        Assert.Equal("assistant", first.AssistantMessage.Role);
        Assert.Equal("high", first.AssistantMessage.Confidence);
        var source = Assert.Single(first.AssistantMessage.Sources!);
        Assert.Equal(("kb-12", "knowledge", "Crazing and thermal expansion"), (source.Id, source.Kind, source.Title));
        Assert.Empty(first.AssistantMessage.ToolCalls!);
        Assert.Equal("Why does my cone 6 glaze craze on porcelain?", first.Conversation.Title);

        // The knowledge excerpt and the earlier exchange reach the model
        var secondRequest = model.Requests[1].Turns;
        Assert.Contains(secondRequest, t => t.Role == "system" && t.Content!.Contains("Crazing and thermal expansion"));
        Assert.Contains(secondRequest, t => t.Role == "assistant" && t.Content!.StartsWith("Crazing means"));
        Assert.Equal("Anything else?", secondRequest[^1].Content);
        Assert.Equal("Why does my cone 6 glaze craze on porcelain?", second.Conversation.Title);

        var stored = await chat.GetAsync(f.Potter.Id, conversation.Id, CancellationToken.None);
        Assert.Equal(["user", "assistant", "user", "assistant"], stored.Messages.Select(m => m.Role));
        Assert.Null(stored.Messages[0].Sources);
    }

    [Fact]
    public async Task Without_knowledge_or_tool_data_the_answer_is_low_confidence()
    {
        await using var f = new Fixture(); await f.InitAsync();
        var chat = f.Service(new ScriptedModel(Text("I'm not sure; a test tile would tell you.")));
        var conversation = await chat.CreateAsync(f.Potter.Id, null, CancellationToken.None);

        var reply = await chat.SendAsync(f.AsPotter, conversation.Id, "Is this glaze food safe?", null, CancellationToken.None);

        Assert.Equal("low", reply.AssistantMessage.Confidence);
        Assert.Empty(reply.AssistantMessage.Sources!);
    }

    [Fact]
    public async Task The_model_can_calculate_a_umf_with_the_api_calculator()
    {
        await using var f = new Fixture(); await f.InitAsync();
        var model = new ScriptedModel(
            Tool("calculate_umf", new { rows = TestChemistry.Leach.Select(r => new { material = r.Material, amount = r.Amount }), cone = "6" }),
            Text("Your silica is 3.0 and alumina 0.3, a stable cone 6 glossy."));
        var chat = f.Service(model);
        var conversation = await chat.CreateAsync(f.Potter.Id, null, CancellationToken.None);

        var reply = await chat.SendAsync(f.AsPotter, conversation.Id, "What is the UMF of my recipe?", null, CancellationToken.None);

        var call = Assert.Single(reply.AssistantMessage.ToolCalls!);
        Assert.Equal("calculate_umf", call.Name);
        Assert.Equal("Calculated the UMF of a 4-material recipe at cone 6", call.Summary);
        Assert.Equal("high", reply.AssistantMessage.Confidence);
        var toolTurn = model.Requests[1].Turns.Single(t => t.Role == "tool");
        Assert.Equal("call_calculate_umf", toolTurn.ToolCallId);
        Assert.Contains("\"SiO2\"", toolTurn.Content);
        Assert.Equal(5, model.Requests[0].Tools.Count);
    }

    [Fact]
    public async Task A_regular_user_gets_no_stored_chemistry_and_the_model_keeps_the_matching_to_itself()
    {
        await using var f = new Fixture(); await f.InitAsync();
        await f.AddIndexedTileAsync("P01-B1-009");
        var model = new ScriptedModel(
            Tool("get_recipe", new { code = "P01-B1-009" }),
            Text("It is a blue celadon."));
        var chat = f.Service(model);
        var conversation = await chat.CreateAsync(f.Potter.Id, null, CancellationToken.None);

        await chat.SendAsync(f.AsPotter, conversation.Id, "What's the chemistry of P01-B1-009?", null, CancellationToken.None);

        // The calculator stays (it works on the user's own recipe); search by UMF and a stored recipe's UMF don't.
        var tools = model.Requests[0].Tools;
        Assert.Contains(tools, t => t.Name == "calculate_umf");
        Assert.False((tools.Single(t => t.Name == "search_recipe_images").Parameters["properties"] as System.Text.Json.Nodes.JsonObject)!.ContainsKey("umf"));
        Assert.Contains(model.Requests[0].Turns, t => t.Role == "system" && t.Content!.Contains("Never explain how Glazion finds or ranks matching tiles"));
        var recipe = model.Requests[1].Turns.Last(t => t.Role == "tool").Content!;
        Assert.Contains("\"umf\":null", recipe.Replace(" ", ""));
    }

    [Fact]
    public async Task A_tile_search_links_its_results_and_cites_the_tiles()
    {
        await using var f = new Fixture(); await f.InitAsync();
        var tile = await f.AddIndexedTileAsync("P01-B1-001");
        var chat = f.Service(new ScriptedModel(
            Tool("search_recipe_images", new { umf = JsonDocument.Parse(BlueUmf).RootElement, coneMin = 6, coneMax = 6, atmosphere = "oxidation" }),
            Text("Here is a close match fired at cone 6 in oxidation.")));
        var conversation = await chat.CreateAsync(f.Potter.Id, null, CancellationToken.None);

        var reply = await chat.SendAsync(f.AsAdmin, conversation.Id, "Show me tiles like this UMF", null, CancellationToken.None);

        var call = Assert.Single(reply.AssistantMessage.ToolCalls!);
        Assert.Equal("search_recipe_images", call.Name);
        Assert.NotNull(call.SearchId);
        Assert.Equal("Found 1 tile photo matching the UMF", call.Summary);
        var source = Assert.Single(reply.AssistantMessage.Sources!);
        Assert.Equal((tile.Id.ToString(), "image"), (source.Id, source.Kind));
        Assert.Contains("cone 6, oxidation", source.Snippet);
        Assert.True(await f.Db.SearchRuns.AnyAsync(x => x.Id == call.SearchId && x.RequesterUserId == f.Potter.Id));
    }

    [Fact]
    public async Task Invented_image_links_are_removed_from_a_tile_answer_and_the_model_is_told_not_to_write_them()
    {
        await using var f = new Fixture(); await f.InitAsync();
        var tile = await f.AddIndexedTileAsync("P01-B1-005");
        var model = new ScriptedModel(
            Tool("search_recipe_images", new { umf = JsonDocument.Parse(BlueUmf).RootElement }),
            Text("Tile #1 · P01-B1-005 is the closest.\n\n![Tile 1](https://example.com/image1)\n\nSee [the photo](https://example.com/image1) or [Digitalfire](https://digitalfire.com/glossary/crazing)."));
        var chat = f.Service(model);
        var conversation = await chat.CreateAsync(f.Potter.Id, null, CancellationToken.None);

        var reply = await chat.SendAsync(f.AsAdmin, conversation.Id, "Show me tiles like this UMF", null, CancellationToken.None);

        Assert.Equal("Tile #1 · P01-B1-005 is the closest.\n\nSee the photo or [Digitalfire](https://digitalfire.com/glossary/crazing).", reply.AssistantMessage.Content);
        Assert.Equal((tile.Id.ToString(), "image"), (reply.AssistantMessage.Sources![0].Id, reply.AssistantMessage.Sources[0].Kind));
        Assert.Contains("Never write image links", model.Requests[0].Turns[0].Content);
        Assert.Contains("do not write image links", model.Requests[1].Turns.Last(t => t.Role == "tool").Content);
    }

    [Fact]
    public async Task A_tile_asked_about_from_a_search_reaches_the_model_with_its_recipe_but_the_conversation_shows_only_the_question()
    {
        await using var f = new Fixture(); await f.InitAsync();
        var tile = await f.AddIndexedTileAsync("P01-B1-007");
        var recipeId = (await f.Db.RecipeImages.SingleAsync(x => x.ImageId == tile.Id)).RecipeId.ToString("D");
        var model = new ScriptedModel(
            Tool("search_recipe_images", new { umf = JsonDocument.Parse(BlueUmf).RootElement }), Text("Tile #1 is the closest."),
            Text("It's a blue celadon."), Text("Cone 6 in oxidation."));
        var chat = f.Service(model);
        var conversation = await chat.CreateAsync(f.Potter.Id, null, CancellationToken.None);
        var searchId = (await chat.SendAsync(f.AsAdmin, conversation.Id, "Show me tiles like this UMF", null, CancellationToken.None)).AssistantMessage.ToolCalls![0].SearchId!.Value;

        var asked = await chat.SendAsync(f.AsAdmin, conversation.Id, "Tell me about this tile", new ChatTileRef(searchId, tile.Id), CancellationToken.None);
        await chat.SendAsync(f.AsAdmin, conversation.Id, "What cone was it?", null, CancellationToken.None);

        // The tile goes to the model just before the question: labels, firing, and the recipe with its id for get_recipe
        var turns = model.Requests[2].Turns;
        Assert.Equal(("user", "Tell me about this tile"), (turns[^1].Role, turns[^1].Content));
        Assert.Equal("system", turns[^2].Role);
        Assert.Contains("glossy, blue, cone 6, oxidation", turns[^2].Content);
        Assert.Contains($"P01-B1-007 Blue celadon with recipeId {recipeId}", turns[^2].Content);
        // A follow-up still knows which tile it was
        Assert.Contains(model.Requests[3].Turns, t => t.Role == "system" && t.Content!.Contains(recipeId));
        // The user sees only what they wrote
        Assert.Equal("Tell me about this tile", asked.UserMessage.Content);
        var stored = await chat.GetAsync(f.Potter.Id, conversation.Id, CancellationToken.None);
        Assert.DoesNotContain(stored.Messages, m => m.Content.Contains(recipeId));
    }

    [Fact]
    public async Task Only_a_tile_from_the_users_own_search_can_be_asked_about()
    {
        await using var f = new Fixture(); await f.InitAsync();
        var tile = await f.AddIndexedTileAsync("P01-B1-008");
        var chat = f.Service(new ScriptedModel(Tool("search_recipe_images", new { umf = JsonDocument.Parse(BlueUmf).RootElement }), Text("Tile #1 is the closest.")) { Fallback = () => Text("Done.") });
        var conversation = await chat.CreateAsync(f.Potter.Id, null, CancellationToken.None);
        var searchId = (await chat.SendAsync(f.AsAdmin, conversation.Id, "Show me tiles like this UMF", null, CancellationToken.None)).AssistantMessage.ToolCalls![0].SearchId!.Value;
        var theirs = await chat.CreateAsync(f.Other.Id, null, CancellationToken.None);

        var notTheirSearch = await Assert.ThrowsAsync<ApiProblemException>(() =>
            chat.SendAsync(new SearchRequester(f.Other.Id, false), theirs.Id, "Tell me about this tile", new ChatTileRef(searchId, tile.Id), CancellationToken.None));
        var notInTheSearch = await Assert.ThrowsAsync<ApiProblemException>(() =>
            chat.SendAsync(f.AsAdmin, conversation.Id, "Tell me about this tile", new ChatTileRef(searchId, Guid.NewGuid()), CancellationToken.None));

        Assert.Equal((404, "search_not_found"), (notTheirSearch.Status, notTheirSearch.Code));
        Assert.Equal((422, "image_not_in_results"), (notInTheSearch.Status, notInTheSearch.Code));
        Assert.Equal(2, await f.Db.ChatMessages.CountAsync());
    }

    [Theory]
    [InlineData("A ![x](https://cdn.example/a.png) tile", "A  tile")]
    [InlineData("[Tile 2](tile-2.png)", "Tile 2")]
    [InlineData("[shot](https://placeholder.co/600)", "shot")]
    [InlineData("[Glazy](https://glazy.org/recipes/1)", "[Glazy](https://glazy.org/recipes/1)")]
    public void Only_images_and_placeholder_links_are_stripped(string answer, string expected) =>
        Assert.Equal(expected, ChatService.StripInventedLinks(answer));

    [Fact]
    public async Task Tile_search_respects_the_system_wide_switch()
    {
        await using var f = new Fixture(); await f.InitAsync();
        f.Features.RecipeImageRetrieval = false;
        var chat = f.Service(new ScriptedModel(Tool("search_recipe_images", new { recipeCode = "P01-B1-001" }), Text("Search isn't available yet.")));
        var conversation = await chat.CreateAsync(f.Potter.Id, null, CancellationToken.None);

        var reply = await chat.SendAsync(f.AsPotter, conversation.Id, "Find tiles for P01-B1-001", null, CancellationToken.None);

        var call = Assert.Single(reply.AssistantMessage.ToolCalls!);
        Assert.Null(call.SearchId);
        Assert.Equal("Tile search is switched off at the moment.", call.Summary);
        Assert.Equal("low", reply.AssistantMessage.Confidence);
    }

    [Fact]
    public async Task Recipes_are_system_scoped_but_uploads_stay_private()
    {
        await using var f = new Fixture(); await f.InitAsync();
        f.Db.Recipes.Add(new Recipe { Id = Guid.CreateVersion7(), Name = "Secret shino", SourceType = RecipeSourceType.Manual, OwnerUserId = f.Other.Id });
        f.Db.Uploads.Add(new Upload { Id = Guid.CreateVersion7(), OwnerUserId = f.Other.Id, OriginalFileName = "theirs.jpg", ObjectKey = "k1", ContentType = "image/jpeg", Status = UploadStatus.NeedsReview });
        f.Db.Uploads.Add(new Upload { Id = Guid.CreateVersion7(), OwnerUserId = f.Potter.Id, OriginalFileName = "mine.jpg", ObjectKey = "k2", ContentType = "image/jpeg", Status = UploadStatus.Approved });
        await f.Db.SaveChangesAsync();
        var model = new ScriptedModel(new ModelReply(null, [new("c1", "get_recipe", """{"name":"shino"}"""), new("c2", "get_upload_status", "{}")], "scripted"), Text("ok"));
        var chat = f.Service(model);
        var conversation = await chat.CreateAsync(f.Potter.Id, null, CancellationToken.None);

        var reply = await chat.SendAsync(f.AsPotter, conversation.Id, "Show my shino and my uploads", null, CancellationToken.None);

        Assert.Equal(["Looked up recipe Secret shino", "Checked upload mine.jpg: Approved"], reply.AssistantMessage.ToolCalls!.Select(c => c.Summary));
        var uploadsResult = model.Requests[1].Turns.Single(t => t.ToolCallId == "c2").Content!;
        Assert.Contains("mine.jpg", uploadsResult);
        Assert.DoesNotContain("theirs.jpg", uploadsResult);
    }

    [Fact]
    public async Task Tool_rounds_are_capped_and_the_last_request_offers_no_tools()
    {
        await using var f = new Fixture(); await f.InitAsync();
        var model = new ScriptedModel { Fallback = () => Tool("get_material", new { name = "Silica" }) };
        var chat = f.Service(model, options: new ChatOptions { MaxToolRounds = 2 });
        var conversation = await chat.CreateAsync(f.Potter.Id, null, CancellationToken.None);

        var reply = await chat.SendAsync(f.AsPotter, conversation.Id, "Tell me about silica", null, CancellationToken.None);

        Assert.Equal(3, model.Requests.Count);
        Assert.Empty(model.Requests[2].Tools);
        Assert.Equal(2, reply.AssistantMessage.ToolCalls!.Count);
        Assert.StartsWith("I couldn't put together an answer", reply.AssistantMessage.Content);
    }

    [Fact]
    public async Task Conversations_are_private_and_can_be_renamed_and_deleted()
    {
        await using var f = new Fixture(); await f.InitAsync();
        var chat = f.Service(new ScriptedModel(Text("Hello!")));
        var conversation = await chat.CreateAsync(f.Potter.Id, null, CancellationToken.None);
        var reply = await chat.SendAsync(f.AsPotter, conversation.Id, "Hi", null, CancellationToken.None);

        Assert.Equal(404, (await Assert.ThrowsAsync<ApiProblemException>(() => chat.GetAsync(f.Other.Id, conversation.Id, CancellationToken.None))).Status);
        Assert.Equal(404, (await Assert.ThrowsAsync<ApiProblemException>(() => chat.SendAsync(new SearchRequester(f.Other.Id, false), conversation.Id, "Hi", null, CancellationToken.None))).Status);
        Assert.Equal(404, (await Assert.ThrowsAsync<ApiProblemException>(() => chat.RecordFeedbackAsync(f.Other.Id, new ChatFeedbackRequest(reply.AssistantMessage.Id, "helpful", null), CancellationToken.None))).Status);

        var renamed = await chat.RenameAsync(f.Potter.Id, conversation.Id, "  Glaze questions  ", CancellationToken.None);
        Assert.Equal("Glaze questions", renamed.Title);
        Assert.Equal(400, (await Assert.ThrowsAsync<ApiProblemException>(() => chat.RenameAsync(f.Potter.Id, conversation.Id, " ", CancellationToken.None))).Status);

        await chat.RecordFeedbackAsync(f.Potter.Id, new ChatFeedbackRequest(reply.AssistantMessage.Id, "helpful", null), CancellationToken.None);
        await chat.DeleteAsync(f.Potter.Id, conversation.Id, CancellationToken.None);
        Assert.Empty(await f.Db.Conversations.ToListAsync());
        Assert.Empty(await f.Db.ChatMessages.ToListAsync());
        Assert.Empty(await f.Db.ChatFeedback.ToListAsync());
    }

    [Fact]
    public async Task Feedback_is_one_per_user_and_answer_and_only_on_answers()
    {
        await using var f = new Fixture(); await f.InitAsync();
        var chat = f.Service(new ScriptedModel(Text("Raise silica.")));
        var conversation = await chat.CreateAsync(f.Potter.Id, null, CancellationToken.None);
        var reply = await chat.SendAsync(f.AsPotter, conversation.Id, "Crazing?", null, CancellationToken.None);

        await chat.RecordFeedbackAsync(f.Potter.Id, new ChatFeedbackRequest(reply.AssistantMessage.Id, "helpful", null), CancellationToken.None);
        await chat.RecordFeedbackAsync(f.Potter.Id, new ChatFeedbackRequest(reply.AssistantMessage.Id, "not_helpful", "Too short"), CancellationToken.None);

        var feedback = Assert.Single(await f.Db.ChatFeedback.ToListAsync());
        Assert.Equal(("not_helpful", "Too short"), (feedback.Feedback, feedback.Note));
        Assert.Equal(400, (await Assert.ThrowsAsync<ApiProblemException>(() => chat.RecordFeedbackAsync(f.Potter.Id, new ChatFeedbackRequest(reply.UserMessage.Id, "helpful", null), CancellationToken.None))).Status);
        Assert.Equal(400, (await Assert.ThrowsAsync<ApiProblemException>(() => chat.RecordFeedbackAsync(f.Potter.Id, new ChatFeedbackRequest(reply.AssistantMessage.Id, "great", null), CancellationToken.None))).Status);
    }

    [Fact]
    public async Task Nothing_is_stored_when_chat_is_not_configured_or_the_message_is_empty()
    {
        await using var f = new Fixture(); await f.InitAsync();
        var conversation = await f.Service(new ScriptedModel()).CreateAsync(f.Potter.Id, null, CancellationToken.None);

        var unavailable = await Assert.ThrowsAsync<ApiProblemException>(() => f.Service(null).SendAsync(f.AsPotter, conversation.Id, "Hello", null, CancellationToken.None));
        var empty = await Assert.ThrowsAsync<ApiProblemException>(() => f.Service(new ScriptedModel()).SendAsync(f.AsPotter, conversation.Id, "   ", null, CancellationToken.None));

        Assert.Equal((503, "chat_unavailable"), (unavailable.Status, unavailable.Code));
        Assert.Equal(400, empty.Status);
        Assert.Empty(await f.Db.ChatMessages.ToListAsync());
    }

    [Fact]
    public void Tool_names_match_the_apps_contract()
    {
        Assert.Equal(["calculate_umf", "search_recipe_images", "get_recipe", "get_upload_status", "get_material"], ChatTools.Specs.Select(s => s.Name));
    }
}
