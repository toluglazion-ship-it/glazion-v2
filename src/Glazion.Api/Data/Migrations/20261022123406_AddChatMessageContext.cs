using Microsoft.EntityFrameworkCore.Migrations;

#nullable disable

namespace Glazion.Api.Data.Migrations
{
    /// <inheritdoc />
    public partial class AddChatMessageContext : Migration
    {
        /// <inheritdoc />
        protected override void Up(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.AddColumn<string>(
                name: "Context",
                schema: "glazion",
                table: "ChatMessages",
                type: "text",
                nullable: true);
        }

        /// <inheritdoc />
        protected override void Down(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.DropColumn(
                name: "Context",
                schema: "glazion",
                table: "ChatMessages");
        }
    }
}
