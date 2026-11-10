using Microsoft.EntityFrameworkCore.Migrations;

#nullable disable

namespace Glazion.Api.Data.Migrations
{
    /// <inheritdoc />
    public partial class AddCalculationInputsAndRawRecipeSearch : Migration
    {
        /// <inheritdoc />
        protected override void Up(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.DropCheckConstraint(
                name: "CK_SearchRuns_QueryType",
                schema: "glazion",
                table: "SearchRuns");

            migrationBuilder.AddColumn<string>(
                name: "InputJson",
                schema: "glazion",
                table: "RecipeCalculationResults",
                type: "jsonb",
                nullable: false,
                defaultValue: "{}");

            migrationBuilder.AddCheckConstraint(
                name: "CK_SearchRuns_QueryType",
                schema: "glazion",
                table: "SearchRuns",
                sql: "\"QueryType\" IN ('umf', 'recipe', 'raw_recipe')");
        }

        /// <inheritdoc />
        protected override void Down(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.DropCheckConstraint(
                name: "CK_SearchRuns_QueryType",
                schema: "glazion",
                table: "SearchRuns");

            migrationBuilder.DropColumn(
                name: "InputJson",
                schema: "glazion",
                table: "RecipeCalculationResults");

            migrationBuilder.AddCheckConstraint(
                name: "CK_SearchRuns_QueryType",
                schema: "glazion",
                table: "SearchRuns",
                sql: "\"QueryType\" IN ('umf', 'recipe')");
        }
    }
}
