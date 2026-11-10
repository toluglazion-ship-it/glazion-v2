using System;
using Microsoft.EntityFrameworkCore.Migrations;

#nullable disable

namespace Glazion.Api.Data.Migrations
{
    /// <inheritdoc />
    public partial class AddIndexRecords : Migration
    {
        /// <inheritdoc />
        protected override void Up(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.CreateTable(
                name: "IndexRecords",
                schema: "glazion",
                columns: table => new
                {
                    Id = table.Column<Guid>(type: "uuid", nullable: false),
                    EntityType = table.Column<string>(type: "character varying(32)", maxLength: 32, nullable: false),
                    EntityId = table.Column<Guid>(type: "uuid", nullable: false),
                    SourceVersion = table.Column<long>(type: "bigint", nullable: false),
                    Indexable = table.Column<bool>(type: "boolean", nullable: false),
                    ApprovalStatus = table.Column<string>(type: "character varying(16)", maxLength: 16, nullable: false),
                    Modality = table.Column<string>(type: "character varying(16)", maxLength: 16, nullable: false),
                    ChemistrySchemaVersion = table.Column<string>(type: "character varying(64)", maxLength: 64, nullable: true),
                    ChemistryVectorHash = table.Column<string>(type: "character varying(64)", maxLength: 64, nullable: true),
                    CanonicalTextHash = table.Column<string>(type: "character varying(64)", maxLength: 64, nullable: true),
                    ImageSha256 = table.Column<string>(type: "character varying(64)", maxLength: 64, nullable: true),
                    EmbeddingModel = table.Column<string>(type: "character varying(128)", maxLength: 128, nullable: false),
                    EmbeddingVersion = table.Column<string>(type: "character varying(64)", maxLength: 64, nullable: false),
                    VectorNamespace = table.Column<string>(type: "character varying(128)", maxLength: 128, nullable: false),
                    VectorId = table.Column<string>(type: "character varying(255)", maxLength: 255, nullable: false),
                    MetadataJson = table.Column<string>(type: "jsonb", nullable: false),
                    IndexedAt = table.Column<DateTimeOffset>(type: "timestamp with time zone", nullable: true),
                    SupersededAt = table.Column<DateTimeOffset>(type: "timestamp with time zone", nullable: true),
                    LastErrorCode = table.Column<string>(type: "character varying(64)", maxLength: 64, nullable: true),
                    LastErrorDetail = table.Column<string>(type: "text", nullable: true),
                    CreatedAt = table.Column<DateTimeOffset>(type: "timestamp with time zone", nullable: false),
                    UpdatedAt = table.Column<DateTimeOffset>(type: "timestamp with time zone", nullable: false)
                },
                constraints: table =>
                {
                    table.PrimaryKey("PK_IndexRecords", x => x.Id);
                });

            migrationBuilder.CreateIndex(
                name: "IX_IndexRecords_EntityType_EntityId_EmbeddingVersion_VectorNam~",
                schema: "glazion",
                table: "IndexRecords",
                columns: new[] { "EntityType", "EntityId", "EmbeddingVersion", "VectorNamespace" },
                unique: true);

            migrationBuilder.CreateIndex(
                name: "IX_IndexRecords_EntityType_Indexable_ApprovalStatus",
                schema: "glazion",
                table: "IndexRecords",
                columns: new[] { "EntityType", "Indexable", "ApprovalStatus" },
                filter: "\"Indexable\" = TRUE");

            migrationBuilder.CreateIndex(
                name: "IX_IndexRecords_VectorNamespace_VectorId",
                schema: "glazion",
                table: "IndexRecords",
                columns: new[] { "VectorNamespace", "VectorId" },
                unique: true);
        }

        /// <inheritdoc />
        protected override void Down(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.DropTable(
                name: "IndexRecords",
                schema: "glazion");
        }
    }
}
