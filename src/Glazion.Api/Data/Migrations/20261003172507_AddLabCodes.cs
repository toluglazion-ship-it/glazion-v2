using System;
using Microsoft.EntityFrameworkCore.Migrations;

#nullable disable

namespace Glazion.Api.Data.Migrations
{
    /// <inheritdoc />
    public partial class AddLabCodes : Migration
    {
        /// <inheritdoc />
        protected override void Up(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.AddColumn<string>(
                name: "ParticipantCode",
                schema: "glazion",
                table: "Users",
                type: "character varying(8)",
                maxLength: 8,
                nullable: true);

            migrationBuilder.AddColumn<Guid>(
                name: "RecipeId",
                schema: "glazion",
                table: "Uploads",
                type: "uuid",
                nullable: true);

            migrationBuilder.AddColumn<string>(
                name: "AdditionsJson",
                schema: "glazion",
                table: "Recipes",
                type: "jsonb",
                nullable: false,
                defaultValue: "[]");

            migrationBuilder.AddColumn<Guid>(
                name: "BaseGlazeId",
                schema: "glazion",
                table: "Recipes",
                type: "uuid",
                nullable: true);

            migrationBuilder.AddColumn<string>(
                name: "Code",
                schema: "glazion",
                table: "Recipes",
                type: "character varying(32)",
                maxLength: 32,
                nullable: true);

            migrationBuilder.AddColumn<string>(
                name: "OutcomeNote",
                schema: "glazion",
                table: "Recipes",
                type: "character varying(500)",
                maxLength: 500,
                nullable: true);

            migrationBuilder.AddColumn<Guid>(
                name: "OwnerUserId",
                schema: "glazion",
                table: "Recipes",
                type: "uuid",
                nullable: true);

            migrationBuilder.AddColumn<int>(
                name: "RecipeNumber",
                schema: "glazion",
                table: "Recipes",
                type: "integer",
                nullable: true);

            migrationBuilder.CreateTable(
                name: "BaseGlazes",
                schema: "glazion",
                columns: table => new
                {
                    Id = table.Column<Guid>(type: "uuid", nullable: false),
                    OwnerUserId = table.Column<Guid>(type: "uuid", nullable: false),
                    ParticipantCode = table.Column<string>(type: "character varying(8)", maxLength: 8, nullable: false),
                    BaseNumber = table.Column<int>(type: "integer", nullable: false),
                    Code = table.Column<string>(type: "character varying(32)", maxLength: 32, nullable: false),
                    Name = table.Column<string>(type: "character varying(180)", maxLength: 180, nullable: false),
                    Description = table.Column<string>(type: "text", nullable: false),
                    FormulationJson = table.Column<string>(type: "jsonb", nullable: false),
                    DerivedFromBaseId = table.Column<Guid>(type: "uuid", nullable: true),
                    ChangeNote = table.Column<string>(type: "character varying(500)", maxLength: 500, nullable: true),
                    CreatedBy = table.Column<Guid>(type: "uuid", nullable: false),
                    CreatedAt = table.Column<DateTimeOffset>(type: "timestamp with time zone", nullable: false)
                },
                constraints: table =>
                {
                    table.PrimaryKey("PK_BaseGlazes", x => x.Id);
                    table.ForeignKey(
                        name: "FK_BaseGlazes_BaseGlazes_DerivedFromBaseId",
                        column: x => x.DerivedFromBaseId,
                        principalSchema: "glazion",
                        principalTable: "BaseGlazes",
                        principalColumn: "Id",
                        onDelete: ReferentialAction.Restrict);
                    table.ForeignKey(
                        name: "FK_BaseGlazes_Users_OwnerUserId",
                        column: x => x.OwnerUserId,
                        principalSchema: "glazion",
                        principalTable: "Users",
                        principalColumn: "Id",
                        onDelete: ReferentialAction.Restrict);
                });

            migrationBuilder.CreateIndex(
                name: "IX_Users_ParticipantCode",
                schema: "glazion",
                table: "Users",
                column: "ParticipantCode",
                unique: true);

            migrationBuilder.CreateIndex(
                name: "IX_Uploads_RecipeId",
                schema: "glazion",
                table: "Uploads",
                column: "RecipeId");

            migrationBuilder.CreateIndex(
                name: "IX_Recipes_BaseGlazeId_RecipeNumber",
                schema: "glazion",
                table: "Recipes",
                columns: new[] { "BaseGlazeId", "RecipeNumber" },
                unique: true);

            migrationBuilder.CreateIndex(
                name: "IX_Recipes_Code",
                schema: "glazion",
                table: "Recipes",
                column: "Code",
                unique: true);

            migrationBuilder.CreateIndex(
                name: "IX_Recipes_OwnerUserId",
                schema: "glazion",
                table: "Recipes",
                column: "OwnerUserId");

            migrationBuilder.AddCheckConstraint(
                name: "CK_Recipes_CodedTriple",
                schema: "glazion",
                table: "Recipes",
                sql: "(\"BaseGlazeId\" IS NULL AND \"RecipeNumber\" IS NULL AND \"Code\" IS NULL) OR (\"BaseGlazeId\" IS NOT NULL AND \"RecipeNumber\" BETWEEN 1 AND 999 AND \"Code\" IS NOT NULL)");

            migrationBuilder.CreateIndex(
                name: "IX_BaseGlazes_Code",
                schema: "glazion",
                table: "BaseGlazes",
                column: "Code",
                unique: true);

            migrationBuilder.CreateIndex(
                name: "IX_BaseGlazes_DerivedFromBaseId",
                schema: "glazion",
                table: "BaseGlazes",
                column: "DerivedFromBaseId");

            migrationBuilder.CreateIndex(
                name: "IX_BaseGlazes_OwnerUserId_BaseNumber",
                schema: "glazion",
                table: "BaseGlazes",
                columns: new[] { "OwnerUserId", "BaseNumber" },
                unique: true);

            migrationBuilder.AddForeignKey(
                name: "FK_Recipes_BaseGlazes_BaseGlazeId",
                schema: "glazion",
                table: "Recipes",
                column: "BaseGlazeId",
                principalSchema: "glazion",
                principalTable: "BaseGlazes",
                principalColumn: "Id",
                onDelete: ReferentialAction.Restrict);

            migrationBuilder.AddForeignKey(
                name: "FK_Uploads_Recipes_RecipeId",
                schema: "glazion",
                table: "Uploads",
                column: "RecipeId",
                principalSchema: "glazion",
                principalTable: "Recipes",
                principalColumn: "Id",
                onDelete: ReferentialAction.Restrict);
        }

        /// <inheritdoc />
        protected override void Down(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.DropForeignKey(
                name: "FK_Recipes_BaseGlazes_BaseGlazeId",
                schema: "glazion",
                table: "Recipes");

            migrationBuilder.DropForeignKey(
                name: "FK_Uploads_Recipes_RecipeId",
                schema: "glazion",
                table: "Uploads");

            migrationBuilder.DropTable(
                name: "BaseGlazes",
                schema: "glazion");

            migrationBuilder.DropIndex(
                name: "IX_Users_ParticipantCode",
                schema: "glazion",
                table: "Users");

            migrationBuilder.DropIndex(
                name: "IX_Uploads_RecipeId",
                schema: "glazion",
                table: "Uploads");

            migrationBuilder.DropIndex(
                name: "IX_Recipes_BaseGlazeId_RecipeNumber",
                schema: "glazion",
                table: "Recipes");

            migrationBuilder.DropIndex(
                name: "IX_Recipes_Code",
                schema: "glazion",
                table: "Recipes");

            migrationBuilder.DropIndex(
                name: "IX_Recipes_OwnerUserId",
                schema: "glazion",
                table: "Recipes");

            migrationBuilder.DropCheckConstraint(
                name: "CK_Recipes_CodedTriple",
                schema: "glazion",
                table: "Recipes");

            migrationBuilder.DropColumn(
                name: "ParticipantCode",
                schema: "glazion",
                table: "Users");

            migrationBuilder.DropColumn(
                name: "RecipeId",
                schema: "glazion",
                table: "Uploads");

            migrationBuilder.DropColumn(
                name: "AdditionsJson",
                schema: "glazion",
                table: "Recipes");

            migrationBuilder.DropColumn(
                name: "BaseGlazeId",
                schema: "glazion",
                table: "Recipes");

            migrationBuilder.DropColumn(
                name: "Code",
                schema: "glazion",
                table: "Recipes");

            migrationBuilder.DropColumn(
                name: "OutcomeNote",
                schema: "glazion",
                table: "Recipes");

            migrationBuilder.DropColumn(
                name: "OwnerUserId",
                schema: "glazion",
                table: "Recipes");

            migrationBuilder.DropColumn(
                name: "RecipeNumber",
                schema: "glazion",
                table: "Recipes");
        }
    }
}
