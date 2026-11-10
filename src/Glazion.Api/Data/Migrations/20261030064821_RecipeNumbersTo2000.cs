using Microsoft.EntityFrameworkCore.Migrations;

#nullable disable

namespace Glazion.Api.Data.Migrations
{
    /// <inheritdoc />
    public partial class RecipeNumbersTo2000 : Migration
    {
        /// <inheritdoc />
        protected override void Up(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.DropCheckConstraint(
                name: "CK_Recipes_CodedTriple",
                schema: "glazion",
                table: "Recipes");

            migrationBuilder.AddCheckConstraint(
                name: "CK_Recipes_CodedTriple",
                schema: "glazion",
                table: "Recipes",
                sql: "(\"BaseGlazeId\" IS NULL AND \"RecipeNumber\" IS NULL AND \"Code\" IS NULL) OR (\"BaseGlazeId\" IS NOT NULL AND \"RecipeNumber\" BETWEEN 1 AND 2000 AND \"Code\" IS NOT NULL)");
        }

        /// <inheritdoc />
        protected override void Down(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.DropCheckConstraint(
                name: "CK_Recipes_CodedTriple",
                schema: "glazion",
                table: "Recipes");

            migrationBuilder.AddCheckConstraint(
                name: "CK_Recipes_CodedTriple",
                schema: "glazion",
                table: "Recipes",
                sql: "(\"BaseGlazeId\" IS NULL AND \"RecipeNumber\" IS NULL AND \"Code\" IS NULL) OR (\"BaseGlazeId\" IS NOT NULL AND \"RecipeNumber\" BETWEEN 1 AND 999 AND \"Code\" IS NOT NULL)");
        }
    }
}
