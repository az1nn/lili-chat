using Microsoft.EntityFrameworkCore.Infrastructure;
using Microsoft.EntityFrameworkCore.Migrations;

#nullable disable

[DbContext(typeof(IdentityDbContext))]
[Migration("202608100001_InitialCreate")]
public sealed class InitialCreate : Migration
{
    protected override void Up(MigrationBuilder migrationBuilder)
    {
        migrationBuilder.CreateTable(
            name: "users",
            columns: table => new
            {
                Id = table.Column<Guid>(type: "uuid", nullable: false),
                Username = table.Column<string>(type: "character varying(100)", maxLength: 100, nullable: false),
                Email = table.Column<string>(type: "character varying(255)", maxLength: 255, nullable: false),
                PasswordHash = table.Column<string>(type: "character varying(512)", maxLength: 512, nullable: false),
                AccessFailedCount = table.Column<int>(type: "integer", nullable: false),
                LockoutEnd = table.Column<DateTimeOffset>(type: "timestamp with time zone", nullable: true),
                CreatedAt = table.Column<DateTimeOffset>(type: "timestamp with time zone", nullable: false),
                LastLoginAt = table.Column<DateTimeOffset>(type: "timestamp with time zone", nullable: true)
            },
            constraints: table => table.PrimaryKey("PK_users", x => x.Id));

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

        migrationBuilder.CreateTable(
            name: "refresh_tokens",
            columns: table => new
            {
                Id = table.Column<Guid>(type: "uuid", nullable: false),
                UserId = table.Column<Guid>(type: "uuid", nullable: false),
                FamilyId = table.Column<Guid>(type: "uuid", nullable: false),
                TokenHash = table.Column<string>(type: "text", nullable: false),
                ExpiresAt = table.Column<DateTimeOffset>(type: "timestamp with time zone", nullable: false),
                RevokedAt = table.Column<DateTimeOffset>(type: "timestamp with time zone", nullable: true)
            },
            constraints: table =>
            {
                table.PrimaryKey("PK_refresh_tokens", x => x.Id);
                table.ForeignKey("FK_refresh_tokens_users_UserId", x => x.UserId, "users", "Id", onDelete: ReferentialAction.Cascade);
            });

        migrationBuilder.CreateIndex("IX_users_Email", "users", "Email", unique: true);
        migrationBuilder.CreateIndex("IX_users_Username", "users", "Username", unique: true);
        migrationBuilder.CreateIndex("IX_refresh_tokens_TokenHash", "refresh_tokens", "TokenHash", unique: true);
        migrationBuilder.CreateIndex("IX_refresh_tokens_UserId", "refresh_tokens", "UserId");
        migrationBuilder.CreateIndex("IX_refresh_tokens_UserId_FamilyId", "refresh_tokens", new[] { "UserId", "FamilyId" });
        migrationBuilder.CreateIndex("IX_outbox_messages_PublishedAt_NextAttemptAt_OccurredAt", "outbox_messages", new[] { "PublishedAt", "NextAttemptAt", "OccurredAt" });
    }

    protected override void Down(MigrationBuilder migrationBuilder)
    {
        migrationBuilder.DropTable("refresh_tokens");
        migrationBuilder.DropTable("outbox_messages");
        migrationBuilder.DropTable("users");
    }
}
