using System;
using Microsoft.EntityFrameworkCore.Migrations;

#nullable disable

namespace Glazion.Api.Data.Migrations
{
    /// <inheritdoc />
    public partial class TileTaskStartAndApproval : Migration
    {
        /// <inheritdoc />
        protected override void Up(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.AddColumn<DateTimeOffset>(
                name: "ApprovedAt",
                schema: "glazion",
                table: "TileTasks",
                type: "timestamp with time zone",
                nullable: true);

            migrationBuilder.AddColumn<Guid>(
                name: "ApprovedBy",
                schema: "glazion",
                table: "TileTasks",
                type: "uuid",
                nullable: true);

            migrationBuilder.AddColumn<string>(
                name: "ReturnNote",
                schema: "glazion",
                table: "TileTasks",
                type: "character varying(500)",
                maxLength: 500,
                nullable: true);

            migrationBuilder.AddColumn<DateTimeOffset>(
                name: "StartedAt",
                schema: "glazion",
                table: "TileTasks",
                type: "timestamp with time zone",
                nullable: true);

            // A tile already made was started; a photo already sent means it was past any check.
            migrationBuilder.Sql("""
                UPDATE glazion."TileTasks" SET "StartedAt" = COALESCE("DoneAt", "AssignedAt")
                WHERE "DoneAt" IS NOT NULL OR EXISTS (SELECT 1 FROM glazion."Uploads" u WHERE u."TileTaskId" = "TileTasks"."Id");
                """);
        }

        /// <inheritdoc />
        protected override void Down(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.DropColumn(
                name: "ApprovedAt",
                schema: "glazion",
                table: "TileTasks");

            migrationBuilder.DropColumn(
                name: "ApprovedBy",
                schema: "glazion",
                table: "TileTasks");

            migrationBuilder.DropColumn(
                name: "ReturnNote",
                schema: "glazion",
                table: "TileTasks");

            migrationBuilder.DropColumn(
                name: "StartedAt",
                schema: "glazion",
                table: "TileTasks");
        }
    }
}
