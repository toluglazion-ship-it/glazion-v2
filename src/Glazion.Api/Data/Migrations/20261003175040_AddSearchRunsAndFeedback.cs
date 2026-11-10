using System;
using Microsoft.EntityFrameworkCore.Migrations;

#nullable disable

namespace Glazion.Api.Data.Migrations
{
    /// <inheritdoc />
    public partial class AddSearchRunsAndFeedback : Migration
    {
        /// <inheritdoc />
        protected override void Up(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.CreateTable(
                name: "SearchRuns",
                schema: "glazion",
                columns: table => new
                {
                    Id = table.Column<Guid>(type: "uuid", nullable: false),
                    RequesterUserId = table.Column<Guid>(type: "uuid", nullable: false),
                    QueryType = table.Column<string>(type: "character varying(32)", maxLength: 32, nullable: false),
                    InputHash = table.Column<string>(type: "character varying(64)", maxLength: 64, nullable: false),
                    IdempotencyKey = table.Column<string>(type: "character varying(128)", maxLength: 128, nullable: true),
                    QuerySnapshotJson = table.Column<string>(type: "jsonb", nullable: false),
                    FiltersJson = table.Column<string>(type: "jsonb", nullable: false),
                    CandidateCount = table.Column<int>(type: "integer", nullable: false),
                    ResultCount = table.Column<int>(type: "integer", nullable: false),
                    RankerVersion = table.Column<string>(type: "character varying(64)", maxLength: 64, nullable: false),
                    LatencyMs = table.Column<int>(type: "integer", nullable: true),
                    ResultsJson = table.Column<string>(type: "jsonb", nullable: false),
                    WarningsJson = table.Column<string>(type: "jsonb", nullable: false),
                    CreatedAt = table.Column<DateTimeOffset>(type: "timestamp with time zone", nullable: false)
                },
                constraints: table =>
                {
                    table.PrimaryKey("PK_SearchRuns", x => x.Id);
                    table.CheckConstraint("CK_SearchRuns_QueryType", "\"QueryType\" IN ('umf', 'recipe')");
                    table.ForeignKey(
                        name: "FK_SearchRuns_Users_RequesterUserId",
                        column: x => x.RequesterUserId,
                        principalSchema: "glazion",
                        principalTable: "Users",
                        principalColumn: "Id",
                        onDelete: ReferentialAction.Restrict);
                });

            migrationBuilder.CreateTable(
                name: "SearchFeedback",
                schema: "glazion",
                columns: table => new
                {
                    Id = table.Column<Guid>(type: "uuid", nullable: false),
                    SearchRunId = table.Column<Guid>(type: "uuid", nullable: false),
                    ImageId = table.Column<Guid>(type: "uuid", nullable: false),
                    RequesterUserId = table.Column<Guid>(type: "uuid", nullable: false),
                    Feedback = table.Column<string>(type: "character varying(32)", maxLength: 32, nullable: false),
                    Note = table.Column<string>(type: "character varying(1000)", maxLength: 1000, nullable: true),
                    CreatedAt = table.Column<DateTimeOffset>(type: "timestamp with time zone", nullable: false),
                    UpdatedAt = table.Column<DateTimeOffset>(type: "timestamp with time zone", nullable: false)
                },
                constraints: table =>
                {
                    table.PrimaryKey("PK_SearchFeedback", x => x.Id);
                    table.CheckConstraint("CK_SearchFeedback_Value", "\"Feedback\" IN ('relevant', 'not_relevant')");
                    table.ForeignKey(
                        name: "FK_SearchFeedback_ImageAssets_ImageId",
                        column: x => x.ImageId,
                        principalSchema: "glazion",
                        principalTable: "ImageAssets",
                        principalColumn: "Id",
                        onDelete: ReferentialAction.Restrict);
                    table.ForeignKey(
                        name: "FK_SearchFeedback_SearchRuns_SearchRunId",
                        column: x => x.SearchRunId,
                        principalSchema: "glazion",
                        principalTable: "SearchRuns",
                        principalColumn: "Id",
                        onDelete: ReferentialAction.Cascade);
                    table.ForeignKey(
                        name: "FK_SearchFeedback_Users_RequesterUserId",
                        column: x => x.RequesterUserId,
                        principalSchema: "glazion",
                        principalTable: "Users",
                        principalColumn: "Id",
                        onDelete: ReferentialAction.Restrict);
                });

            migrationBuilder.CreateIndex(
                name: "IX_SearchFeedback_ImageId_Feedback",
                schema: "glazion",
                table: "SearchFeedback",
                columns: new[] { "ImageId", "Feedback" });

            migrationBuilder.CreateIndex(
                name: "IX_SearchFeedback_RequesterUserId",
                schema: "glazion",
                table: "SearchFeedback",
                column: "RequesterUserId");

            migrationBuilder.CreateIndex(
                name: "IX_SearchFeedback_SearchRunId_ImageId_RequesterUserId",
                schema: "glazion",
                table: "SearchFeedback",
                columns: new[] { "SearchRunId", "ImageId", "RequesterUserId" },
                unique: true);

            migrationBuilder.CreateIndex(
                name: "IX_SearchRuns_InputHash",
                schema: "glazion",
                table: "SearchRuns",
                column: "InputHash");

            migrationBuilder.CreateIndex(
                name: "IX_SearchRuns_RequesterUserId_CreatedAt",
                schema: "glazion",
                table: "SearchRuns",
                columns: new[] { "RequesterUserId", "CreatedAt" });

            migrationBuilder.CreateIndex(
                name: "IX_SearchRuns_RequesterUserId_IdempotencyKey",
                schema: "glazion",
                table: "SearchRuns",
                columns: new[] { "RequesterUserId", "IdempotencyKey" },
                unique: true,
                filter: "\"IdempotencyKey\" IS NOT NULL");
        }

        /// <inheritdoc />
        protected override void Down(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.DropTable(
                name: "SearchFeedback",
                schema: "glazion");

            migrationBuilder.DropTable(
                name: "SearchRuns",
                schema: "glazion");
        }
    }
}
