using System;
using Microsoft.EntityFrameworkCore.Migrations;

#nullable disable

namespace Glazion.Api.Data.Migrations
{
    /// <inheritdoc />
    public partial class AddTileTasks : Migration
    {
        /// <inheritdoc />
        protected override void Up(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.AddColumn<Guid>(
                name: "TileTaskId",
                schema: "glazion",
                table: "Uploads",
                type: "uuid",
                nullable: true);

            migrationBuilder.CreateTable(
                name: "TileTasks",
                schema: "glazion",
                columns: table => new
                {
                    Id = table.Column<Guid>(type: "uuid", nullable: false),
                    RecipeId = table.Column<Guid>(type: "uuid", nullable: false),
                    ClayBody = table.Column<string>(type: "character varying(32)", maxLength: 32, nullable: false),
                    AssignedUserId = table.Column<Guid>(type: "uuid", nullable: false),
                    AssignedAt = table.Column<DateTimeOffset>(type: "timestamp with time zone", nullable: false),
                    DueOn = table.Column<DateOnly>(type: "date", nullable: true),
                    Note = table.Column<string>(type: "character varying(500)", maxLength: 500, nullable: true),
                    DoneAt = table.Column<DateTimeOffset>(type: "timestamp with time zone", nullable: true),
                    CancelledAt = table.Column<DateTimeOffset>(type: "timestamp with time zone", nullable: true),
                    CreatedBy = table.Column<Guid>(type: "uuid", nullable: false),
                    CreatedAt = table.Column<DateTimeOffset>(type: "timestamp with time zone", nullable: false),
                    UpdatedAt = table.Column<DateTimeOffset>(type: "timestamp with time zone", nullable: false),
                    ConcurrencyStamp = table.Column<Guid>(type: "uuid", nullable: false)
                },
                constraints: table =>
                {
                    table.PrimaryKey("PK_TileTasks", x => x.Id);
                    table.ForeignKey(
                        name: "FK_TileTasks_Recipes_RecipeId",
                        column: x => x.RecipeId,
                        principalSchema: "glazion",
                        principalTable: "Recipes",
                        principalColumn: "Id",
                        onDelete: ReferentialAction.Restrict);
                    table.ForeignKey(
                        name: "FK_TileTasks_Users_AssignedUserId",
                        column: x => x.AssignedUserId,
                        principalSchema: "glazion",
                        principalTable: "Users",
                        principalColumn: "Id",
                        onDelete: ReferentialAction.Restrict);
                });

            migrationBuilder.CreateTable(
                name: "TileTaskEvents",
                schema: "glazion",
                columns: table => new
                {
                    Id = table.Column<Guid>(type: "uuid", nullable: false),
                    TileTaskId = table.Column<Guid>(type: "uuid", nullable: false),
                    Kind = table.Column<string>(type: "character varying(24)", maxLength: 24, nullable: false),
                    ActorUserId = table.Column<Guid>(type: "uuid", nullable: false),
                    FromUserId = table.Column<Guid>(type: "uuid", nullable: true),
                    ToUserId = table.Column<Guid>(type: "uuid", nullable: true),
                    Note = table.Column<string>(type: "character varying(500)", maxLength: 500, nullable: true),
                    At = table.Column<DateTimeOffset>(type: "timestamp with time zone", nullable: false)
                },
                constraints: table =>
                {
                    table.PrimaryKey("PK_TileTaskEvents", x => x.Id);
                    table.ForeignKey(
                        name: "FK_TileTaskEvents_TileTasks_TileTaskId",
                        column: x => x.TileTaskId,
                        principalSchema: "glazion",
                        principalTable: "TileTasks",
                        principalColumn: "Id",
                        onDelete: ReferentialAction.Cascade);
                });

            migrationBuilder.CreateIndex(
                name: "IX_Uploads_TileTaskId_CreatedAt",
                schema: "glazion",
                table: "Uploads",
                columns: new[] { "TileTaskId", "CreatedAt" });

            migrationBuilder.CreateIndex(
                name: "IX_TileTaskEvents_TileTaskId_At",
                schema: "glazion",
                table: "TileTaskEvents",
                columns: new[] { "TileTaskId", "At" });

            migrationBuilder.CreateIndex(
                name: "IX_TileTasks_AssignedUserId_DueOn",
                schema: "glazion",
                table: "TileTasks",
                columns: new[] { "AssignedUserId", "DueOn" });

            migrationBuilder.CreateIndex(
                name: "IX_TileTasks_DueOn",
                schema: "glazion",
                table: "TileTasks",
                column: "DueOn");

            migrationBuilder.CreateIndex(
                name: "IX_TileTasks_RecipeId_ClayBody",
                schema: "glazion",
                table: "TileTasks",
                columns: new[] { "RecipeId", "ClayBody" },
                unique: true,
                filter: "\"CancelledAt\" IS NULL");

            migrationBuilder.AddForeignKey(
                name: "FK_Uploads_TileTasks_TileTaskId",
                schema: "glazion",
                table: "Uploads",
                column: "TileTaskId",
                principalSchema: "glazion",
                principalTable: "TileTasks",
                principalColumn: "Id",
                onDelete: ReferentialAction.SetNull);
        }

        /// <inheritdoc />
        protected override void Down(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.DropForeignKey(
                name: "FK_Uploads_TileTasks_TileTaskId",
                schema: "glazion",
                table: "Uploads");

            migrationBuilder.DropTable(
                name: "TileTaskEvents",
                schema: "glazion");

            migrationBuilder.DropTable(
                name: "TileTasks",
                schema: "glazion");

            migrationBuilder.DropIndex(
                name: "IX_Uploads_TileTaskId_CreatedAt",
                schema: "glazion",
                table: "Uploads");

            migrationBuilder.DropColumn(
                name: "TileTaskId",
                schema: "glazion",
                table: "Uploads");
        }
    }
}
