using Glazion.Api.Chat;
using Amazon.S3;
using System.Threading.RateLimiting;
using Glazion.Api.Chemistry;
using Glazion.Api.Contracts;
using Glazion.Api.Data;
using Glazion.Api.Domain;
using Glazion.Api.Indexing;
using Glazion.Api.Infrastructure;
using Glazion.Api.Retrieval;
using Glazion.Api.Tasks;
using Microsoft.AspNetCore.Authentication;
using Microsoft.AspNetCore.Authorization;
using Microsoft.AspNetCore.RateLimiting;
using Microsoft.EntityFrameworkCore;
using Microsoft.OpenApi;
using Serilog;

Log.Logger = new LoggerConfiguration().WriteTo.Console().CreateBootstrapLogger();
try
{
    var builder = WebApplication.CreateBuilder(args);
    builder.Host.UseSerilog((context, services, configuration) => configuration.ReadFrom.Configuration(context.Configuration).ReadFrom.Services(services).Enrich.FromLogContext().WriteTo.Console());

    builder.Services.Configure<R2Options>(builder.Configuration.GetSection("R2"));
    builder.Services.Configure<UploadOptions>(builder.Configuration.GetSection("Uploads"));
    builder.Services.Configure<Glazion.Api.Infrastructure.SessionOptions>(builder.Configuration.GetSection("Sessions"));
    builder.Services.Configure<IngestionOptions>(builder.Configuration.GetSection("Ingestion"));
    builder.Services.Configure<DerivativeOptions>(builder.Configuration.GetSection("Derivatives"));
    builder.Services.Configure<WorkerOptions>(builder.Configuration.GetSection("Worker"));
    builder.Services.Configure<VectorIndexOptions>(builder.Configuration.GetSection("VectorIndex"));
    builder.Services.Configure<EmbeddingOptions>(builder.Configuration.GetSection("Embeddings"));
    builder.Services.Configure<RetrievalOptions>(builder.Configuration.GetSection("Retrieval"));
    builder.Services.Configure<FeatureOptions>(builder.Configuration.GetSection("Features"));
    builder.Services.Configure<ChemistryOptions>(builder.Configuration.GetSection("Chemistry"));
    builder.Services.Configure<ChatOptions>(builder.Configuration.GetSection("Chat"));
    builder.Services.Configure<AiCopyOptions>(builder.Configuration.GetSection("AiCopies"));
    // AI copies use the chat's OpenAI key unless they are given one of their own.
    builder.Services.PostConfigure<AiCopyOptions>(o => { if (string.IsNullOrWhiteSpace(o.ApiKey)) o.ApiKey = builder.Configuration["Chat:OpenAI:ApiKey"] ?? string.Empty; });
    var connectionString = builder.Configuration.GetConnectionString("GlazionDb") ?? throw new InvalidOperationException("ConnectionStrings:GlazionDb is required.");
    builder.Services.AddDbContext<GlazionDbContext>(options => options.UseNpgsql(connectionString, npgsql => npgsql.MigrationsHistoryTable("__EFMigrationsHistory_Glazion")));

    builder.Services.AddSingleton<IPasswordService, PasswordService>();
    builder.Services.AddScoped<IR2ObjectStorage, R2ObjectStorage>();
    builder.Services.AddScoped<IImageIngestionService, ImageIngestionService>();
    builder.Services.AddScoped<AuthService>();
    builder.Services.AddScoped<ImageApprovalService>();
    builder.Services.AddScoped<LabCodeService>();
    builder.Services.AddScoped<LabDataReset>();
    builder.Services.AddScoped<TileTaskService>();
builder.Services.AddScoped<MatchCheckService>();
builder.Services.AddScoped<Glazion.Api.Experiments.ExperimentService>();
    builder.Services.AddScoped<RecipeImageSearchService>();
    builder.Services.AddScoped<RecipeLibraryService>();
    builder.Services.AddSingleton<ChemistryDataProvider>();
    builder.Services.AddScoped<MaterialCatalog>();
    builder.Services.AddScoped<UmfCalculationService>();
    builder.Services.AddHttpClient<OpenAiChatModel>();
    builder.Services.AddScoped<IChatModelFactory, ChatModelFactory>();
    builder.Services.AddScoped<ChatTools>();
    builder.Services.AddScoped<ChatService>();
    if (string.Equals(builder.Configuration["Chat:Knowledge:Provider"], "Pinecone", StringComparison.OrdinalIgnoreCase))
        builder.Services.AddHttpClient<IKnowledgeSource, PineconeKnowledgeSource>(client => client.Timeout = TimeSpan.FromSeconds(20));
    else
        builder.Services.AddSingleton<IKnowledgeSource, NoKnowledgeSource>();
    builder.Services.AddScoped<IJobHandler, ImageValidateHandler>();
    builder.Services.AddScoped<IJobHandler, ImageDerivativesHandler>();
    builder.Services.AddScoped<IJobHandler, IndexImageUpsertHandler>();
    builder.Services.AddScoped<IJobHandler, IndexImageRemoveHandler>();
    builder.Services.AddScoped<IJobHandler, AiCopyHandler>();
    builder.Services.AddScoped<AiCopyService>();
    builder.Services.AddHttpClient<IAiImageModel, OpenAiImageModel>();
    if (string.Equals(builder.Configuration["Embeddings:Provider"], "OpenAI", StringComparison.OrdinalIgnoreCase))
        builder.Services.AddHttpClient<ITextEmbeddingProvider, OpenAiCompatibleTextEmbeddingProvider>(client => client.Timeout = TimeSpan.FromSeconds(60));
    else
        builder.Services.AddSingleton<ITextEmbeddingProvider, HashingTextEmbeddingProvider>();
    if (builder.Configuration.GetValue("Worker:Enabled", true)) builder.Services.AddHostedService<JobWorker>();
    if (string.Equals(builder.Configuration["VectorIndex:Provider"], "Pinecone", StringComparison.OrdinalIgnoreCase))
        builder.Services.AddHttpClient<IVectorIndex, PineconeVectorIndex>(client => client.Timeout = TimeSpan.FromSeconds(30));
    else
        builder.Services.AddSingleton<IVectorIndex, InMemoryVectorIndex>();
    var r2 = builder.Configuration.GetSection("R2").Get<R2Options>() ?? new R2Options();
    var r2Config = new AmazonS3Config { ServiceURL = string.IsNullOrWhiteSpace(r2.ServiceUrl) ? "http://127.0.0.1:9000" : r2.ServiceUrl, AuthenticationRegion = r2.Region, ForcePathStyle = true,
        // R2 rejects the checksums newer SDKs add to every request; send them only when an operation requires one.
        RequestChecksumCalculation = Amazon.Runtime.RequestChecksumCalculation.WHEN_REQUIRED, ResponseChecksumValidation = Amazon.Runtime.ResponseChecksumValidation.WHEN_REQUIRED };
    builder.Services.AddSingleton<IAmazonS3>(_ => new AmazonS3Client(r2.AccessKey ?? "not-configured", r2.SecretKey ?? "not-configured", r2Config));

    builder.Services.AddAuthentication("Session").AddScheme<AuthenticationSchemeOptions, SessionAuthenticationHandler>("Session", _ => { });
    // Every [Authorize] endpoint needs an Active account; the few a Pending account may reach opt in with AnyAccount.
    builder.Services.AddAuthorizationBuilder()
        .SetDefaultPolicy(new AuthorizationPolicyBuilder("Session").RequireAuthenticatedUser().RequireClaim(GlazionClaims.AccountStatus, nameof(AccountStatus.Active)).Build())
        .AddPolicy(GlazionPolicies.AnyAccount, policy => policy.AddAuthenticationSchemes("Session").RequireAuthenticatedUser());
    builder.Services.AddSingleton<IAuthorizationMiddlewareResultHandler, AccountStatusAuthorizationResultHandler>();
    builder.Services.AddProblemDetails();
    builder.Services.AddExceptionHandler<ApiExceptionHandler>();
    builder.Services.AddRateLimiter(options =>
    {
        options.RejectionStatusCode = StatusCodes.Status429TooManyRequests;
        options.AddPolicy(RateLimits.Credentials, http => RateLimitPartition.GetFixedWindowLimiter(http.Connection.RemoteIpAddress?.ToString() ?? "unknown",
            _ => new FixedWindowRateLimiterOptions { PermitLimit = builder.Configuration.GetValue("RateLimits:CredentialsPerMinute", 20), Window = TimeSpan.FromMinutes(1), QueueLimit = 0 }));
        options.OnRejected = async (context, cancellationToken) =>
        {
            var problem = ApiProblems.Build(context.HttpContext, new ApiProblemException(429, "rate_limited", "Too many attempts", "Too many attempts. Wait a minute and try again."));
            await context.HttpContext.Response.WriteAsJsonAsync(problem, (System.Text.Json.JsonSerializerOptions?)null, "application/problem+json", cancellationToken);
        };
    });
    // Enums travel as numbers in v2 (the clients' @glazion/types are numeric enums), so no string-enum converter.
    builder.Services.AddControllers(options => { options.Filters.Add<ApiProblemExceptionFilter>(); options.Filters.Add<ErrorBodyProblemFilter>(); })
        .ConfigureApiBehaviorOptions(options => options.InvalidModelStateResponseFactory = context =>
        {
            var errors = context.ModelState.Where(x => x.Value?.Errors.Count > 0).ToDictionary(x => x.Key, x => x.Value!.Errors.Select(e => string.IsNullOrEmpty(e.ErrorMessage) ? "Invalid value." : e.ErrorMessage).ToArray());
            var first = errors.Values.SelectMany(x => x).FirstOrDefault() ?? "The request is not valid.";
            var problem = ApiProblems.Build(context.HttpContext, new ApiProblemException(400, "validation_error", "Validation failed", first, new Dictionary<string, object?> { ["errors"] = errors }));
            return new Microsoft.AspNetCore.Mvc.BadRequestObjectResult(problem) { ContentTypes = { "application/problem+json" } };
        });
    builder.Services.AddEndpointsApiExplorer();
    builder.Services.AddSwaggerGen(options =>
    {
        // Session tokens from POST /api/v2/auth/login, sent as "Authorization: Bearer <accessToken>"
        options.AddSecurityDefinition("Bearer", new OpenApiSecurityScheme
        {
            Type = SecuritySchemeType.Http,
            Scheme = "bearer",
            Description = "Paste tokens.accessToken from POST /api/v2/auth/login."
        });
        options.AddSecurityRequirement(document => new OpenApiSecurityRequirement { [new OpenApiSecuritySchemeReference("Bearer", document)] = [] });
        options.OperationFilter<OptInPagingOperationFilter>();
    });
    builder.Services.AddHealthChecks();
    builder.Services.AddCors(options => options.AddDefaultPolicy(policy => policy.AllowAnyOrigin().AllowAnyHeader().AllowAnyMethod()));

    var app = builder.Build();
    app.UseExceptionHandler();
    app.UseStatusCodePages();
    app.UseSerilogRequestLogging();
    app.UseCors();
    // Always on in Development; elsewhere only when Swagger:Enabled is true
    if (app.Environment.IsDevelopment() || builder.Configuration.GetValue<bool>("Swagger:Enabled")) { app.UseSwagger(); app.UseSwaggerUI(); }
    app.UseAuthentication();
    app.UseAuthorization();
    app.UseRateLimiter();
    app.MapControllers();
    app.MapGet("/health", async (GlazionDbContext db, CancellationToken cancellationToken) =>
    {
        try
        {
            await db.Database.CanConnectAsync(cancellationToken);
            return Results.Ok(new { status = "healthy", timestamp = DateTimeOffset.UtcNow });
        }
        catch (Exception ex)
        {
            return Results.Problem(title: "Database unavailable", statusCode: StatusCodes.Status503ServiceUnavailable, detail: ex.Message);
        }
    });

    using (var scope = app.Services.CreateScope())
    {
        var db = scope.ServiceProvider.GetRequiredService<GlazionDbContext>();
        if (builder.Configuration.GetValue<bool>("Database:ApplyMigrationsOnStartup")) await db.Database.MigrateAsync();
        await BootstrapAdminAsync(db, scope.ServiceProvider.GetRequiredService<IPasswordService>(), builder.Configuration);
        await LoadMaterialOverridesAsync(scope.ServiceProvider.GetRequiredService<MaterialCatalog>());
        await RehydrateInMemoryIndexAsync(db, scope.ServiceProvider.GetRequiredService<IVectorIndex>(), scope.ServiceProvider.GetRequiredService<ITextEmbeddingProvider>(), builder.Configuration);
    }

    await app.RunAsync();
}
catch (Exception ex)
{
    Log.Fatal(ex, "Glazion API terminated unexpectedly");
}
finally { await Log.CloseAndFlushAsync(); }

// The in-memory vector index lives in this process only and starts empty, while IndexRecords persist. Re-queue sync
// for every approved image so local search matches the manifest. A durable provider (Pinecone) only needs the images
// it has not confirmed itself (the first start after switching to it).
static async Task RehydrateInMemoryIndexAsync(GlazionDbContext db, IVectorIndex index, ITextEmbeddingProvider embeddings, IConfiguration configuration)
{
    if (!configuration.GetValue("Worker:Enabled", true)) return;
    if (index is not InMemoryVectorIndex)
    {
        // A durable store keeps its vectors across restarts, but after a switch (InMemory → Pinecone, or a new index)
        // it holds none of the images the manifest calls indexed. Re-send every approved image that has no live
        // record confirmed by this store; on later starts this finds nothing.
        var provider = index.ProviderName;
        if (index is PineconeVectorIndex pinecone)
        {
            try
            {
                var problems = await pinecone.CheckDimensionsAsync(ChemistrySchema.OxideOrder.Count, embeddings.Dimension, CancellationToken.None);
                foreach (var problem in problems) Log.Error("Pinecone: {Problem} Nothing was sent; fix the setting and redeploy.", problem);
                if (problems.Count > 0) return;
            }
            catch (VectorIndexException ex) { Log.Warning(ex, "Pinecone could not be checked at startup; index jobs will retry until it answers"); }
        }
        var pending = await db.ImageAssets
            .Where(x => x.ApprovalStatus == ApprovalStatus.Approved && x.QualityStatus != QualityStatus.Failed)
            .Where(x => !db.IndexRecords.Any(r => r.EntityType == EntityTypes.Image && r.EntityId == x.Id && r.Indexable && r.SupersededAt == null && r.IndexedAt != null && r.VectorProvider == provider))
            .Select(x => x.Id).ToListAsync();
        foreach (var chunk in pending.Chunk(500))
        {
            foreach (var id in chunk) await IndexJobs.EnqueueUpsertAsync(db, id, CancellationToken.None);
            await db.SaveChangesAsync();
            db.ChangeTracker.Clear();
        }
        if (pending.Count > 0) Log.Information("{Provider} vector index: queued {Count} approved images not yet stored there", provider, pending.Count);
        return;
    }
    var ids = await db.ImageAssets.Where(x => x.ApprovalStatus == ApprovalStatus.Approved && x.QualityStatus != QualityStatus.Failed).Select(x => x.Id).ToListAsync();
    foreach (var id in ids) await IndexJobs.EnqueueUpsertAsync(db, id, CancellationToken.None);
    await db.SaveChangesAsync();
    if (ids.Count > 0) Log.Information("In-memory vector index: re-queued index sync for {Count} approved images", ids.Count);
}

// Administrators' material corrections apply from the first request. A database without the table yet (migrations
// not applied) leaves the calculator on materials.json alone rather than failing startup.
static async Task LoadMaterialOverridesAsync(MaterialCatalog catalog)
{
    try
    {
        var count = await catalog.LoadAsync(CancellationToken.None);
        if (count > 0) Log.Information("Material overrides loaded: {Count}", count);
    }
    catch (Exception ex) when (ex is Npgsql.PostgresException or InvalidOperationException)
    {
        Log.Warning(ex, "Material overrides could not be loaded; using materials.json only");
    }
}

static async Task BootstrapAdminAsync(GlazionDbContext db, IPasswordService passwords, IConfiguration configuration)
{
    var username = configuration["BootstrapAdmin:Username"];
    var password = configuration["BootstrapAdmin:Password"];
    if (string.IsNullOrWhiteSpace(username) || string.IsNullOrWhiteSpace(password)) return;
    if (await db.Users.AnyAsync(x => x.Role == UserRole.Admin)) return;
    db.Users.Add(new AppUser { Id = Guid.NewGuid(), Username = username.Trim(), DisplayName = "Glazion Administrator", PasswordHash = passwords.Hash(password), Role = UserRole.Admin, Status = AccountStatus.Active });
    await db.SaveChangesAsync();
}
