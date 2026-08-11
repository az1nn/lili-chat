using Microsoft.EntityFrameworkCore.Infrastructure;
using Microsoft.EntityFrameworkCore.Migrations;

#nullable disable

[DbContext(typeof(NotificationDbContext))]
[Migration("202608100001_InitialCreate")]
public sealed class InitialCreate : Migration
{
    protected override void Up(MigrationBuilder migrationBuilder)
    {
        migrationBuilder.CreateTable("notification_audit", table => new
        {
            Id = table.Column<Guid>(type: "uuid", nullable: false),
            MessageId = table.Column<Guid>(type: "uuid", nullable: false),
            RoomId = table.Column<Guid>(type: "uuid", nullable: false),
            SenderId = table.Column<Guid>(type: "uuid", nullable: false),
            CreatedAt = table.Column<DateTimeOffset>(type: "timestamp with time zone", nullable: false),
            Status = table.Column<string>(type: "text", nullable: false)
        }, constraints: table => table.PrimaryKey("PK_notification_audit", x => x.Id));

        migrationBuilder.CreateIndex("IX_notification_audit_MessageId", "notification_audit", "MessageId", unique: true);
    }

    protected override void Down(MigrationBuilder migrationBuilder) => migrationBuilder.DropTable("notification_audit");
}
