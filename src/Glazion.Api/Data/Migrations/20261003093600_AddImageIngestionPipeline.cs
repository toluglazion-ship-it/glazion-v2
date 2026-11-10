using System;
using Microsoft.EntityFrameworkCore.Migrations;

#nullable disable

namespace Glazion.Api.Data.Migrations
{
    /// <inheritdoc />
    public partial class AddImageIngestionPipeline : Migration
    {
        /// <inheritdoc />
        protected override void Up(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.AddColumn<string>(
                name: "SourceReference",
                schema: "glazion",
                table: "Recipes",
                type: "character varying(256)",
                maxLength: 256,
                nullable: true);

            migrationBuilder.AddColumn<string>(
                name: "SourceType",
                schema: "glazion",
                table: "Recipes",
                type: "character varying(16)",
                maxLength: 16,
                nullable: false,
                defaultValue: "Manual");

            migrationBuilder.CreateTable(
                name: "ImportBatches",
                schema: "glazion",
                columns: table => new
                {
                    Id = table.Column<Guid>(type: "uuid", nullable: false),
                    Name = table.Column<string>(type: "character varying(180)", maxLength: 180, nullable: false),
                    SourceLabel = table.Column<string>(type: "character varying(64)", maxLength: 64, nullable: false),
                    DefaultTileVariantKey = table.Column<string>(type: "character varying(64)", maxLength: 64, nullable: false),
                    AutoApprove = table.Column<bool>(type: "boolean", nullable: false),
                    Status = table.Column<string>(type: "character varying(16)", maxLength: 16, nullable: false),
                    CreatedBy = table.Column<Guid>(type: "uuid", nullable: false),
                    CreatedAt = table.Column<DateTimeOffset>(type: "timestamp with time zone", nullable: false),
                    ClosedAt = table.Column<DateTimeOffset>(type: "timestamp with time zone", nullable: true)
                },
                constraints: table =>
                {
                    table.PrimaryKey("PK_ImportBatches", x => x.Id);
                });

            migrationBuilder.CreateTable(
                name: "ProcessingJobs",
                schema: "glazion",
                columns: table => new
                {
                    Id = table.Column<Guid>(type: "uuid", nullable: false),
                    JobType = table.Column<string>(type: "character varying(64)", maxLength: 64, nullable: false),
                    EntityType = table.Column<string>(type: "character varying(32)", maxLength: 32, nullable: false),
                    EntityId = table.Column<Guid>(type: "uuid", nullable: false),
                    Status = table.Column<string>(type: "character varying(16)", maxLength: 16, nullable: false),
                    Attempts = table.Column<int>(type: "integer", nullable: false),
                    MaxAttempts = table.Column<int>(type: "integer", nullable: false),
                    DedupeKey = table.Column<string>(type: "character varying(255)", maxLength: 255, nullable: false),
                    PayloadJson = table.Column<string>(type: "jsonb", nullable: false),
                    ErrorCode = table.Column<string>(type: "character varying(64)", maxLength: 64, nullable: true),
                    ErrorDetail = table.Column<string>(type: "text", nullable: true),
                    AvailableAt = table.Column<DateTimeOffset>(type: "timestamp with time zone", nullable: false),
                    StartedAt = table.Column<DateTimeOffset>(type: "timestamp with time zone", nullable: true),
                    LeaseExpiresAt = table.Column<DateTimeOffset>(type: "timestamp with time zone", nullable: true),
                    CompletedAt = table.Column<DateTimeOffset>(type: "timestamp with time zone", nullable: true),
                    CreatedAt = table.Column<DateTimeOffset>(type: "timestamp with time zone", nullable: false)
                },
                constraints: table =>
                {
                    table.PrimaryKey("PK_ProcessingJobs", x => x.Id);
                });

            migrationBuilder.CreateTable(
                name: "RecipeCalculationResults",
                schema: "glazion",
                columns: table => new
                {
                    Id = table.Column<Guid>(type: "uuid", nullable: false),
                    RecipeId = table.Column<Guid>(type: "uuid", nullable: false),
                    CalculationProfile = table.Column<string>(type: "character varying(64)", maxLength: 64, nullable: false),
                    CalculationVersion = table.Column<string>(type: "character varying(64)", maxLength: 64, nullable: false),
                    MaterialAnalysisVersion = table.Column<string>(type: "character varying(64)", maxLength: 64, nullable: false),
                    ChemistrySchemaVersion = table.Column<string>(type: "character varying(64)", maxLength: 64, nullable: false),
                    InputHash = table.Column<string>(type: "character varying(64)", maxLength: 64, nullable: false),
                    OxideWeightPercentJson = table.Column<string>(type: "jsonb", nullable: false),
                    RawMolesJson = table.Column<string>(type: "jsonb", nullable: false),
                    FluxTotal = table.Column<decimal>(type: "numeric(18,10)", precision: 18, scale: 10, nullable: false),
                    UmfJson = table.Column<string>(type: "jsonb", nullable: false),
                    OxideGroupsJson = table.Column<string>(type: "jsonb", nullable: false),
                    RatiosJson = table.Column<string>(type: "jsonb", nullable: false),
                    WarningsJson = table.Column<string>(type: "jsonb", nullable: false),
                    CreatedAt = table.Column<DateTimeOffset>(type: "timestamp with time zone", nullable: false)
                },
                constraints: table =>
                {
                    table.PrimaryKey("PK_RecipeCalculationResults", x => x.Id);
                    table.ForeignKey(
                        name: "FK_RecipeCalculationResults_Recipes_RecipeId",
                        column: x => x.RecipeId,
                        principalSchema: "glazion",
                        principalTable: "Recipes",
                        principalColumn: "Id",
                        onDelete: ReferentialAction.Cascade);
                });

            migrationBuilder.CreateTable(
                name: "ImportRecords",
                schema: "glazion",
                columns: table => new
                {
                    Id = table.Column<Guid>(type: "uuid", nullable: false),
                    BatchId = table.Column<Guid>(type: "uuid", nullable: false),
                    ExternalRecordId = table.Column<string>(type: "character varying(128)", maxLength: 128, nullable: false),
                    ObjectKey = table.Column<string>(type: "character varying(512)", maxLength: 512, nullable: false),
                    FileName = table.Column<string>(type: "character varying(255)", maxLength: 255, nullable: false),
                    DeclaredContentType = table.Column<string>(type: "character varying(128)", maxLength: 128, nullable: false),
                    DeclaredSizeBytes = table.Column<long>(type: "bigint", nullable: false),
                    DeclaredSha256 = table.Column<string>(type: "character varying(64)", maxLength: 64, nullable: true),
                    TileVariantKey = table.Column<string>(type: "character varying(64)", maxLength: 64, nullable: false),
                    ConeMin = table.Column<decimal>(type: "numeric(6,2)", precision: 6, scale: 2, nullable: true),
                    ConeMax = table.Column<decimal>(type: "numeric(6,2)", precision: 6, scale: 2, nullable: true),
                    Atmosphere = table.Column<string>(type: "character varying(32)", maxLength: 32, nullable: true),
                    LabelsJson = table.Column<string>(type: "jsonb", nullable: false),
                    RecipeId = table.Column<Guid>(type: "uuid", nullable: true),
                    UmfJson = table.Column<string>(type: "jsonb", nullable: true),
                    ChemistryWarningsJson = table.Column<string>(type: "jsonb", nullable: false),
                    Status = table.Column<string>(type: "character varying(16)", maxLength: 16, nullable: false),
                    FailureCode = table.Column<string>(type: "character varying(64)", maxLength: 64, nullable: true),
                    FailureDetail = table.Column<string>(type: "text", nullable: true),
                    CreatedAt = table.Column<DateTimeOffset>(type: "timestamp with time zone", nullable: false),
                    UploadedAt = table.Column<DateTimeOffset>(type: "timestamp with time zone", nullable: true)
                },
                constraints: table =>
                {
                    table.PrimaryKey("PK_ImportRecords", x => x.Id);
                    table.ForeignKey(
                        name: "FK_ImportRecords_ImportBatches_BatchId",
                        column: x => x.BatchId,
                        principalSchema: "glazion",
                        principalTable: "ImportBatches",
                        principalColumn: "Id",
                        onDelete: ReferentialAction.Cascade);
                });

            migrationBuilder.CreateTable(
                name: "ImageAssets",
                schema: "glazion",
                columns: table => new
                {
                    Id = table.Column<Guid>(type: "uuid", nullable: false),
                    SourceType = table.Column<string>(type: "character varying(16)", maxLength: 16, nullable: false),
                    UploadId = table.Column<Guid>(type: "uuid", nullable: true),
                    ImportRecordId = table.Column<Guid>(type: "uuid", nullable: true),
                    OwnerUserId = table.Column<Guid>(type: "uuid", nullable: true),
                    TileVariantKey = table.Column<string>(type: "character varying(64)", maxLength: 64, nullable: false),
                    OriginalKey = table.Column<string>(type: "character varying(512)", maxLength: 512, nullable: false),
                    DisplayKey = table.Column<string>(type: "character varying(512)", maxLength: 512, nullable: true),
                    ThumbnailKey = table.Column<string>(type: "character varying(512)", maxLength: 512, nullable: true),
                    DeclaredContentType = table.Column<string>(type: "character varying(128)", maxLength: 128, nullable: false),
                    DeclaredSizeBytes = table.Column<long>(type: "bigint", nullable: false),
                    DeclaredSha256 = table.Column<string>(type: "character varying(64)", maxLength: 64, nullable: true),
                    Format = table.Column<string>(type: "character varying(16)", maxLength: 16, nullable: true),
                    Width = table.Column<int>(type: "integer", nullable: true),
                    Height = table.Column<int>(type: "integer", nullable: true),
                    SizeBytes = table.Column<long>(type: "bigint", nullable: true),
                    Sha256 = table.Column<string>(type: "character varying(64)", maxLength: 64, nullable: true),
                    ETag = table.Column<string>(type: "character varying(128)", maxLength: 128, nullable: true),
                    QualityStatus = table.Column<string>(type: "character varying(16)", maxLength: 16, nullable: false),
                    QualityReasonsJson = table.Column<string>(type: "jsonb", nullable: false),
                    QualityPolicyVersion = table.Column<string>(type: "character varying(64)", maxLength: 64, nullable: true),
                    ApprovalStatus = table.Column<string>(type: "character varying(16)", maxLength: 16, nullable: false),
                    ApprovedAt = table.Column<DateTimeOffset>(type: "timestamp with time zone", nullable: true),
                    ApprovedBy = table.Column<Guid>(type: "uuid", nullable: true),
                    ChemistryStatus = table.Column<string>(type: "character varying(16)", maxLength: 16, nullable: false),
                    CalculationResultId = table.Column<Guid>(type: "uuid", nullable: true),
                    DuplicateOfImageId = table.Column<Guid>(type: "uuid", nullable: true),
                    ConeMin = table.Column<decimal>(type: "numeric(6,2)", precision: 6, scale: 2, nullable: true),
                    ConeMax = table.Column<decimal>(type: "numeric(6,2)", precision: 6, scale: 2, nullable: true),
                    Atmosphere = table.Column<string>(type: "character varying(32)", maxLength: 32, nullable: true),
                    LabelsJson = table.Column<string>(type: "jsonb", nullable: false),
                    SourceVersion = table.Column<long>(type: "bigint", nullable: false),
                    CreatedAt = table.Column<DateTimeOffset>(type: "timestamp with time zone", nullable: false),
                    UpdatedAt = table.Column<DateTimeOffset>(type: "timestamp with time zone", nullable: false),
                    ValidatedAt = table.Column<DateTimeOffset>(type: "timestamp with time zone", nullable: true),
                    ConcurrencyStamp = table.Column<Guid>(type: "uuid", nullable: false)
                },
                constraints: table =>
                {
                    table.PrimaryKey("PK_ImageAssets", x => x.Id);
                    table.CheckConstraint("CK_ImageAssets_OneProvenance", "(\"UploadId\" IS NOT NULL AND \"ImportRecordId\" IS NULL) OR (\"UploadId\" IS NULL AND \"ImportRecordId\" IS NOT NULL)");
                    table.ForeignKey(
                        name: "FK_ImageAssets_ImportRecords_ImportRecordId",
                        column: x => x.ImportRecordId,
                        principalSchema: "glazion",
                        principalTable: "ImportRecords",
                        principalColumn: "Id",
                        onDelete: ReferentialAction.Restrict);
                    table.ForeignKey(
                        name: "FK_ImageAssets_RecipeCalculationResults_CalculationResultId",
                        column: x => x.CalculationResultId,
                        principalSchema: "glazion",
                        principalTable: "RecipeCalculationResults",
                        principalColumn: "Id",
                        onDelete: ReferentialAction.Restrict);
                    table.ForeignKey(
                        name: "FK_ImageAssets_Uploads_UploadId",
                        column: x => x.UploadId,
                        principalSchema: "glazion",
                        principalTable: "Uploads",
                        principalColumn: "Id",
                        onDelete: ReferentialAction.Restrict);
                });

            migrationBuilder.CreateTable(
                name: "RecipeImages",
                schema: "glazion",
                columns: table => new
                {
                    RecipeId = table.Column<Guid>(type: "uuid", nullable: false),
                    ImageId = table.Column<Guid>(type: "uuid", nullable: false),
                    RelationshipType = table.Column<string>(type: "character varying(16)", maxLength: 16, nullable: false),
                    Verified = table.Column<bool>(type: "boolean", nullable: false),
                    CreatedBy = table.Column<Guid>(type: "uuid", nullable: true),
                    CreatedAt = table.Column<DateTimeOffset>(type: "timestamp with time zone", nullable: false)
                },
                constraints: table =>
                {
                    table.PrimaryKey("PK_RecipeImages", x => new { x.RecipeId, x.ImageId });
                    table.ForeignKey(
                        name: "FK_RecipeImages_ImageAssets_ImageId",
                        column: x => x.ImageId,
                        principalSchema: "glazion",
                        principalTable: "ImageAssets",
                        principalColumn: "Id",
                        onDelete: ReferentialAction.Restrict);
                    table.ForeignKey(
                        name: "FK_RecipeImages_Recipes_RecipeId",
                        column: x => x.RecipeId,
                        principalSchema: "glazion",
                        principalTable: "Recipes",
                        principalColumn: "Id",
                        onDelete: ReferentialAction.Cascade);
                });

            migrationBuilder.CreateIndex(
                name: "IX_Recipes_SourceReference",
                schema: "glazion",
                table: "Recipes",
                column: "SourceReference");

            migrationBuilder.CreateIndex(
                name: "IX_ImageAssets_ApprovalStatus_QualityStatus_CreatedAt",
                schema: "glazion",
                table: "ImageAssets",
                columns: new[] { "ApprovalStatus", "QualityStatus", "CreatedAt" });

            migrationBuilder.CreateIndex(
                name: "IX_ImageAssets_CalculationResultId",
                schema: "glazion",
                table: "ImageAssets",
                column: "CalculationResultId");

            migrationBuilder.CreateIndex(
                name: "IX_ImageAssets_ImportRecordId",
                schema: "glazion",
                table: "ImageAssets",
                column: "ImportRecordId",
                unique: true);

            migrationBuilder.CreateIndex(
                name: "IX_ImageAssets_OriginalKey",
                schema: "glazion",
                table: "ImageAssets",
                column: "OriginalKey",
                unique: true);

            migrationBuilder.CreateIndex(
                name: "IX_ImageAssets_OwnerUserId_CreatedAt",
                schema: "glazion",
                table: "ImageAssets",
                columns: new[] { "OwnerUserId", "CreatedAt" });

            migrationBuilder.CreateIndex(
                name: "IX_ImageAssets_Sha256",
                schema: "glazion",
                table: "ImageAssets",
                column: "Sha256");

            migrationBuilder.CreateIndex(
                name: "IX_ImageAssets_UploadId",
                schema: "glazion",
                table: "ImageAssets",
                column: "UploadId",
                unique: true);

            migrationBuilder.CreateIndex(
                name: "IX_ImportBatches_CreatedAt",
                schema: "glazion",
                table: "ImportBatches",
                column: "CreatedAt");

            migrationBuilder.CreateIndex(
                name: "IX_ImportRecords_BatchId_ExternalRecordId",
                schema: "glazion",
                table: "ImportRecords",
                columns: new[] { "BatchId", "ExternalRecordId" },
                unique: true);

            migrationBuilder.CreateIndex(
                name: "IX_ImportRecords_BatchId_Status",
                schema: "glazion",
                table: "ImportRecords",
                columns: new[] { "BatchId", "Status" });

            migrationBuilder.CreateIndex(
                name: "IX_ImportRecords_ObjectKey",
                schema: "glazion",
                table: "ImportRecords",
                column: "ObjectKey",
                unique: true);

            migrationBuilder.CreateIndex(
                name: "IX_ProcessingJobs_DedupeKey",
                schema: "glazion",
                table: "ProcessingJobs",
                column: "DedupeKey",
                unique: true);

            migrationBuilder.CreateIndex(
                name: "IX_ProcessingJobs_EntityType_EntityId",
                schema: "glazion",
                table: "ProcessingJobs",
                columns: new[] { "EntityType", "EntityId" });

            migrationBuilder.CreateIndex(
                name: "IX_ProcessingJobs_Status_AvailableAt",
                schema: "glazion",
                table: "ProcessingJobs",
                columns: new[] { "Status", "AvailableAt" },
                filter: "\"Status\" = 'Queued'");

            migrationBuilder.CreateIndex(
                name: "IX_RecipeCalculationResults_InputHash",
                schema: "glazion",
                table: "RecipeCalculationResults",
                column: "InputHash");

            migrationBuilder.CreateIndex(
                name: "IX_RecipeCalculationResults_RecipeId_CalculationProfile_InputH~",
                schema: "glazion",
                table: "RecipeCalculationResults",
                columns: new[] { "RecipeId", "CalculationProfile", "InputHash" },
                unique: true);

            migrationBuilder.CreateIndex(
                name: "IX_RecipeImages_ImageId",
                schema: "glazion",
                table: "RecipeImages",
                column: "ImageId");
        }

        /// <inheritdoc />
        protected override void Down(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.DropTable(
                name: "ProcessingJobs",
                schema: "glazion");

            migrationBuilder.DropTable(
                name: "RecipeImages",
                schema: "glazion");

            migrationBuilder.DropTable(
                name: "ImageAssets",
                schema: "glazion");

            migrationBuilder.DropTable(
                name: "ImportRecords",
                schema: "glazion");

            migrationBuilder.DropTable(
                name: "RecipeCalculationResults",
                schema: "glazion");

            migrationBuilder.DropTable(
                name: "ImportBatches",
                schema: "glazion");

            migrationBuilder.DropIndex(
                name: "IX_Recipes_SourceReference",
                schema: "glazion",
                table: "Recipes");

            migrationBuilder.DropColumn(
                name: "SourceReference",
                schema: "glazion",
                table: "Recipes");

            migrationBuilder.DropColumn(
                name: "SourceType",
                schema: "glazion",
                table: "Recipes");
        }
    }
}
