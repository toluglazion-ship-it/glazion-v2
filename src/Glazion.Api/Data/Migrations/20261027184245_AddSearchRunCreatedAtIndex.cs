using Microsoft.EntityFrameworkCore.Migrations;

#nullable disable

namespace Glazion.Api.Data.Migrations
{
    /// <inheritdoc />
    public partial class AddSearchRunCreatedAtIndex : Migration
    {
        /// <inheritdoc />
        protected override void Up(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.CreateIndex(
                name: "IX_SearchRuns_CreatedAt",
                schema: "glazion",
                table: "SearchRuns",
                column: "CreatedAt");
        }

        /// <inheritdoc />
        protected override void Down(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.DropIndex(
                name: "IX_SearchRuns_CreatedAt",
                schema: "glazion",
                table: "SearchRuns");
        }
    }
}
