using Microsoft.EntityFrameworkCore.Migrations;

#nullable disable

namespace Message.API.Migrations
{
    /// <inheritdoc />
    public partial class AddMessageRetentionIndex : Migration
    {
        /// <inheritdoc />
        protected override void Up(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.CreateIndex(
                name: "IX_messages_SentAt",
                table: "messages",
                column: "SentAt");
        }

        /// <inheritdoc />
        protected override void Down(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.DropIndex(
                name: "IX_messages_SentAt",
                table: "messages");
        }
    }
}
