using Microsoft.EntityFrameworkCore.Infrastructure;
using Microsoft.EntityFrameworkCore.Migrations;

#nullable disable

[DbContext(typeof(MessageDbContext))]
[Migration("202608100001_InitialCreate")]
public sealed class InitialCreate : Migration
{
    protected override void Up(MigrationBuilder migrationBuilder)
    {
        migrationBuilder.CreateTable("outbox_messages", table => new
        {
            Id = table.Column<Guid>(type: "uuid", nullable: false),
            Type = table.Column<string>(type: "character varying(200)", maxLength: 200, nullable: false),
            Payload = table.Column<string>(type: "text", nullable: false),
            OccurredAt = table.Column<DateTimeOffset>(type: "timestamp with time zone", nullable: false),
            PublishedAt = table.Column<DateTimeOffset>(type: "timestamp with time zone", nullable: true),
            Attempts = table.Column<int>(type: "integer", nullable: false),
            NextAttemptAt = table.Column<DateTimeOffset>(type: "timestamp with time zone", nullable: true),
            LastError = table.Column<string>(type: "text", nullable: true)
        }, constraints: table => table.PrimaryKey("PK_outbox_messages", x => x.Id));

        migrationBuilder.CreateTable("messages", table => new
        {
            Id = table.Column<Guid>(type: "uuid", nullable: false),
            RoomId = table.Column<Guid>(type: "uuid", nullable: false),
            SenderId = table.Column<Guid>(type: "uuid", nullable: false),
            Content = table.Column<string>(type: "character varying(2000)", maxLength: 2000, nullable: false),
            SentAt = table.Column<DateTimeOffset>(type: "timestamp with time zone", nullable: false)
        }, constraints: table => table.PrimaryKey("PK_messages", x => x.Id));

        migrationBuilder.CreateIndex("IX_messages_RoomId_SentAt", "messages", new[] { "RoomId", "SentAt" });
        migrationBuilder.CreateIndex("IX_outbox_messages_PublishedAt_NextAttemptAt_OccurredAt", "outbox_messages", new[] { "PublishedAt", "NextAttemptAt", "OccurredAt" });
    }

    protected override void Down(MigrationBuilder migrationBuilder)
    {
        migrationBuilder.DropTable("messages");
        migrationBuilder.DropTable("outbox_messages");
    }
}
