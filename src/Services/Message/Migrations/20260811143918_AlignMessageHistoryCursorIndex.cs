using Microsoft.EntityFrameworkCore.Migrations;

#nullable disable

namespace Message.API.Migrations
{
    /// <inheritdoc />
    public partial class AlignMessageHistoryCursorIndex : Migration
    {
        /// <inheritdoc />
        protected override void Up(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.DropIndex(
                name: "IX_messages_RoomId_SentAt",
                table: "messages");

            migrationBuilder.CreateIndex(
                name: "IX_messages_RoomId_SentAt_Id",
                table: "messages",
                columns: new[] { "RoomId", "SentAt", "Id" });
        }

        /// <inheritdoc />
        protected override void Down(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.DropIndex(
                name: "IX_messages_RoomId_SentAt_Id",
                table: "messages");

            migrationBuilder.CreateIndex(
                name: "IX_messages_RoomId_SentAt",
                table: "messages",
                columns: new[] { "RoomId", "SentAt" });
        }
    }
}
