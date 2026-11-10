using System;
using Microsoft.EntityFrameworkCore.Migrations;

#nullable disable

namespace Glazion.Api.Data.Migrations
{
    /// <inheritdoc />
    public partial class CaptureByCode : Migration
    {
        /// <inheritdoc />
        protected override void Up(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.AlterColumn<Guid>(
                name: "CaptureCodeId",
                schema: "glazion",
                table: "Uploads",
                type: "uuid",
                nullable: true,
                oldClrType: typeof(Guid),
                oldType: "uuid");

            migrationBuilder.AddColumn<string>(
                name: "BackContentType",
                schema: "glazion",
                table: "Uploads",
                type: "character varying(128)",
                maxLength: 128,
                nullable: true);

            migrationBuilder.AddColumn<long>(
                name: "BackDeclaredSizeBytes",
                schema: "glazion",
                table: "Uploads",
                type: "bigint",
                nullable: true);

            migrationBuilder.AddColumn<string>(
                name: "BackObjectKey",
                schema: "glazion",
                table: "Uploads",
                type: "character varying(512)",
                maxLength: 512,
                nullable: true);

            migrationBuilder.AddColumn<string>(
                name: "BackContentType",
                schema: "glazion",
                table: "ImageAssets",
                type: "character varying(128)",
                maxLength: 128,
                nullable: true);

            migrationBuilder.AddColumn<string>(
                name: "BackKey",
                schema: "glazion",
                table: "ImageAssets",
                type: "character varying(512)",
                maxLength: 512,
                nullable: true);
        }

        /// <inheritdoc />
        protected override void Down(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.DropColumn(
                name: "BackContentType",
                schema: "glazion",
                table: "Uploads");

            migrationBuilder.DropColumn(
                name: "BackDeclaredSizeBytes",
                schema: "glazion",
                table: "Uploads");

            migrationBuilder.DropColumn(
                name: "BackObjectKey",
                schema: "glazion",
                table: "Uploads");

            migrationBuilder.DropColumn(
                name: "BackContentType",
                schema: "glazion",
                table: "ImageAssets");

            migrationBuilder.DropColumn(
                name: "BackKey",
                schema: "glazion",
                table: "ImageAssets");

            migrationBuilder.AlterColumn<Guid>(
                name: "CaptureCodeId",
                schema: "glazion",
                table: "Uploads",
                type: "uuid",
                nullable: false,
                defaultValue: new Guid("00000000-0000-0000-0000-000000000000"),
                oldClrType: typeof(Guid),
                oldType: "uuid",
                oldNullable: true);
        }
    }
}
