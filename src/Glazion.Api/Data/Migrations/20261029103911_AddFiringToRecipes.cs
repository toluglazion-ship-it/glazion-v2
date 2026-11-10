using Microsoft.EntityFrameworkCore.Migrations;

#nullable disable

namespace Glazion.Api.Data.Migrations
{
    /// <inheritdoc />
    public partial class AddFiringToRecipes : Migration
    {
        /// <inheritdoc />
        protected override void Up(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.AddColumn<string>(
                name: "Atmosphere",
                schema: "glazion",
                table: "Recipes",
                type: "character varying(16)",
                maxLength: 16,
                nullable: true);

            migrationBuilder.AddColumn<string>(
                name: "Cone",
                schema: "glazion",
                table: "Recipes",
                type: "character varying(8)",
                maxLength: 8,
                nullable: true);

            migrationBuilder.AddColumn<string>(
                name: "Atmosphere",
                schema: "glazion",
                table: "BaseGlazes",
                type: "character varying(16)",
                maxLength: 16,
                nullable: true);

            migrationBuilder.AddColumn<string>(
                name: "Cone",
                schema: "glazion",
                table: "BaseGlazes",
                type: "character varying(8)",
                maxLength: 8,
                nullable: true);
        }

        /// <inheritdoc />
        protected override void Down(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.DropColumn(
                name: "Atmosphere",
                schema: "glazion",
                table: "Recipes");

            migrationBuilder.DropColumn(
                name: "Cone",
                schema: "glazion",
                table: "Recipes");

            migrationBuilder.DropColumn(
                name: "Atmosphere",
                schema: "glazion",
                table: "BaseGlazes");

            migrationBuilder.DropColumn(
                name: "Cone",
                schema: "glazion",
                table: "BaseGlazes");
        }
    }
}
