using Microsoft.EntityFrameworkCore.Infrastructure;
using Microsoft.EntityFrameworkCore.Migrations;

#nullable disable

[DbContext(typeof(FamilyDbContext))]
[Migration("202608100001_InitialCreate")]
public sealed class InitialCreate : Migration
{
    protected override void Up(MigrationBuilder migrationBuilder)
    {
        migrationBuilder.CreateTable("families", table => new
        {
            Id = table.Column<Guid>(type: "uuid", nullable: false),
            Name = table.Column<string>(type: "character varying(100)", maxLength: 100, nullable: false),
            Description = table.Column<string>(type: "character varying(1000)", maxLength: 1000, nullable: true),
            CreatedAt = table.Column<DateTimeOffset>(type: "timestamp with time zone", nullable: false)
        }, constraints: table => table.PrimaryKey("PK_families", x => x.Id));

        migrationBuilder.CreateTable("users", table => new
        {
            Id = table.Column<Guid>(type: "uuid", nullable: false),
            PublicId = table.Column<string>(type: "character varying(8)", maxLength: 8, nullable: false),
            Username = table.Column<string>(type: "character varying(100)", maxLength: 100, nullable: false),
            Email = table.Column<string>(type: "character varying(255)", maxLength: 255, nullable: false),
            CreatedAt = table.Column<DateTimeOffset>(type: "timestamp with time zone", nullable: false)
        }, constraints: table => table.PrimaryKey("PK_users", x => x.Id));

        migrationBuilder.CreateTable("family_members", table => new
        {
            Id = table.Column<Guid>(type: "uuid", nullable: false),
            FamilyId = table.Column<Guid>(type: "uuid", nullable: false),
            UserId = table.Column<Guid>(type: "uuid", nullable: false),
            Role = table.Column<string>(type: "text", nullable: false),
            AddedById = table.Column<Guid>(type: "uuid", nullable: false),
            JoinedAt = table.Column<DateTimeOffset>(type: "timestamp with time zone", nullable: false)
        }, constraints: table =>
        {
            table.PrimaryKey("PK_family_members", x => x.Id);
            table.ForeignKey("FK_family_members_families_FamilyId", x => x.FamilyId, "families", "Id", onDelete: ReferentialAction.Cascade);
            table.ForeignKey("FK_family_members_users_UserId", x => x.UserId, "users", "Id", onDelete: ReferentialAction.Cascade);
        });

        migrationBuilder.CreateIndex("IX_users_Email", "users", "Email", unique: true);
        migrationBuilder.CreateIndex("IX_users_PublicId", "users", "PublicId", unique: true);
        migrationBuilder.CreateIndex("IX_family_members_FamilyId_UserId", "family_members", new[] { "FamilyId", "UserId" }, unique: true);
        migrationBuilder.CreateIndex("IX_family_members_UserId", "family_members", "UserId");
    }

    protected override void Down(MigrationBuilder migrationBuilder)
    {
        migrationBuilder.DropTable("family_members");
        migrationBuilder.DropTable("families");
        migrationBuilder.DropTable("users");
    }
}
