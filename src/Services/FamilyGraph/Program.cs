using System.Security.Claims;
using System.Security.Cryptography;
using System.Text;
using FamilyChat.Contracts.Events;
using FamilyChat.Contracts.Family;
using FamilyChat.ServiceDefaults;
using Grpc.Core;
using MassTransit;
using Microsoft.AspNetCore.Authentication.JwtBearer;
using Microsoft.AspNetCore.Server.Kestrel.Core;
using Microsoft.EntityFrameworkCore;
using Microsoft.IdentityModel.Tokens;
using Npgsql;

var builder = WebApplication.CreateBuilder(args);
builder.WebHost.ConfigureKestrel(o =>
{
    o.ListenAnyIP(8080, lo => lo.Protocols = HttpProtocols.Http1);
    o.ListenAnyIP(8081, lo => lo.Protocols = HttpProtocols.Http2);
});
builder.AddFamilyChatObservability("family-svc");

builder.Services.AddDbContext<FamilyDbContext>(o =>
    o.UseNpgsql(builder.Configuration.GetConnectionString("Default")));
builder.Services.AddGrpc();
var internalToken = InternalServiceAuth.RequiredToken(builder.Configuration, "InternalAuth:Token");

builder.Services.AddAuthentication(JwtBearerDefaults.AuthenticationScheme)
    .AddJwtBearer(o =>
    {
        o.MapInboundClaims = false;
        o.TokenValidationParameters = JwtValidation(builder.Configuration, builder.Environment);
    });
builder.Services.AddAuthorization();
var rabbitMq = RabbitMqCredentials.Load(
    builder.Configuration, builder.Environment.IsDevelopment());

builder.Services.AddMassTransit(x =>
{
    x.AddConsumer<UserRegisteredConsumer>();
    x.UsingRabbitMq((ctx, cfg) =>
    {
        cfg.Host(builder.Configuration["RabbitMQ:Host"] ?? "rabbitmq", "/", h =>
        {
            h.Username(rabbitMq.Username);
            h.Password(rabbitMq.Password);
        });
        cfg.ConfigureEndpoints(ctx);
    });
});

var app = builder.Build();
app.UseAuthentication();
app.UseAuthorization();
app.UseInternalGrpcAuthentication("/family.FamilyGraphGrpc", internalToken);

using (var scope = app.Services.CreateScope())
{
    var db = scope.ServiceProvider.GetRequiredService<FamilyDbContext>();
    await db.Database.MigrateAsync();
}

app.MapHealthChecks("/health");
app.MapGrpcService<FamilyGrpcService>();

app.MapGet("/api/v1/users/me", async (HttpContext http, FamilyDbContext db, CancellationToken ct) =>
{
    if (!TryUserId(http.User, out var userId)) return Results.Unauthorized();
    var u = await db.Users.AsNoTracking().FirstOrDefaultAsync(x => x.Id == userId, ct);
    return u is null
        ? Results.NotFound(new { error = "Projeção do usuário ainda não criada." })
        : Results.Ok(UserDto.From(u));
}).RequireAuthorization();

app.MapGet("/api/v1/users/by-public-id/{publicId}", async (
    string publicId, FamilyDbContext db, CancellationToken ct) =>
{
    if (!FamilyInput.TryPublicId(publicId, out var normalized))
        return Results.BadRequest(new { error = "PublicId inválido." });
    var u = await db.Users.AsNoTracking().FirstOrDefaultAsync(x => x.PublicId == normalized, ct);
    return u is null ? Results.NotFound() : Results.Ok(UserDto.From(u));
}).RequireAuthorization();

app.MapGet("/api/v1/families", async (HttpContext http, FamilyDbContext db, CancellationToken ct) =>
{
    if (!TryUserId(http.User, out var userId)) return Results.Unauthorized();
    var items = await db.FamilyMembers.AsNoTracking()
        .Where(m => m.UserId == userId)
        .Select(m => new FamilyDto(m.Family.Id, m.Family.Name, m.Role, m.Family.Members.Count))
        .ToListAsync(ct);
    return Results.Ok(items);
}).RequireAuthorization();

app.MapPost("/api/v1/families", async (
    HttpContext http, CreateFamilyRequest req, FamilyDbContext db, CancellationToken ct) =>
{
    if (!TryUserId(http.User, out var userId)) return Results.Unauthorized();
    if (!FamilyInput.TryText(req.Name, req.Description, out var name, out var description))
        return Results.BadRequest(new { error = "Nome deve ter 1–100 e descrição até 1000 caracteres." });
    if (!await db.Users.AnyAsync(u => u.Id == userId, ct))
        return Results.Conflict(new { error = "A projeção do usuário ainda não chegou ao Family Graph." });

    var family = new FamilyEntity
    {
        Id = Guid.NewGuid(),
        Name = name,
        Description = description,
        CreatedAt = DateTimeOffset.UtcNow
    };
    family.Members.Add(new FamilyMember
    {
        Id = Guid.NewGuid(),
        UserId = userId,
        Role = "Head",
        AddedById = userId,
        JoinedAt = DateTimeOffset.UtcNow
    });
    db.Families.Add(family);
    await db.SaveChangesAsync(ct);
    return Results.Created($"/api/v1/families/{family.Id}",
        new FamilyDto(family.Id, family.Name, "Head", 1));
}).RequireAuthorization();

app.MapPost("/api/v1/families/{familyId:guid}/members", async (
    Guid familyId, HttpContext http, AddFamilyMemberRequest req,
    FamilyDbContext db, CancellationToken ct) =>
{
    if (!TryUserId(http.User, out var userId)) return Results.Unauthorized();
    if (!FamilyInput.TryPublicId(req.PublicId, out var publicId))
        return Results.BadRequest(new { error = "PublicId inválido." });

    var family = await db.Families.Include(f => f.Members)
        .FirstOrDefaultAsync(f => f.Id == familyId, ct);
    if (family is null) return Results.NotFound();

    var current = family.Members.FirstOrDefault(m => m.UserId == userId);
    if (current?.Role != "Head") return Results.Forbid();

    var target = await db.Users.FirstOrDefaultAsync(
        u => u.PublicId == publicId, ct);
    if (target is null) return Results.NotFound(new { error = "PublicId não encontrado." });
    if (family.Members.Any(m => m.UserId == target.Id))
        return Results.Conflict(new { error = "Usuário já pertence à família." });

    family.Members.Add(new FamilyMember
    {
        Id = Guid.NewGuid(),
        UserId = target.Id,
        Role = "Member",
        AddedById = userId,
        JoinedAt = DateTimeOffset.UtcNow
    });
    await db.SaveChangesAsync(ct);
    return Results.Created($"/api/v1/families/{familyId}/members",
        new { target.Id, target.PublicId, target.Username });
}).RequireAuthorization();

await app.RunAsync();

static TokenValidationParameters JwtValidation(IConfiguration c, IHostEnvironment environment) => new()
{
    ValidateIssuer = true,
    ValidateAudience = true,
    ValidateIssuerSigningKey = true,
    ValidateLifetime = true,
    ValidIssuer = c["JWT:Issuer"] ?? "familychat",
    ValidAudience = c["JWT:Audience"] ?? "familychat-web",
    IssuerSigningKey = JwtKeyFactory.ValidationKey(c, environment),
    ClockSkew = TimeSpan.FromSeconds(10)
};

static bool TryUserId(ClaimsPrincipal p, out Guid id)
{
    var raw = p.FindFirstValue("sub") ?? p.FindFirstValue(ClaimTypes.NameIdentifier);
    return Guid.TryParse(raw, out id);
}

record CreateFamilyRequest(string Name, string? Description);
record AddFamilyMemberRequest(string PublicId);

static class FamilyInput
{
    private const string PublicIdAlphabet = "ABCDEFGHJKLMNPQRSTUVWXYZ23456789";

    public static bool TryText(
        string? nameInput, string? descriptionInput,
        out string name, out string? description)
    {
        name = nameInput?.Trim() ?? "";
        description = string.IsNullOrWhiteSpace(descriptionInput)
            ? null
            : descriptionInput.Trim();
        return name.Length is >= 1 and <= 100 && description?.Length is null or <= 1000;
    }

    public static bool TryPublicId(string? input, out string publicId)
    {
        publicId = input?.Trim().ToUpperInvariant() ?? "";
        return publicId.Length == 8 && publicId.All(PublicIdAlphabet.Contains);
    }
}
record UserDto(Guid Id, string PublicId, string Username, string Email)
{
    public static UserDto From(UserProjection u) => new(u.Id, u.PublicId, u.Username, u.Email);
}
record FamilyDto(Guid Id, string Name, string Role, int MembersCount);

class UserProjection
{
    public Guid Id { get; set; }
    public string PublicId { get; set; } = "";
    public string Username { get; set; } = "";
    public string Email { get; set; } = "";
    public DateTimeOffset CreatedAt { get; set; }
    public List<FamilyMember> Families { get; set; } = [];
}

class FamilyEntity
{
    public Guid Id { get; set; }
    public string Name { get; set; } = "";
    public string? Description { get; set; }
    public DateTimeOffset CreatedAt { get; set; }
    public List<FamilyMember> Members { get; set; } = [];
}

class FamilyMember
{
    public Guid Id { get; set; }
    public Guid FamilyId { get; set; }
    public Guid UserId { get; set; }
    public string Role { get; set; } = "Member";
    public Guid AddedById { get; set; }
    public DateTimeOffset JoinedAt { get; set; }
    public FamilyEntity Family { get; set; } = null!;
    public UserProjection User { get; set; } = null!;
}

class FamilyDbContext(DbContextOptions<FamilyDbContext> options) : DbContext(options)
{
    public DbSet<UserProjection> Users => Set<UserProjection>();
    public DbSet<FamilyEntity> Families => Set<FamilyEntity>();
    public DbSet<FamilyMember> FamilyMembers => Set<FamilyMember>();

    protected override void OnModelCreating(ModelBuilder b)
    {
        b.Entity<UserProjection>().ToTable("users").HasKey(x => x.Id);
        b.Entity<UserProjection>().HasIndex(x => x.PublicId).IsUnique();
        b.Entity<UserProjection>().HasIndex(x => x.Email).IsUnique();
        b.Entity<UserProjection>().Property(x => x.PublicId).HasMaxLength(8);
        b.Entity<UserProjection>().Property(x => x.Username).HasMaxLength(100);
        b.Entity<UserProjection>().Property(x => x.Email).HasMaxLength(255);

        b.Entity<FamilyEntity>().ToTable("families").HasKey(x => x.Id);
        b.Entity<FamilyEntity>().Property(x => x.Name).HasMaxLength(100);
        b.Entity<FamilyEntity>().Property(x => x.Description).HasMaxLength(1000);

        b.Entity<FamilyMember>().ToTable("family_members").HasKey(x => x.Id);
        b.Entity<FamilyMember>().HasIndex(x => new { x.FamilyId, x.UserId }).IsUnique();
        b.Entity<FamilyMember>().HasOne(x => x.Family).WithMany(x => x.Members)
            .HasForeignKey(x => x.FamilyId).OnDelete(DeleteBehavior.Cascade);
        b.Entity<FamilyMember>().HasOne(x => x.User).WithMany(x => x.Families)
            .HasForeignKey(x => x.UserId).OnDelete(DeleteBehavior.Cascade);
    }
}

class UserRegisteredConsumer(FamilyDbContext db, ILogger<UserRegisteredConsumer> logger)
    : IConsumer<UserRegisteredEvent>
{
    public async Task Consume(ConsumeContext<UserRegisteredEvent> context)
    {
        var e = context.Message;
        if (await db.Users.AnyAsync(u => u.Id == e.UserId, context.CancellationToken))
            return;

        string publicId;
        do
        {
            publicId = GeneratePublicId();
        } while (await db.Users.AnyAsync(u => u.PublicId == publicId, context.CancellationToken));

        db.Users.Add(new UserProjection
        {
            Id = e.UserId,
            PublicId = publicId,
            Username = e.Username,
            Email = e.Email,
            CreatedAt = DateTimeOffset.UtcNow
        });
        try
        {
            await db.SaveChangesAsync(context.CancellationToken);
        }
        catch (DbUpdateException ex) when (
            ex.InnerException is PostgresException
            {
                SqlState: PostgresErrorCodes.UniqueViolation,
                ConstraintName: "PK_users"
            })
        {
            db.ChangeTracker.Clear();
            logger.LogInformation("Duplicate UserRegisteredEvent ignored for {UserId}", e.UserId);
            return;
        }
        logger.LogInformation("User projection created {UserId} -> {PublicId}", e.UserId, publicId);
    }

    static string GeneratePublicId()
    {
        const string alphabet = "ABCDEFGHJKLMNPQRSTUVWXYZ23456789";
        Span<char> chars = stackalloc char[8];
        for (var i = 0; i < chars.Length; i++)
            chars[i] = alphabet[RandomNumberGenerator.GetInt32(alphabet.Length)];
        return new string(chars);
    }
}

class FamilyGrpcService(FamilyDbContext db) : FamilyGraphGrpc.FamilyGraphGrpcBase
{
    public override async Task<UserInfoResponse> ResolvePublicId(
        ResolvePublicIdRequest request, ServerCallContext context)
    {
        if (!FamilyInput.TryPublicId(request.PublicId, out var publicId))
            return new UserInfoResponse { Found = false };
        var u = await db.Users.AsNoTracking()
            .FirstOrDefaultAsync(x => x.PublicId == publicId, context.CancellationToken);
        return u is null ? new UserInfoResponse { Found = false } : Map(u);
    }

    public override async Task<UserInfoResponse> GetUserById(
        GetUserByIdRequest request, ServerCallContext context)
    {
        if (!Guid.TryParse(request.UserId, out var id))
            return new UserInfoResponse { Found = false };
        var u = await db.Users.AsNoTracking()
            .FirstOrDefaultAsync(x => x.Id == id, context.CancellationToken);
        return u is null ? new UserInfoResponse { Found = false } : Map(u);
    }

    public override async Task<UsersResponse> GetUsersByIds(
        GetUsersByIdsRequest request, ServerCallContext context)
    {
        var ids = request.UserIds
            .Select(x => Guid.TryParse(x, out var id) ? id : Guid.Empty)
            .Where(x => x != Guid.Empty)
            .ToList();
        var users = await db.Users.AsNoTracking()
            .Where(x => ids.Contains(x.Id))
            .ToListAsync(context.CancellationToken);
        var response = new UsersResponse();
        response.Users.AddRange(users.Select(Map));
        return response;
    }

    static UserInfoResponse Map(UserProjection u) => new()
    {
        Found = true,
        UserId = u.Id.ToString(),
        PublicId = u.PublicId,
        Username = u.Username,
        Email = u.Email
    };
}
