using System;
using Microsoft.EntityFrameworkCore.Migrations;

#nullable disable

namespace Glazion.Api.Data.Migrations
{
    /// <inheritdoc />
    public partial class AdminDeleteAndMixChanges : Migration
    {
        /// <inheritdoc />
        protected override void Up(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.AddColumn<DateTimeOffset>(
                name: "DeletedAt",
                schema: "glazion",
                table: "Recipes",
                type: "timestamp with time zone",
                nullable: true);

            migrationBuilder.AddColumn<Guid>(
                name: "DeletedBy",
                schema: "glazion",
                table: "Recipes",
                type: "uuid",
                nullable: true);

            migrationBuilder.AddColumn<DateTimeOffset>(
                name: "MixChangedAt",
                schema: "glazion",
                table: "Recipes",
                type: "timestamp with time zone",
                nullable: true);

            migrationBuilder.AddColumn<DateTimeOffset>(
                name: "DeletedAt",
                schema: "glazion",
                table: "BaseGlazes",
                type: "timestamp with time zone",
                nullable: true);

            migrationBuilder.AddColumn<Guid>(
                name: "DeletedBy",
                schema: "glazion",
                table: "BaseGlazes",
                type: "uuid",
                nullable: true);
        }

        /// <inheritdoc />
        protected override void Down(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.DropColumn(
                name: "DeletedAt",
                schema: "glazion",
                table: "Recipes");

            migrationBuilder.DropColumn(
                name: "DeletedBy",
                schema: "glazion",
                table: "Recipes");

            migrationBuilder.DropColumn(
                name: "MixChangedAt",
                schema: "glazion",
                table: "Recipes");

            migrationBuilder.DropColumn(
                name: "DeletedAt",
                schema: "glazion",
                table: "BaseGlazes");

            migrationBuilder.DropColumn(
                name: "DeletedBy",
                schema: "glazion",
                table: "BaseGlazes");
        }
    }
}
