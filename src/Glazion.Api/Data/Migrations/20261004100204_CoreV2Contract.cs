using System;
using Microsoft.EntityFrameworkCore.Migrations;

#nullable disable

namespace Glazion.Api.Data.Migrations
{
    /// <inheritdoc />
    public partial class CoreV2Contract : Migration
    {
        /// <inheritdoc />
        protected override void Up(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.DropIndex(
                name: "IX_CaptureCodes_AssignedUserId",
                schema: "glazion",
                table: "CaptureCodes");

            migrationBuilder.RenameColumn(
                name: "Code",
                schema: "glazion",
                table: "CaptureCodes",
                newName: "CodeHash");

            migrationBuilder.RenameIndex(
                name: "IX_CaptureCodes_Code",
                schema: "glazion",
                table: "CaptureCodes",
                newName: "IX_CaptureCodes_CodeHash");

            migrationBuilder.AddColumn<string>(
                name: "Email",
                schema: "glazion",
                table: "Users",
                type: "character varying(320)",
                maxLength: 320,
                nullable: true);

            migrationBuilder.AddColumn<string>(
                name: "Status",
                schema: "glazion",
                table: "Users",
                type: "character varying(16)",
                maxLength: 16,
                nullable: false,
                defaultValue: "Active");

            // IsActive → AccountStatus: an account an administrator switched off becomes Disabled.
            migrationBuilder.Sql("""UPDATE glazion."Users" SET "Status" = CASE WHEN "IsActive" THEN 'Active' ELSE 'Disabled' END;""");

            migrationBuilder.DropColumn(
                name: "IsActive",
                schema: "glazion",
                table: "Users");

            migrationBuilder.AddColumn<string>(
                name: "ReviewNote",
                schema: "glazion",
                table: "Uploads",
                type: "character varying(2000)",
                maxLength: 2000,
                nullable: true);

            migrationBuilder.AddColumn<DateTimeOffset>(
                name: "ReviewedAt",
                schema: "glazion",
                table: "Uploads",
                type: "timestamp with time zone",
                nullable: true);

            migrationBuilder.AddColumn<Guid>(
                name: "ReviewedBy",
                schema: "glazion",
                table: "Uploads",
                type: "uuid",
                nullable: true);

            // A rejection reason used to live in FailureReason; it is the reviewer's note now.
            migrationBuilder.Sql("""UPDATE glazion."Uploads" SET "ReviewNote" = "FailureReason", "FailureReason" = NULL WHERE "Status" = 'Rejected';""");
            // Approved uploads whose image already has a live vector are searchable: Indexed.
            migrationBuilder.Sql("""
                UPDATE glazion."Uploads" u SET "Status" = 'Indexed'
                WHERE u."Status" = 'Approved' AND EXISTS (
                    SELECT 1 FROM glazion."ImageAssets" a JOIN glazion."IndexRecords" r ON r."EntityType" = 'image' AND r."EntityId" = a."Id"
                    WHERE a."UploadId" = u."Id" AND r."IndexedAt" IS NOT NULL AND r."SupersededAt" IS NULL);
                """);

            migrationBuilder.AlterColumn<string>(
                name: "TokenHash",
                schema: "glazion",
                table: "Sessions",
                type: "character varying(64)",
                maxLength: 64,
                nullable: false,
                oldClrType: typeof(string),
                oldType: "text");

            migrationBuilder.AddColumn<DateTimeOffset>(
                name: "RefreshExpiresAt",
                schema: "glazion",
                table: "Sessions",
                type: "timestamp with time zone",
                nullable: true);

            migrationBuilder.AddColumn<string>(
                name: "RefreshTokenHash",
                schema: "glazion",
                table: "Sessions",
                type: "character varying(64)",
                maxLength: 64,
                nullable: true);

            migrationBuilder.AddColumn<Guid>(
                name: "ReplacedBySessionId",
                schema: "glazion",
                table: "Sessions",
                type: "uuid",
                nullable: true);

            migrationBuilder.AddColumn<Guid>(
                name: "CreatedBy",
                schema: "glazion",
                table: "CaptureCodes",
                type: "uuid",
                nullable: true);

            migrationBuilder.AddColumn<string>(
                name: "LabelPrefix",
                schema: "glazion",
                table: "CaptureCodes",
                type: "character varying(32)",
                maxLength: 32,
                nullable: true);

            migrationBuilder.AddColumn<DateTimeOffset>(
                name: "RevokedAt",
                schema: "glazion",
                table: "CaptureCodes",
                type: "timestamp with time zone",
                nullable: true);

            migrationBuilder.AddColumn<string>(
                name: "MaskedSuffix",
                schema: "glazion",
                table: "CaptureCodes",
                type: "character varying(8)",
                maxLength: 8,
                nullable: false,
                defaultValue: "");

            // Capture codes were stored in plain text. Keep the last four characters for display and replace the code
            // with the SHA-256 hash the API now stores (same normalisation as CaptureCodeGenerator.Hash).
            migrationBuilder.Sql("""
                UPDATE glazion."CaptureCodes"
                SET "MaskedSuffix" = right(upper(trim("CodeHash")), 4),
                    "CodeHash" = encode(sha256(convert_to(upper(trim("CodeHash")), 'UTF8')), 'hex'),
                    "RevokedAt" = CASE WHEN "IsActive" THEN NULL ELSE now() END;
                """);

            migrationBuilder.AddColumn<Guid>(
                name: "RevokedBy",
                schema: "glazion",
                table: "CaptureCodes",
                type: "uuid",
                nullable: true);

            migrationBuilder.CreateIndex(
                name: "IX_Users_Email",
                schema: "glazion",
                table: "Users",
                column: "Email",
                unique: true,
                filter: "\"Email\" IS NOT NULL");

            migrationBuilder.CreateIndex(
                name: "IX_Users_Status_CreatedAt",
                schema: "glazion",
                table: "Users",
                columns: new[] { "Status", "CreatedAt" });

            migrationBuilder.CreateIndex(
                name: "IX_Uploads_Status_CreatedAt",
                schema: "glazion",
                table: "Uploads",
                columns: new[] { "Status", "CreatedAt" });

            migrationBuilder.CreateIndex(
                name: "IX_Sessions_RefreshTokenHash",
                schema: "glazion",
                table: "Sessions",
                column: "RefreshTokenHash",
                unique: true,
                filter: "\"RefreshTokenHash\" IS NOT NULL");

            migrationBuilder.CreateIndex(
                name: "IX_CaptureCodes_AssignedUserId_IsActive_CreatedAt",
                schema: "glazion",
                table: "CaptureCodes",
                columns: new[] { "AssignedUserId", "IsActive", "CreatedAt" });
        }

        /// <inheritdoc />
        protected override void Down(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.DropIndex(
                name: "IX_Users_Email",
                schema: "glazion",
                table: "Users");

            migrationBuilder.DropIndex(
                name: "IX_Users_Status_CreatedAt",
                schema: "glazion",
                table: "Users");

            migrationBuilder.DropIndex(
                name: "IX_Uploads_Status_CreatedAt",
                schema: "glazion",
                table: "Uploads");

            migrationBuilder.DropIndex(
                name: "IX_Sessions_RefreshTokenHash",
                schema: "glazion",
                table: "Sessions");

            migrationBuilder.DropIndex(
                name: "IX_CaptureCodes_AssignedUserId_IsActive_CreatedAt",
                schema: "glazion",
                table: "CaptureCodes");

            migrationBuilder.DropColumn(
                name: "Email",
                schema: "glazion",
                table: "Users");

            migrationBuilder.DropColumn(
                name: "ReviewNote",
                schema: "glazion",
                table: "Uploads");

            migrationBuilder.DropColumn(
                name: "ReviewedAt",
                schema: "glazion",
                table: "Uploads");

            migrationBuilder.DropColumn(
                name: "ReviewedBy",
                schema: "glazion",
                table: "Uploads");

            migrationBuilder.DropColumn(
                name: "RefreshExpiresAt",
                schema: "glazion",
                table: "Sessions");

            migrationBuilder.DropColumn(
                name: "RefreshTokenHash",
                schema: "glazion",
                table: "Sessions");

            migrationBuilder.DropColumn(
                name: "ReplacedBySessionId",
                schema: "glazion",
                table: "Sessions");

            migrationBuilder.DropColumn(
                name: "CreatedBy",
                schema: "glazion",
                table: "CaptureCodes");

            migrationBuilder.DropColumn(
                name: "LabelPrefix",
                schema: "glazion",
                table: "CaptureCodes");

            migrationBuilder.DropColumn(
                name: "MaskedSuffix",
                schema: "glazion",
                table: "CaptureCodes");

            migrationBuilder.DropColumn(
                name: "RevokedAt",
                schema: "glazion",
                table: "CaptureCodes");

            migrationBuilder.DropColumn(
                name: "RevokedBy",
                schema: "glazion",
                table: "CaptureCodes");

            migrationBuilder.RenameColumn(
                name: "CodeHash",
                schema: "glazion",
                table: "CaptureCodes",
                newName: "Code");

            migrationBuilder.RenameIndex(
                name: "IX_CaptureCodes_CodeHash",
                schema: "glazion",
                table: "CaptureCodes",
                newName: "IX_CaptureCodes_Code");

            migrationBuilder.AddColumn<bool>(
                name: "IsActive",
                schema: "glazion",
                table: "Users",
                type: "boolean",
                nullable: false,
                defaultValue: true);

            // Capture codes stay hashed: the plain codes cannot be recovered. Reissue codes after a rollback.
            migrationBuilder.Sql("""UPDATE glazion."Users" SET "IsActive" = ("Status" = 'Active');""");

            migrationBuilder.DropColumn(
                name: "Status",
                schema: "glazion",
                table: "Users");

            migrationBuilder.AlterColumn<string>(
                name: "TokenHash",
                schema: "glazion",
                table: "Sessions",
                type: "text",
                nullable: false,
                oldClrType: typeof(string),
                oldType: "character varying(64)",
                oldMaxLength: 64);

            migrationBuilder.CreateIndex(
                name: "IX_CaptureCodes_AssignedUserId",
                schema: "glazion",
                table: "CaptureCodes",
                column: "AssignedUserId");
        }
    }
}
