using System;
using Microsoft.EntityFrameworkCore.Migrations;

#nullable disable

namespace Glazion.Api.Data.Migrations
{
    /// <inheritdoc />
    public partial class AddClayBodyVariationPromotionAndMaterialOverrides : Migration
    {
        /// <inheritdoc />
        protected override void Up(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.AddColumn<string>(
                name: "ClayBody",
                schema: "glazion",
                table: "Uploads",
                type: "character varying(32)",
                maxLength: 32,
                nullable: true);

            migrationBuilder.AddColumn<string>(
                name: "ClayBody",
                schema: "glazion",
                table: "ImportRecords",
                type: "character varying(32)",
                maxLength: 32,
                nullable: true);

            migrationBuilder.AddColumn<string>(
                name: "ClayBody",
                schema: "glazion",
                table: "ImageAssets",
                type: "character varying(32)",
                maxLength: 32,
                nullable: true);

            migrationBuilder.AddColumn<Guid>(
                name: "PromotedFromRecipeId",
                schema: "glazion",
                table: "BaseGlazes",
                type: "uuid",
                nullable: true);

            migrationBuilder.CreateTable(
                name: "MaterialOverrides",
                schema: "glazion",
                columns: table => new
                {
                    Id = table.Column<Guid>(type: "uuid", nullable: false),
                    MaterialId = table.Column<string>(type: "character varying(255)", maxLength: 255, nullable: false),
                    Name = table.Column<string>(type: "character varying(255)", maxLength: 255, nullable: false),
                    OxideAnalysisJson = table.Column<string>(type: "jsonb", nullable: false),
                    Loi = table.Column<double>(type: "double precision", nullable: true),
                    Note = table.Column<string>(type: "character varying(1000)", maxLength: 1000, nullable: true),
                    CreatedBy = table.Column<Guid>(type: "uuid", nullable: false),
                    UpdatedBy = table.Column<Guid>(type: "uuid", nullable: false),
                    CreatedAt = table.Column<DateTimeOffset>(type: "timestamp with time zone", nullable: false),
                    UpdatedAt = table.Column<DateTimeOffset>(type: "timestamp with time zone", nullable: false)
                },
                constraints: table =>
                {
                    table.PrimaryKey("PK_MaterialOverrides", x => x.Id);
                });

            migrationBuilder.CreateIndex(
                name: "IX_BaseGlazes_PromotedFromRecipeId",
                schema: "glazion",
                table: "BaseGlazes",
                column: "PromotedFromRecipeId",
                unique: true,
                filter: "\"PromotedFromRecipeId\" IS NOT NULL");

            migrationBuilder.CreateIndex(
                name: "IX_MaterialOverrides_MaterialId",
                schema: "glazion",
                table: "MaterialOverrides",
                column: "MaterialId",
                unique: true);

            migrationBuilder.AddForeignKey(
                name: "FK_BaseGlazes_Recipes_PromotedFromRecipeId",
                schema: "glazion",
                table: "BaseGlazes",
                column: "PromotedFromRecipeId",
                principalSchema: "glazion",
                principalTable: "Recipes",
                principalColumn: "Id",
                onDelete: ReferentialAction.Restrict);
        }

        /// <inheritdoc />
        protected override void Down(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.DropForeignKey(
                name: "FK_BaseGlazes_Recipes_PromotedFromRecipeId",
                schema: "glazion",
                table: "BaseGlazes");

            migrationBuilder.DropTable(
                name: "MaterialOverrides",
                schema: "glazion");

            migrationBuilder.DropIndex(
                name: "IX_BaseGlazes_PromotedFromRecipeId",
                schema: "glazion",
                table: "BaseGlazes");

            migrationBuilder.DropColumn(
                name: "ClayBody",
                schema: "glazion",
                table: "Uploads");

            migrationBuilder.DropColumn(
                name: "ClayBody",
                schema: "glazion",
                table: "ImportRecords");

            migrationBuilder.DropColumn(
                name: "ClayBody",
                schema: "glazion",
                table: "ImageAssets");

            migrationBuilder.DropColumn(
                name: "PromotedFromRecipeId",
                schema: "glazion",
                table: "BaseGlazes");
        }
    }
}
