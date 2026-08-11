using Microsoft.EntityFrameworkCore.Infrastructure;
using Microsoft.EntityFrameworkCore.Migrations;

#nullable disable

[DbContext(typeof(RoomDbContext))]
[Migration("202608100001_InitialCreate")]
public sealed class InitialCreate : Migration
{
    protected override void Up(MigrationBuilder migrationBuilder)
    {
        migrationBuilder.CreateTable("rooms", table => new
        {
            Id = table.Column<Guid>(type: "uuid", nullable: false),
            Name = table.Column<string>(type: "character varying(100)", maxLength: 100, nullable: false),
            Description = table.Column<string>(type: "character varying(1000)", maxLength: 1000, nullable: true),
            OwnerId = table.Column<Guid>(type: "uuid", nullable: false),
            CreatedAt = table.Column<DateTimeOffset>(type: "timestamp with time zone", nullable: false),
            ArchivedAt = table.Column<DateTimeOffset>(type: "timestamp with time zone", nullable: true)
        }, constraints: table => table.PrimaryKey("PK_rooms", x => x.Id));

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

        migrationBuilder.CreateTable("room_audit", table => new
        {
            Id = table.Column<Guid>(type: "uuid", nullable: false),
            RoomId = table.Column<Guid>(type: "uuid", nullable: false),
            ActorId = table.Column<Guid>(type: "uuid", nullable: false),
            TargetUserId = table.Column<Guid>(type: "uuid", nullable: true),
            Action = table.Column<string>(type: "character varying(100)", maxLength: 100, nullable: false),
            Detail = table.Column<string>(type: "character varying(500)", maxLength: 500, nullable: true),
            OccurredAt = table.Column<DateTimeOffset>(type: "timestamp with time zone", nullable: false)
        }, constraints: table => table.PrimaryKey("PK_room_audit", x => x.Id));

        migrationBuilder.CreateTable("room_members", table => new
        {
            Id = table.Column<Guid>(type: "uuid", nullable: false),
            RoomId = table.Column<Guid>(type: "uuid", nullable: false),
            UserId = table.Column<Guid>(type: "uuid", nullable: false),
            Role = table.Column<string>(type: "text", nullable: false),
            AddedById = table.Column<Guid>(type: "uuid", nullable: false),
            JoinedAt = table.Column<DateTimeOffset>(type: "timestamp with time zone", nullable: false)
        }, constraints: table =>
        {
            table.PrimaryKey("PK_room_members", x => x.Id);
            table.ForeignKey("FK_room_members_rooms_RoomId", x => x.RoomId, "rooms", "Id", onDelete: ReferentialAction.Cascade);
        });

        migrationBuilder.CreateIndex("IX_room_members_RoomId_UserId", "room_members", new[] { "RoomId", "UserId" }, unique: true);
        migrationBuilder.CreateIndex("IX_outbox_messages_PublishedAt_NextAttemptAt_OccurredAt", "outbox_messages", new[] { "PublishedAt", "NextAttemptAt", "OccurredAt" });
        migrationBuilder.CreateIndex("IX_room_audit_RoomId_OccurredAt", "room_audit", new[] { "RoomId", "OccurredAt" });
    }

    protected override void Down(MigrationBuilder migrationBuilder)
    {
        migrationBuilder.DropTable("room_members");
        migrationBuilder.DropTable("outbox_messages");
        migrationBuilder.DropTable("room_audit");
        migrationBuilder.DropTable("rooms");
    }
}
