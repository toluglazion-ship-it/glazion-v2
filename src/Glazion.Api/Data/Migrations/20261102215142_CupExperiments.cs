using System;
using Microsoft.EntityFrameworkCore.Migrations;

#nullable disable

namespace Glazion.Api.Data.Migrations
{
    /// <inheritdoc />
    public partial class CupExperiments : Migration
    {
        /// <inheritdoc />
        protected override void Up(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.CreateTable(
                name: "Experiments",
                schema: "glazion",
                columns: table => new
                {
                    Id = table.Column<Guid>(type: "uuid", nullable: false),
                    Name = table.Column<string>(type: "character varying(180)", maxLength: 180, nullable: false),
                    BaseGlazeId = table.Column<Guid>(type: "uuid", nullable: false),
                    ParticipantUserId = table.Column<Guid>(type: "uuid", nullable: false),
                    CupCount = table.Column<int>(type: "integer", nullable: false),
                    DryGrams = table.Column<double>(type: "double precision", nullable: false),
                    WaterGrams = table.Column<double>(type: "double precision", nullable: false),
                    TilesPerStep = table.Column<int>(type: "integer", nullable: false),
                    ClayBodiesJson = table.Column<string>(type: "jsonb", nullable: false),
                    MinReserveGrams = table.Column<double>(type: "double precision", nullable: false),
                    StagesJson = table.Column<string>(type: "jsonb", nullable: false),
                    CupsJson = table.Column<string>(type: "jsonb", nullable: false),
                    CreatedBy = table.Column<Guid>(type: "uuid", nullable: false),
                    CreatedAt = table.Column<DateTimeOffset>(type: "timestamp with time zone", nullable: false),
                    UpdatedAt = table.Column<DateTimeOffset>(type: "timestamp with time zone", nullable: false),
                    DeletedAt = table.Column<DateTimeOffset>(type: "timestamp with time zone", nullable: true)
                },
                constraints: table =>
                {
                    table.PrimaryKey("PK_Experiments", x => x.Id);
                    table.CheckConstraint("CK_Experiments_Cups", "\"CupCount\" BETWEEN 1 AND 20");
                    table.ForeignKey(
                        name: "FK_Experiments_BaseGlazes_BaseGlazeId",
                        column: x => x.BaseGlazeId,
                        principalSchema: "glazion",
                        principalTable: "BaseGlazes",
                        principalColumn: "Id",
                        onDelete: ReferentialAction.Restrict);
                    table.ForeignKey(
                        name: "FK_Experiments_Users_ParticipantUserId",
                        column: x => x.ParticipantUserId,
                        principalSchema: "glazion",
                        principalTable: "Users",
                        principalColumn: "Id",
                        onDelete: ReferentialAction.Restrict);
                });

            migrationBuilder.CreateTable(
                name: "ExperimentSteps",
                schema: "glazion",
                columns: table => new
                {
                    Id = table.Column<Guid>(type: "uuid", nullable: false),
                    ExperimentId = table.Column<Guid>(type: "uuid", nullable: false),
                    Ordinal = table.Column<int>(type: "integer", nullable: false),
                    Kind = table.Column<string>(type: "character varying(16)", maxLength: 16, nullable: false),
                    StageIndex = table.Column<int>(type: "integer", nullable: true),
                    Level = table.Column<int>(type: "integer", nullable: true),
                    Material = table.Column<string>(type: "character varying(255)", maxLength: 255, nullable: true),
                    CupsJson = table.Column<string>(type: "jsonb", nullable: false),
                    ActorUserId = table.Column<Guid>(type: "uuid", nullable: false),
                    StartedAt = table.Column<DateTimeOffset>(type: "timestamp with time zone", nullable: false),
                    DippedAt = table.Column<DateTimeOffset>(type: "timestamp with time zone", nullable: true),
                    WeighedCup = table.Column<int>(type: "integer", nullable: true),
                    WeighedGrams = table.Column<double>(type: "double precision", nullable: true),
                    Retention = table.Column<double>(type: "double precision", nullable: true)
                },
                constraints: table =>
                {
                    table.PrimaryKey("PK_ExperimentSteps", x => x.Id);
                    table.ForeignKey(
                        name: "FK_ExperimentSteps_Experiments_ExperimentId",
                        column: x => x.ExperimentId,
                        principalSchema: "glazion",
                        principalTable: "Experiments",
                        principalColumn: "Id",
                        onDelete: ReferentialAction.Cascade);
                });

            migrationBuilder.CreateIndex(
                name: "IX_Experiments_BaseGlazeId",
                schema: "glazion",
                table: "Experiments",
                column: "BaseGlazeId");

            migrationBuilder.CreateIndex(
                name: "IX_Experiments_ParticipantUserId",
                schema: "glazion",
                table: "Experiments",
                column: "ParticipantUserId");

            migrationBuilder.CreateIndex(
                name: "IX_ExperimentSteps_ExperimentId_Ordinal",
                schema: "glazion",
                table: "ExperimentSteps",
                columns: new[] { "ExperimentId", "Ordinal" },
                unique: true);
        }

        /// <inheritdoc />
        protected override void Down(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.DropTable(
                name: "ExperimentSteps",
                schema: "glazion");

            migrationBuilder.DropTable(
                name: "Experiments",
                schema: "glazion");
        }
    }
}
