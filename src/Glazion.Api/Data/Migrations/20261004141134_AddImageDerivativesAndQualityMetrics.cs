using System;
using Microsoft.EntityFrameworkCore.Migrations;

#nullable disable

namespace Glazion.Api.Data.Migrations
{
    /// <inheritdoc />
    public partial class AddImageDerivativesAndQualityMetrics : Migration
    {
        /// <inheritdoc />
        protected override void Up(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.AddColumn<DateTimeOffset>(
                name: "DerivativesCreatedAt",
                schema: "glazion",
                table: "ImageAssets",
                type: "timestamp with time zone",
                nullable: true);

            migrationBuilder.AddColumn<string>(
                name: "QualityMetricsJson",
                schema: "glazion",
                table: "ImageAssets",
                type: "jsonb",
                nullable: true);
        }

        /// <inheritdoc />
        protected override void Down(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.DropColumn(
                name: "DerivativesCreatedAt",
                schema: "glazion",
                table: "ImageAssets");

            migrationBuilder.DropColumn(
                name: "QualityMetricsJson",
                schema: "glazion",
                table: "ImageAssets");
        }
    }
}
