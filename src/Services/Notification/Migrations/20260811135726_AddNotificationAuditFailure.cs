using Microsoft.EntityFrameworkCore.Migrations;

#nullable disable

namespace Notification.API.Migrations
{
    /// <inheritdoc />
    public partial class AddNotificationAuditFailure : Migration
    {
        /// <inheritdoc />
        protected override void Up(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.AddColumn<string>(
                name: "LastError",
                table: "notification_audit",
                type: "character varying(500)",
                maxLength: 500,
                nullable: true);
        }

        /// <inheritdoc />
        protected override void Down(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.DropColumn(
                name: "LastError",
                table: "notification_audit");
        }
    }
}
