using Glazion.Api.Domain;
using Microsoft.EntityFrameworkCore;

namespace Glazion.Api.Data;

public sealed class GlazionDbContext(DbContextOptions<GlazionDbContext> options) : DbContext(options)
{
    public DbSet<AppUser> Users => Set<AppUser>();
    public DbSet<UserSession> Sessions => Set<UserSession>();
    public DbSet<CaptureCode> CaptureCodes => Set<CaptureCode>();
    public DbSet<Recipe> Recipes => Set<Recipe>();
    public DbSet<BaseGlaze> BaseGlazes => Set<BaseGlaze>();
    public DbSet<CodeRecipe> CodeRecipes => Set<CodeRecipe>();
    public DbSet<Upload> Uploads => Set<Upload>();
    public DbSet<ImageAsset> ImageAssets => Set<ImageAsset>();
    public DbSet<AiImageCopy> AiImageCopies => Set<AiImageCopy>();
    public DbSet<RecipeCalculationResult> RecipeCalculationResults => Set<RecipeCalculationResult>();
    public DbSet<RecipeImage> RecipeImages => Set<RecipeImage>();
    public DbSet<ImportBatch> ImportBatches => Set<ImportBatch>();
    public DbSet<ImportRecord> ImportRecords => Set<ImportRecord>();
    public DbSet<ProcessingJob> ProcessingJobs => Set<ProcessingJob>();
    public DbSet<IndexRecord> IndexRecords => Set<IndexRecord>();
    public DbSet<SearchRun> SearchRuns => Set<SearchRun>();
    public DbSet<SearchFeedback> SearchFeedback => Set<SearchFeedback>();
    public DbSet<Conversation> Conversations => Set<Conversation>();
    public DbSet<ChatMessage> ChatMessages => Set<ChatMessage>();
    public DbSet<ChatFeedback> ChatFeedback => Set<ChatFeedback>();
    public DbSet<MaterialOverride> MaterialOverrides => Set<MaterialOverride>();
    public DbSet<TileTask> TileTasks => Set<TileTask>();
    public DbSet<TileTaskEvent> TileTaskEvents => Set<TileTaskEvent>();
    public DbSet<AppSetting> AppSettings => Set<AppSetting>();
    public DbSet<Experiment> Experiments => Set<Experiment>();
    public DbSet<ExperimentStep> ExperimentSteps => Set<ExperimentStep>();

    protected override void OnModelCreating(ModelBuilder modelBuilder)
    {
        modelBuilder.HasDefaultSchema("glazion");

        modelBuilder.Entity<AppUser>(entity =>
        {
            entity.HasKey(x => x.Id);
            entity.HasIndex(x => x.Username).IsUnique();
            entity.Property(x => x.Username).HasMaxLength(80).IsRequired();
            entity.Property(x => x.PasswordHash).HasMaxLength(512).IsRequired();
            entity.Property(x => x.Role).HasConversion<string>().HasMaxLength(16);
            entity.Property(x => x.Status).HasConversion<string>().HasMaxLength(16);
            entity.Property(x => x.Email).HasMaxLength(320);
            entity.HasIndex(x => x.Email).IsUnique().HasFilter("\"Email\" IS NOT NULL");
            entity.HasIndex(x => new { x.Status, x.CreatedAt });
            entity.Property(x => x.ParticipantCode).HasMaxLength(8);
            entity.HasIndex(x => x.ParticipantCode).IsUnique();
        });

        modelBuilder.Entity<BaseGlaze>(entity =>
        {
            entity.HasKey(x => x.Id);
            entity.Property(x => x.Cone).HasMaxLength(8);
            entity.Property(x => x.Atmosphere).HasMaxLength(16);
            entity.Property(x => x.ParticipantCode).HasMaxLength(8).IsRequired();
            entity.Property(x => x.Code).HasMaxLength(32).IsRequired();
            entity.Property(x => x.Name).HasMaxLength(180).IsRequired();
            entity.Property(x => x.FormulationJson).HasColumnType("jsonb");
            entity.Property(x => x.ChangeNote).HasMaxLength(500);
            entity.HasIndex(x => x.Code).IsUnique();
            entity.HasIndex(x => new { x.OwnerUserId, x.BaseNumber }).IsUnique();
            entity.HasOne(x => x.OwnerUser).WithMany(x => x.BaseGlazes).HasForeignKey(x => x.OwnerUserId).OnDelete(DeleteBehavior.Restrict);
            entity.HasOne(x => x.DerivedFromBase).WithMany().HasForeignKey(x => x.DerivedFromBaseId).OnDelete(DeleteBehavior.Restrict);
            entity.HasOne(x => x.PromotedFromRecipe).WithMany().HasForeignKey(x => x.PromotedFromRecipeId).OnDelete(DeleteBehavior.Restrict);
            entity.HasIndex(x => x.PromotedFromRecipeId).IsUnique().HasFilter("\"PromotedFromRecipeId\" IS NOT NULL");
        });

        modelBuilder.Entity<UserSession>(entity =>
        {
            entity.HasKey(x => x.Id);
            entity.HasIndex(x => x.TokenHash).IsUnique();
            entity.HasIndex(x => x.RefreshTokenHash).IsUnique().HasFilter("\"RefreshTokenHash\" IS NOT NULL");
            entity.HasIndex(x => new { x.UserId, x.ExpiresAt });
            entity.Property(x => x.TokenHash).HasMaxLength(64);
            entity.Property(x => x.RefreshTokenHash).HasMaxLength(64);
            // Two refreshes racing on one token: the second save fails instead of minting a second session.
            entity.Property(x => x.RevokedAt).IsConcurrencyToken();
            entity.HasOne(x => x.User).WithMany().HasForeignKey(x => x.UserId).OnDelete(DeleteBehavior.Cascade);
        });

        modelBuilder.Entity<CaptureCode>(entity =>
        {
            entity.HasKey(x => x.Id);
            entity.Property(x => x.CodeHash).HasMaxLength(64).IsRequired();
            entity.HasIndex(x => x.CodeHash).IsUnique();
            entity.Property(x => x.MaskedSuffix).HasMaxLength(8).IsRequired();
            entity.Property(x => x.LabelPrefix).HasMaxLength(32);
            entity.HasIndex(x => new { x.AssignedUserId, x.IsActive, x.CreatedAt });
            entity.HasOne(x => x.AssignedUser).WithMany(x => x.AssignedCodes).HasForeignKey(x => x.AssignedUserId).OnDelete(DeleteBehavior.Restrict);
        });

        modelBuilder.Entity<Recipe>(entity =>
        {
            entity.HasKey(x => x.Id);
            entity.Property(x => x.Cone).HasMaxLength(8);
            entity.Property(x => x.Atmosphere).HasMaxLength(16);
            entity.Property(x => x.Name).HasMaxLength(180).IsRequired();
            entity.Property(x => x.RecipeJson).HasColumnType("jsonb");
            entity.Property(x => x.SourceType).HasConversion<string>().HasMaxLength(16);
            entity.Property(x => x.SourceReference).HasMaxLength(256);
            entity.HasIndex(x => x.SourceReference);
            entity.Property(x => x.Code).HasMaxLength(32);
            entity.Property(x => x.AdditionsJson).HasColumnType("jsonb");
            entity.Property(x => x.OutcomeNote).HasMaxLength(500);
            entity.HasIndex(x => x.Code).IsUnique();
            entity.HasIndex(x => new { x.BaseGlazeId, x.RecipeNumber }).IsUnique();
            entity.HasIndex(x => x.OwnerUserId);
            entity.HasOne(x => x.BaseGlaze).WithMany(x => x.Recipes).HasForeignKey(x => x.BaseGlazeId).OnDelete(DeleteBehavior.Restrict);
            entity.ToTable(t => t.HasCheckConstraint("CK_Recipes_CodedTriple",
                "(\"BaseGlazeId\" IS NULL AND \"RecipeNumber\" IS NULL AND \"Code\" IS NULL) OR (\"BaseGlazeId\" IS NOT NULL AND \"RecipeNumber\" BETWEEN 1 AND 2000 AND \"Code\" IS NOT NULL)"));
        });

        modelBuilder.Entity<CodeRecipe>(entity =>
        {
            entity.HasKey(x => new { x.CaptureCodeId, x.RecipeId });
            entity.HasOne(x => x.CaptureCode).WithMany(x => x.Recipes).HasForeignKey(x => x.CaptureCodeId).OnDelete(DeleteBehavior.Cascade);
            entity.HasOne(x => x.Recipe).WithMany(x => x.Codes).HasForeignKey(x => x.RecipeId).OnDelete(DeleteBehavior.Cascade);
        });

        modelBuilder.Entity<Upload>(entity =>
        {
            entity.HasKey(x => x.Id);
            entity.HasIndex(x => new { x.OwnerUserId, x.CreatedAt });
            entity.HasIndex(x => new { x.CaptureCodeId, x.Status, x.CreatedAt });
            entity.HasIndex(x => x.ObjectKey).IsUnique();
            entity.Property(x => x.Status).HasConversion<string>().HasMaxLength(24);
            entity.Property(x => x.ObjectKey).HasMaxLength(512).IsRequired();
            entity.Property(x => x.ContentType).HasMaxLength(128).IsRequired();
            entity.Property(x => x.BackObjectKey).HasMaxLength(512);
            entity.Property(x => x.BackContentType).HasMaxLength(128);
            entity.Property(x => x.ReviewNote).HasMaxLength(2000);
            entity.Property(x => x.ClayBody).HasMaxLength(32);
            entity.HasIndex(x => new { x.Status, x.CreatedAt });
            entity.Property(x => x.ConcurrencyStamp).IsConcurrencyToken();
            entity.HasOne(x => x.OwnerUser).WithMany(x => x.Uploads).HasForeignKey(x => x.OwnerUserId).OnDelete(DeleteBehavior.Restrict);
            entity.HasOne(x => x.CaptureCode).WithMany(x => x.Uploads).HasForeignKey(x => x.CaptureCodeId).OnDelete(DeleteBehavior.Restrict);
            entity.HasOne(x => x.Recipe).WithMany().HasForeignKey(x => x.RecipeId).OnDelete(DeleteBehavior.Restrict);
            entity.HasOne(x => x.TileTask).WithMany(x => x.Uploads).HasForeignKey(x => x.TileTaskId).OnDelete(DeleteBehavior.SetNull);
            entity.HasIndex(x => new { x.TileTaskId, x.CreatedAt });
        });

        modelBuilder.Entity<TileTask>(entity =>
        {
            entity.HasKey(x => x.Id);
            entity.Property(x => x.ReturnNote).HasMaxLength(500);
            entity.Property(x => x.ClayBody).HasMaxLength(32).IsRequired();
            entity.Property(x => x.Note).HasMaxLength(500);
            entity.Property(x => x.ConcurrencyStamp).IsConcurrencyToken();
            // One live task per tile: a recipe on a clay body is with one participant at a time.
            entity.HasIndex(x => new { x.RecipeId, x.ClayBody }).IsUnique().HasFilter("\"CancelledAt\" IS NULL");
            entity.HasIndex(x => new { x.AssignedUserId, x.DueOn });
            entity.HasIndex(x => x.DueOn);
            entity.HasOne(x => x.Recipe).WithMany().HasForeignKey(x => x.RecipeId).OnDelete(DeleteBehavior.Restrict);
            entity.HasOne(x => x.AssignedUser).WithMany().HasForeignKey(x => x.AssignedUserId).OnDelete(DeleteBehavior.Restrict);
        });

        modelBuilder.Entity<TileTaskEvent>(entity =>
        {
            entity.HasKey(x => x.Id);
            entity.Property(x => x.Kind).HasConversion<string>().HasMaxLength(24);
            entity.Property(x => x.Note).HasMaxLength(500);
            entity.HasIndex(x => new { x.TileTaskId, x.At });
            entity.HasOne(x => x.TileTask).WithMany(x => x.Events).HasForeignKey(x => x.TileTaskId).OnDelete(DeleteBehavior.Cascade);
        });

        modelBuilder.Entity<RecipeCalculationResult>(entity =>
        {
            entity.Property(x => x.InputJson).HasColumnType("jsonb");
            entity.HasKey(x => x.Id);
            entity.Property(x => x.CalculationProfile).HasMaxLength(64).IsRequired();
            entity.Property(x => x.CalculationVersion).HasMaxLength(64).IsRequired();
            entity.Property(x => x.MaterialAnalysisVersion).HasMaxLength(64).IsRequired();
            entity.Property(x => x.ChemistrySchemaVersion).HasMaxLength(64).IsRequired();
            entity.Property(x => x.InputHash).HasMaxLength(64).IsRequired();
            entity.Property(x => x.FluxTotal).HasPrecision(18, 10);
            entity.Property(x => x.OxideWeightPercentJson).HasColumnType("jsonb");
            entity.Property(x => x.RawMolesJson).HasColumnType("jsonb");
            entity.Property(x => x.UmfJson).HasColumnType("jsonb");
            entity.Property(x => x.OxideGroupsJson).HasColumnType("jsonb");
            entity.Property(x => x.RatiosJson).HasColumnType("jsonb");
            entity.Property(x => x.WarningsJson).HasColumnType("jsonb");
            entity.HasIndex(x => new { x.RecipeId, x.CalculationProfile, x.InputHash }).IsUnique();
            entity.HasIndex(x => x.InputHash);
            entity.HasOne(x => x.Recipe).WithMany(x => x.CalculationResults).HasForeignKey(x => x.RecipeId).OnDelete(DeleteBehavior.Cascade);
        });

        modelBuilder.Entity<RecipeImage>(entity =>
        {
            entity.HasKey(x => new { x.RecipeId, x.ImageId });
            entity.Property(x => x.RelationshipType).HasConversion<string>().HasMaxLength(16);
            entity.HasOne(x => x.Recipe).WithMany(x => x.Images).HasForeignKey(x => x.RecipeId).OnDelete(DeleteBehavior.Cascade);
            entity.HasOne(x => x.Image).WithMany(x => x.Recipes).HasForeignKey(x => x.ImageId).OnDelete(DeleteBehavior.Restrict);
            entity.HasIndex(x => x.ImageId);
        });

        modelBuilder.Entity<ImageAsset>(entity =>
        {
            entity.HasKey(x => x.Id);
            entity.Property(x => x.BackKey).HasMaxLength(512);
            entity.Property(x => x.BackContentType).HasMaxLength(128);
            entity.Property(x => x.SourceType).HasConversion<string>().HasMaxLength(16);
            entity.Property(x => x.QualityStatus).HasConversion<string>().HasMaxLength(16);
            entity.Property(x => x.ApprovalStatus).HasConversion<string>().HasMaxLength(16);
            entity.Property(x => x.ChemistryStatus).HasConversion<string>().HasMaxLength(16);
            entity.Property(x => x.TileVariantKey).HasMaxLength(64).IsRequired();
            entity.Property(x => x.OriginalKey).HasMaxLength(512).IsRequired();
            entity.Property(x => x.DisplayKey).HasMaxLength(512);
            entity.Property(x => x.ThumbnailKey).HasMaxLength(512);
            entity.Property(x => x.DeclaredContentType).HasMaxLength(128).IsRequired();
            entity.Property(x => x.DeclaredSha256).HasMaxLength(64);
            entity.Property(x => x.Sha256).HasMaxLength(64);
            entity.Property(x => x.Format).HasMaxLength(16);
            entity.Property(x => x.ETag).HasMaxLength(128);
            entity.Property(x => x.QualityPolicyVersion).HasMaxLength(64);
            entity.Property(x => x.Atmosphere).HasMaxLength(32);
            entity.Property(x => x.ClayBody).HasMaxLength(32);
            entity.Property(x => x.ConeMin).HasPrecision(6, 2);
            entity.Property(x => x.ConeMax).HasPrecision(6, 2);
            entity.Property(x => x.QualityReasonsJson).HasColumnType("jsonb");
            entity.Property(x => x.QualityMetricsJson).HasColumnType("jsonb");
            entity.Property(x => x.LabelsJson).HasColumnType("jsonb");
            entity.Property(x => x.ConcurrencyStamp).IsConcurrencyToken();
            entity.HasIndex(x => x.OriginalKey).IsUnique();
            entity.HasIndex(x => x.UploadId).IsUnique();
            entity.HasIndex(x => x.ImportRecordId).IsUnique();
            entity.HasIndex(x => x.Sha256);
            entity.HasIndex(x => new { x.ApprovalStatus, x.QualityStatus, x.CreatedAt });
            entity.HasIndex(x => new { x.OwnerUserId, x.CreatedAt });
            entity.ToTable(t => t.HasCheckConstraint("CK_ImageAssets_OneProvenance",
                "(\"UploadId\" IS NOT NULL AND \"ImportRecordId\" IS NULL) OR (\"UploadId\" IS NULL AND \"ImportRecordId\" IS NOT NULL)"));
            entity.HasOne(x => x.Upload).WithOne(x => x.ImageAsset).HasForeignKey<ImageAsset>(x => x.UploadId).OnDelete(DeleteBehavior.Restrict);
            entity.HasOne(x => x.ImportRecord).WithOne(x => x.ImageAsset).HasForeignKey<ImageAsset>(x => x.ImportRecordId).OnDelete(DeleteBehavior.Restrict);
            entity.HasOne(x => x.CalculationResult).WithMany().HasForeignKey(x => x.CalculationResultId).OnDelete(DeleteBehavior.Restrict);
        });

        modelBuilder.Entity<ImportBatch>(entity =>
        {
            entity.HasKey(x => x.Id);
            entity.Property(x => x.Name).HasMaxLength(180).IsRequired();
            entity.Property(x => x.SourceLabel).HasMaxLength(64).IsRequired();
            entity.Property(x => x.DefaultTileVariantKey).HasMaxLength(64).IsRequired();
            entity.Property(x => x.Status).HasConversion<string>().HasMaxLength(16);
            entity.HasIndex(x => x.CreatedAt);
        });

        modelBuilder.Entity<ImportRecord>(entity =>
        {
            entity.HasKey(x => x.Id);
            entity.Property(x => x.ExternalRecordId).HasMaxLength(128).IsRequired();
            entity.Property(x => x.ObjectKey).HasMaxLength(512).IsRequired();
            entity.Property(x => x.FileName).HasMaxLength(255).IsRequired();
            entity.Property(x => x.DeclaredContentType).HasMaxLength(128).IsRequired();
            entity.Property(x => x.DeclaredSha256).HasMaxLength(64);
            entity.Property(x => x.TileVariantKey).HasMaxLength(64).IsRequired();
            entity.Property(x => x.Atmosphere).HasMaxLength(32);
            entity.Property(x => x.ClayBody).HasMaxLength(32);
            entity.Property(x => x.ConeMin).HasPrecision(6, 2);
            entity.Property(x => x.ConeMax).HasPrecision(6, 2);
            entity.Property(x => x.Status).HasConversion<string>().HasMaxLength(16);
            entity.Property(x => x.FailureCode).HasMaxLength(64);
            entity.Property(x => x.LabelsJson).HasColumnType("jsonb");
            entity.Property(x => x.UmfJson).HasColumnType("jsonb");
            entity.Property(x => x.ChemistryWarningsJson).HasColumnType("jsonb");
            entity.HasIndex(x => new { x.BatchId, x.ExternalRecordId }).IsUnique();
            entity.HasIndex(x => x.ObjectKey).IsUnique();
            entity.HasIndex(x => new { x.BatchId, x.Status });
            entity.HasOne(x => x.Batch).WithMany(x => x.Records).HasForeignKey(x => x.BatchId).OnDelete(DeleteBehavior.Cascade);
        });

        modelBuilder.Entity<IndexRecord>(entity =>
        {
            entity.HasKey(x => x.Id);
            entity.Property(x => x.EntityType).HasMaxLength(32).IsRequired();
            entity.Property(x => x.ApprovalStatus).HasConversion<string>().HasMaxLength(16);
            entity.Property(x => x.Modality).HasConversion<string>().HasMaxLength(16);
            entity.Property(x => x.ChemistrySchemaVersion).HasMaxLength(64);
            entity.Property(x => x.ChemistryVectorHash).HasMaxLength(64);
            entity.Property(x => x.CanonicalTextHash).HasMaxLength(64);
            entity.Property(x => x.ImageSha256).HasMaxLength(64);
            entity.Property(x => x.EmbeddingModel).HasMaxLength(128).IsRequired();
            entity.Property(x => x.EmbeddingVersion).HasMaxLength(64).IsRequired();
            entity.Property(x => x.VectorNamespace).HasMaxLength(128).IsRequired();
            entity.Property(x => x.VectorProvider).HasMaxLength(32);
            entity.Property(x => x.VectorId).HasMaxLength(255).IsRequired();
            entity.Property(x => x.LastErrorCode).HasMaxLength(64);
            entity.Property(x => x.MetadataJson).HasColumnType("jsonb");
            entity.HasIndex(x => new { x.EntityType, x.EntityId, x.EmbeddingVersion, x.VectorNamespace }).IsUnique();
            entity.HasIndex(x => new { x.VectorNamespace, x.VectorId }).IsUnique();
            entity.HasIndex(x => new { x.EntityType, x.Indexable, x.ApprovalStatus }).HasFilter("\"Indexable\" = TRUE");
        });

        modelBuilder.Entity<AiImageCopy>(entity =>
        {
            entity.HasKey(x => x.Id);
            entity.Property(x => x.Status).HasConversion<string>().HasMaxLength(16);
            entity.Property(x => x.Model).HasMaxLength(64).IsRequired();
            entity.Property(x => x.Quality).HasMaxLength(16).IsRequired();
            entity.Property(x => x.Instruction).HasMaxLength(500);
            entity.Property(x => x.Key).HasMaxLength(512);
            entity.Property(x => x.ThumbnailKey).HasMaxLength(512);
            entity.Property(x => x.UsageJson).HasColumnType("jsonb");
            entity.Property(x => x.Error).HasMaxLength(1000);
            entity.HasIndex(x => new { x.ImageId, x.CreatedAt });
            entity.HasIndex(x => new { x.Status, x.CreatedAt });
            entity.HasOne(x => x.Image).WithMany().HasForeignKey(x => x.ImageId).OnDelete(DeleteBehavior.Cascade);
        });

        modelBuilder.Entity<SearchRun>(entity =>
        {
            entity.HasKey(x => x.Id);
            entity.Property(x => x.QueryType).HasMaxLength(32).IsRequired();
            entity.Property(x => x.InputHash).HasMaxLength(64).IsRequired();
            entity.Property(x => x.IdempotencyKey).HasMaxLength(128);
            entity.Property(x => x.RankerVersion).HasMaxLength(64).IsRequired();
            entity.Property(x => x.QuerySnapshotJson).HasColumnType("jsonb");
            entity.Property(x => x.FiltersJson).HasColumnType("jsonb");
            entity.Property(x => x.ResultsJson).HasColumnType("jsonb");
            entity.Property(x => x.WarningsJson).HasColumnType("jsonb");
            entity.HasIndex(x => new { x.RequesterUserId, x.CreatedAt });
            // Match check lists everyone's searches, newest first.
            entity.HasIndex(x => x.CreatedAt);
            entity.HasIndex(x => new { x.RequesterUserId, x.IdempotencyKey }).IsUnique().HasFilter("\"IdempotencyKey\" IS NOT NULL");
            entity.HasIndex(x => x.InputHash);
            entity.ToTable(t => t.HasCheckConstraint("CK_SearchRuns_QueryType", "\"QueryType\" IN ('umf', 'recipe', 'raw_recipe')"));
            entity.HasOne<AppUser>().WithMany().HasForeignKey(x => x.RequesterUserId).OnDelete(DeleteBehavior.Restrict);
        });

        modelBuilder.Entity<SearchFeedback>(entity =>
        {
            entity.HasKey(x => x.Id);
            entity.Property(x => x.Feedback).HasMaxLength(32).IsRequired();
            entity.Property(x => x.Note).HasMaxLength(1000);
            entity.HasIndex(x => new { x.SearchRunId, x.ImageId, x.RequesterUserId }).IsUnique();
            entity.HasIndex(x => new { x.ImageId, x.Feedback });
            entity.ToTable(t => t.HasCheckConstraint("CK_SearchFeedback_Value", "\"Feedback\" IN ('relevant', 'not_relevant')"));
            entity.HasOne(x => x.SearchRun).WithMany().HasForeignKey(x => x.SearchRunId).OnDelete(DeleteBehavior.Cascade);
            entity.HasOne<ImageAsset>().WithMany().HasForeignKey(x => x.ImageId).OnDelete(DeleteBehavior.Restrict);
            entity.HasOne<AppUser>().WithMany().HasForeignKey(x => x.RequesterUserId).OnDelete(DeleteBehavior.Restrict);
        });

        modelBuilder.Entity<Conversation>(entity =>
        {
            entity.HasKey(x => x.Id);
            entity.Property(x => x.Title).HasMaxLength(200).IsRequired();
            entity.HasIndex(x => new { x.OwnerUserId, x.UpdatedAt });
            entity.HasOne<AppUser>().WithMany().HasForeignKey(x => x.OwnerUserId).OnDelete(DeleteBehavior.Cascade);
        });

        modelBuilder.Entity<ChatMessage>(entity =>
        {
            entity.HasKey(x => x.Id);
            entity.Property(x => x.Role).HasMaxLength(16).IsRequired();
            entity.Property(x => x.Content).IsRequired();
            entity.Property(x => x.SourcesJson).HasColumnType("jsonb");
            entity.Property(x => x.ToolCallsJson).HasColumnType("jsonb");
            entity.Property(x => x.Confidence).HasMaxLength(16);
            entity.Property(x => x.Model).HasMaxLength(100);
            entity.HasIndex(x => new { x.ConversationId, x.CreatedAt });
            entity.ToTable(t => t.HasCheckConstraint("CK_ChatMessages_Role", "\"Role\" IN ('user', 'assistant')"));
            entity.HasOne(x => x.Conversation).WithMany(x => x.Messages).HasForeignKey(x => x.ConversationId).OnDelete(DeleteBehavior.Cascade);
        });

        modelBuilder.Entity<ChatFeedback>(entity =>
        {
            entity.HasKey(x => x.Id);
            entity.Property(x => x.Feedback).HasMaxLength(32).IsRequired();
            entity.Property(x => x.Note).HasMaxLength(1000);
            entity.HasIndex(x => new { x.MessageId, x.UserId }).IsUnique();
            entity.ToTable(t => t.HasCheckConstraint("CK_ChatFeedback_Value", "\"Feedback\" IN ('helpful', 'not_helpful')"));
            entity.HasOne(x => x.Message).WithMany().HasForeignKey(x => x.MessageId).OnDelete(DeleteBehavior.Cascade);
            entity.HasOne<AppUser>().WithMany().HasForeignKey(x => x.UserId).OnDelete(DeleteBehavior.Restrict);
        });

        modelBuilder.Entity<Experiment>(entity =>
        {
            entity.HasKey(x => x.Id);
            entity.Property(x => x.Name).HasMaxLength(180).IsRequired();
            entity.Property(x => x.ClayBodiesJson).HasColumnType("jsonb");
            entity.Property(x => x.StagesJson).HasColumnType("jsonb");
            entity.Property(x => x.CupsJson).HasColumnType("jsonb");
            entity.HasOne(x => x.BaseGlaze).WithMany().HasForeignKey(x => x.BaseGlazeId).OnDelete(DeleteBehavior.Restrict);
            entity.HasOne(x => x.Participant).WithMany().HasForeignKey(x => x.ParticipantUserId).OnDelete(DeleteBehavior.Restrict);
            entity.HasIndex(x => x.ParticipantUserId);
            entity.ToTable(t => t.HasCheckConstraint("CK_Experiments_Cups", "\"CupCount\" BETWEEN 1 AND 20"));
        });

        modelBuilder.Entity<ExperimentStep>(entity =>
        {
            entity.HasKey(x => x.Id);
            entity.Property(x => x.Kind).HasMaxLength(16).IsRequired();
            entity.Property(x => x.Material).HasMaxLength(255);
            entity.Property(x => x.CupsJson).HasColumnType("jsonb");
            entity.HasOne(x => x.Experiment).WithMany(x => x.Steps).HasForeignKey(x => x.ExperimentId).OnDelete(DeleteBehavior.Cascade);
            entity.HasIndex(x => new { x.ExperimentId, x.Ordinal }).IsUnique();
        });

        modelBuilder.Entity<AppSetting>(entity =>
        {
            entity.HasKey(x => x.Key);
            entity.Property(x => x.Key).HasMaxLength(100);
            entity.Property(x => x.Value).HasMaxLength(1000).IsRequired();
        });

        modelBuilder.Entity<MaterialOverride>(entity =>
        {
            entity.HasKey(x => x.Id);
            entity.Property(x => x.MaterialId).HasMaxLength(255).IsRequired();
            entity.Property(x => x.Name).HasMaxLength(255).IsRequired();
            entity.Property(x => x.OxideAnalysisJson).HasColumnType("jsonb");
            entity.Property(x => x.Note).HasMaxLength(1000);
            entity.HasIndex(x => x.MaterialId).IsUnique();
        });

        modelBuilder.Entity<ProcessingJob>(entity =>
        {
            entity.HasKey(x => x.Id);
            entity.Property(x => x.JobType).HasMaxLength(64).IsRequired();
            entity.Property(x => x.EntityType).HasMaxLength(32).IsRequired();
            entity.Property(x => x.DedupeKey).HasMaxLength(255).IsRequired();
            entity.Property(x => x.Status).HasConversion<string>().HasMaxLength(16);
            entity.Property(x => x.ErrorCode).HasMaxLength(64);
            entity.Property(x => x.PayloadJson).HasColumnType("jsonb");
            entity.HasIndex(x => x.DedupeKey).IsUnique();
            entity.HasIndex(x => new { x.Status, x.AvailableAt }).HasFilter("\"Status\" = 'Queued'");
            entity.HasIndex(x => new { x.EntityType, x.EntityId });
        });
    }
}
