using System;
using Microsoft.EntityFrameworkCore.Migrations;

#nullable disable

namespace Glazion.Api.Data.Migrations
{
    /// <inheritdoc />
    public partial class AddAiImageCopies : Migration
    {
        /// <inheritdoc />
        protected override void Up(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.AddColumn<Guid>(
                name: "DisplayCopyId",
                schema: "glazion",
                table: "ImageAssets",
                type: "uuid",
                nullable: true);

            migrationBuilder.CreateTable(
                name: "AiImageCopies",
                schema: "glazion",
                columns: table => new
                {
                    Id = table.Column<Guid>(type: "uuid", nullable: false),
                    ImageId = table.Column<Guid>(type: "uuid", nullable: false),
                    Status = table.Column<string>(type: "character varying(16)", maxLength: 16, nullable: false),
                    Model = table.Column<string>(type: "character varying(64)", maxLength: 64, nullable: false),
                    Quality = table.Column<string>(type: "character varying(16)", maxLength: 16, nullable: false),
                    Prompt = table.Column<string>(type: "text", nullable: false),
                    Instruction = table.Column<string>(type: "character varying(500)", maxLength: 500, nullable: true),
                    Key = table.Column<string>(type: "character varying(512)", maxLength: 512, nullable: true),
                    ThumbnailKey = table.Column<string>(type: "character varying(512)", maxLength: 512, nullable: true),
                    Width = table.Column<int>(type: "integer", nullable: true),
                    Height = table.Column<int>(type: "integer", nullable: true),
                    UsageJson = table.Column<string>(type: "jsonb", nullable: true),
                    Error = table.Column<string>(type: "character varying(1000)", maxLength: 1000, nullable: true),
                    CreatedBy = table.Column<Guid>(type: "uuid", nullable: false),
                    CreatedAt = table.Column<DateTimeOffset>(type: "timestamp with time zone", nullable: false),
                    CompletedAt = table.Column<DateTimeOffset>(type: "timestamp with time zone", nullable: true),
                    ReviewedBy = table.Column<Guid>(type: "uuid", nullable: true),
                    ReviewedAt = table.Column<DateTimeOffset>(type: "timestamp with time zone", nullable: true)
                },
                constraints: table =>
                {
                    table.PrimaryKey("PK_AiImageCopies", x => x.Id);
                    table.ForeignKey(
                        name: "FK_AiImageCopies_ImageAssets_ImageId",
                        column: x => x.ImageId,
                        principalSchema: "glazion",
                        principalTable: "ImageAssets",
                        principalColumn: "Id",
                        onDelete: ReferentialAction.Cascade);
                });

            migrationBuilder.CreateIndex(
                name: "IX_AiImageCopies_ImageId_CreatedAt",
                schema: "glazion",
                table: "AiImageCopies",
                columns: new[] { "ImageId", "CreatedAt" });

            migrationBuilder.CreateIndex(
                name: "IX_AiImageCopies_Status_CreatedAt",
                schema: "glazion",
                table: "AiImageCopies",
                columns: new[] { "Status", "CreatedAt" });
        }

        /// <inheritdoc />
        protected override void Down(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.DropTable(
                name: "AiImageCopies",
                schema: "glazion");

            migrationBuilder.DropColumn(
                name: "DisplayCopyId",
                schema: "glazion",
                table: "ImageAssets");
        }
    }
}
