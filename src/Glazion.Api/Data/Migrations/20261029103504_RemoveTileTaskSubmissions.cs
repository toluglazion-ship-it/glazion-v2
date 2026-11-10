using System;
using Microsoft.EntityFrameworkCore.Migrations;

#nullable disable

namespace Glazion.Api.Data.Migrations
{
    /// <inheritdoc />
    public partial class RemoveTileTaskSubmissions : Migration
    {
        /// <inheritdoc />
        protected override void Up(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.DropIndex(
                name: "IX_TileTasks_SubmissionId",
                schema: "glazion",
                table: "TileTasks");

            migrationBuilder.DropColumn(
                name: "ReturnNote",
                schema: "glazion",
                table: "TileTasks");

            migrationBuilder.DropColumn(
                name: "SubmissionApprovedAt",
                schema: "glazion",
                table: "TileTasks");

            migrationBuilder.DropColumn(
                name: "SubmissionApprovedBy",
                schema: "glazion",
                table: "TileTasks");

            migrationBuilder.DropColumn(
                name: "SubmissionId",
                schema: "glazion",
                table: "TileTasks");

            migrationBuilder.DropColumn(
                name: "SubmittedAt",
                schema: "glazion",
                table: "TileTasks");
        }

        /// <inheritdoc />
        protected override void Down(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.AddColumn<string>(
                name: "ReturnNote",
                schema: "glazion",
                table: "TileTasks",
                type: "character varying(500)",
                maxLength: 500,
                nullable: true);

            migrationBuilder.AddColumn<DateTimeOffset>(
                name: "SubmissionApprovedAt",
                schema: "glazion",
                table: "TileTasks",
                type: "timestamp with time zone",
                nullable: true);

            migrationBuilder.AddColumn<Guid>(
                name: "SubmissionApprovedBy",
                schema: "glazion",
                table: "TileTasks",
                type: "uuid",
                nullable: true);

            migrationBuilder.AddColumn<Guid>(
                name: "SubmissionId",
                schema: "glazion",
                table: "TileTasks",
                type: "uuid",
                nullable: true);

            migrationBuilder.AddColumn<DateTimeOffset>(
                name: "SubmittedAt",
                schema: "glazion",
                table: "TileTasks",
                type: "timestamp with time zone",
                nullable: true);

            migrationBuilder.CreateIndex(
                name: "IX_TileTasks_SubmissionId",
                schema: "glazion",
                table: "TileTasks",
                column: "SubmissionId");
        }
    }
}
