using System.Security.Claims;
using System.Text;
using System.Text.Json;
using FamilyChat.Contracts.Events;
using FamilyChat.Contracts.Family;
using FamilyChat.Contracts.Room;
using FamilyChat.ServiceDefaults;
using Grpc.Core;
using MassTransit;
using Microsoft.AspNetCore.Authentication.JwtBearer;
using Microsoft.AspNetCore.Server.Kestrel.Core;
using Microsoft.EntityFrameworkCore;
using Microsoft.IdentityModel.Tokens;

var builder = WebApplication.CreateBuilder(args);
builder.WebHost.ConfigureKestrel(o =>
{
    o.ListenAnyIP(8080, lo => lo.Protocols = HttpProtocols.Http1);
    o.ListenAnyIP(8081, lo => lo.Protocols = HttpProtocols.Http2);
});
builder.AddFamilyChatObservability("room-svc");

builder.Services.AddDbContext<RoomDbContext>(o =>
    o.UseNpgsql(builder.Configuration.GetConnectionString("Default")));
builder.Services.AddGrpc();
builder.Services.AddHostedService<RoomOutboxPublisher>();
var internalToken = InternalServiceAuth.RequiredToken(builder.Configuration, "InternalAuth:Token");
var familyToken = InternalServiceAuth.RequiredToken(builder.Configuration, "InternalAuth:FamilyToken");
builder.Services.AddGrpcClient<FamilyGraphGrpc.FamilyGraphGrpcClient>(o =>
    o.Address = new Uri(builder.Configuration["Services:FamilyGraph"] ?? "http://family-svc:8081"))
    .ConfigureChannel(o => o.UnsafeUseInsecureChannelCallCredentials = true)
    .AddCallCredentials((_, metadata) =>
    {
        metadata.Add(InternalServiceAuth.HeaderName, familyToken);
        return Task.CompletedTask;
    });

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
    x.AddConsumer<UserDeletedRoomConsumer>();
    x.UsingRabbitMq((ctx, cfg) =>
    {
        cfg.Host(builder.Configuration["RabbitMQ:Host"] ?? "rabbitmq", "/", h =>
        {
            h.Username(rabbitMq.Username);
            h.Password(rabbitMq.Password);
        });
        cfg.UseMessageRetry(r => r.Interval(3, TimeSpan.FromSeconds(1)));
        cfg.ConfigureEndpoints(ctx);
    });
});

var app = builder.Build();
app.UseAuthentication();
app.Use(async (context, next) =>
{
    if (context.Request.Path.StartsWithSegments("/api/v1/rooms") &&
        TryUserId(context.User, out var userId) &&
        await context.RequestServices.GetRequiredService<RoomDbContext>()
            .DeletedUsers.AsNoTracking().AnyAsync(user => user.UserId == userId,
                context.RequestAborted))
    {
        context.Response.StatusCode = StatusCodes.Status401Unauthorized;
        return;
    }
    await next();
});
app.UseAuthorization();
app.UseInternalGrpcAuthentication("/room.RoomGrpc", internalToken);

using (var scope = app.Services.CreateScope())
{
    await scope.ServiceProvider.GetRequiredService<RoomDbContext>().Database.MigrateAsync();
}

app.MapHealthChecks("/health");
app.MapGrpcService<RoomGrpcService>();

app.MapGet("/api/v1/rooms", async (HttpContext http, RoomDbContext db, CancellationToken ct) =>
{
    if (!TryUserId(http.User, out var userId)) return Results.Unauthorized();
    var rooms = await db.RoomMembers.AsNoTracking()
        .Where(m => m.UserId == userId && m.Room.ArchivedAt == null)
        .OrderByDescending(m => m.JoinedAt)
        .Select(m => new RoomDto(
            m.Room.Id, m.Room.Name, m.Room.Description, m.Room.OwnerId,
            m.Room.Members.Count, m.Role, m.Room.CreatedAt))
        .ToListAsync(ct);
    return Results.Ok(rooms);
}).RequireAuthorization();

app.MapPost("/api/v1/rooms", async (
    HttpContext http, CreateRoomRequest req, RoomDbContext db,
    CancellationToken ct) =>
{
    if (!TryUserId(http.User, out var userId)) return Results.Unauthorized();
    if (!RoomInput.TryText(req.Name, req.Description, out var name, out var description))
        return Results.BadRequest(new { error = "Nome deve ter 1–100 e descrição até 1000 caracteres." });

    var room = new RoomEntity
    {
        Id = Guid.NewGuid(),
        Name = name,
        Description = description,
        OwnerId = userId,
        CreatedAt = DateTimeOffset.UtcNow
    };
    room.Members.Add(new RoomMember
    {
        Id = Guid.NewGuid(),
        UserId = userId,
        Role = "Admin",
        AddedById = userId,
        JoinedAt = DateTimeOffset.UtcNow
    });
    db.Rooms.Add(room);
    db.Audits.Add(RoomAudit.Create(room.Id, userId, null, "room.created"));
    var created = new RoomCreatedEvent(room.Id, userId, room.Name, room.CreatedAt);
    db.OutboxMessages.Add(OutboxMessage.Create(Guid.NewGuid(), nameof(RoomCreatedEvent), created));
    await db.SaveChangesAsync(ct);

    return Results.Created($"/api/v1/rooms/{room.Id}",
        new RoomDto(room.Id, room.Name, room.Description, room.OwnerId, 1, "Admin", room.CreatedAt));
}).RequireAuthorization();

app.MapGet("/api/v1/rooms/{roomId:guid}", async (
    Guid roomId, HttpContext http, RoomDbContext db, CancellationToken ct) =>
{
    if (!TryUserId(http.User, out var userId)) return Results.Unauthorized();
    var room = await db.Rooms.AsNoTracking().Include(r => r.Members)
        .FirstOrDefaultAsync(r => r.Id == roomId && r.ArchivedAt == null, ct);
    if (room is null) return Results.NotFound();
    var member = room.Members.FirstOrDefault(m => m.UserId == userId);
    if (member is null) return Results.Forbid();
    return Results.Ok(new RoomDto(
        room.Id, room.Name, room.Description, room.OwnerId,
        room.Members.Count, member.Role, room.CreatedAt));
}).RequireAuthorization();

app.MapPatch("/api/v1/rooms/{roomId:guid}", async (
    Guid roomId, HttpContext http, UpdateRoomRequest req, RoomDbContext db, CancellationToken ct) =>
{
    if (!TryUserId(http.User, out var userId)) return Results.Unauthorized();
    var room = await db.Rooms.Include(r => r.Members)
        .FirstOrDefaultAsync(r => r.Id == roomId && r.ArchivedAt == null, ct);
    if (room is null) return Results.NotFound();
    var actor = room.Members.FirstOrDefault(m => m.UserId == userId);
    if (!RoomAuthorization.CanManageRoom(actor?.Role)) return Results.Forbid();
    if (!RoomInput.TryText(req.Name, req.Description, out var name, out var description))
        return Results.BadRequest(new { error = "Nome deve ter 1–100 e descrição até 1000 caracteres." });

    room.Name = name;
    room.Description = description;
    db.Audits.Add(RoomAudit.Create(room.Id, userId, null, "room.updated"));
    await db.SaveChangesAsync(ct);
    return Results.Ok(new RoomDto(room.Id, room.Name, room.Description, room.OwnerId,
        room.Members.Count, actor!.Role, room.CreatedAt));
}).RequireAuthorization();

app.MapPost("/api/v1/rooms/{roomId:guid}/owner", async (
    Guid roomId, HttpContext http, TransferRoomOwnerRequest req,
    RoomDbContext db, CancellationToken ct) =>
{
    if (!TryUserId(http.User, out var userId)) return Results.Unauthorized();
    var room = await db.Rooms.Include(r => r.Members)
        .FirstOrDefaultAsync(r => r.Id == roomId && r.ArchivedAt == null, ct);
    if (room is null) return Results.NotFound();

    var decision = RoomOwnership.Transfer(room, userId, req.UserId);
    if (decision == RoomOwnershipDecision.Forbidden) return Results.Forbid();
    if (decision == RoomOwnershipDecision.TargetNotMember) return Results.NotFound();
    if (decision == RoomOwnershipDecision.TargetAlreadyOwner)
        return Results.Conflict(new { error = "O usuário informado já é owner da sala." });

    db.Audits.Add(RoomAudit.Create(
        room.Id, userId, req.UserId, "room.owner_transferred", $"{userId}->{req.UserId}"));
    await db.SaveChangesAsync(ct);
    var actor = room.Members.First(m => m.UserId == userId);
    return Results.Ok(new RoomDto(
        room.Id, room.Name, room.Description, room.OwnerId,
        room.Members.Count, actor.Role, room.CreatedAt));
}).RequireAuthorization();

app.MapPost("/api/v1/rooms/{roomId:guid}/leave", async (
    Guid roomId, HttpContext http, RoomDbContext db, CancellationToken ct) =>
{
    if (!TryUserId(http.User, out var userId)) return Results.Unauthorized();
    var room = await db.Rooms.Include(r => r.Members)
        .FirstOrDefaultAsync(r => r.Id == roomId && r.ArchivedAt == null, ct);
    if (room is null) return Results.NotFound();
    if (room.OwnerId == userId)
        return Results.Conflict(new { error = "O owner deve transferir a sala ou arquivá-la antes de sair." });
    var member = room.Members.FirstOrDefault(m => m.UserId == userId);
    if (member is null) return Results.NotFound();
    db.RoomMembers.Remove(member);
    db.Audits.Add(RoomAudit.Create(room.Id, userId, userId, "member.left"));
    var removed = new RoomMemberRemovedEvent(
        roomId, userId, userId, "left", DateTimeOffset.UtcNow);
    db.OutboxMessages.Add(OutboxMessage.Create(
        Guid.NewGuid(), nameof(RoomMemberRemovedEvent), removed));
    await db.SaveChangesAsync(ct);
    return Results.NoContent();
}).RequireAuthorization();

app.MapDelete("/api/v1/rooms/{roomId:guid}", async (
    Guid roomId, HttpContext http, RoomDbContext db, CancellationToken ct) =>
{
    if (!TryUserId(http.User, out var userId)) return Results.Unauthorized();
    var room = await db.Rooms.FirstOrDefaultAsync(
        r => r.Id == roomId && r.ArchivedAt == null, ct);
    if (room is null) return Results.NotFound();
    if (room.OwnerId != userId) return Results.Forbid();
    room.ArchivedAt = DateTimeOffset.UtcNow;
    db.Audits.Add(RoomAudit.Create(room.Id, userId, null, "room.archived"));
    var archived = new RoomArchivedEvent(room.Id, userId, room.ArchivedAt.Value);
    db.OutboxMessages.Add(OutboxMessage.Create(
        Guid.NewGuid(), nameof(RoomArchivedEvent), archived));
    await db.SaveChangesAsync(ct);
    return Results.NoContent();
}).RequireAuthorization();

app.MapGet("/api/v1/rooms/{roomId:guid}/audit", async (
    Guid roomId, int? take, HttpContext http, RoomDbContext db, CancellationToken ct) =>
{
    if (!TryUserId(http.User, out var userId)) return Results.Unauthorized();
    var ownerId = await db.Rooms.AsNoTracking()
        .Where(r => r.Id == roomId)
        .Select(r => (Guid?)r.OwnerId)
        .FirstOrDefaultAsync(ct);
    if (ownerId is null) return Results.NotFound();
    if (ownerId != userId) return Results.Forbid();

    var limit = Math.Clamp(take ?? 100, 1, 200);
    var rows = await db.Audits.AsNoTracking()
        .Where(a => a.RoomId == roomId)
        .OrderByDescending(a => a.OccurredAt)
        .Take(limit)
        .Select(a => new RoomAuditDto(
            a.Id, a.ActorId, a.TargetUserId, a.Action, a.Detail, a.OccurredAt))
        .ToListAsync(ct);
    return Results.Ok(rows);
}).RequireAuthorization();

app.MapGet("/api/v1/rooms/{roomId:guid}/members", async (
    Guid roomId, HttpContext http, RoomDbContext db,
    FamilyGraphGrpc.FamilyGraphGrpcClient familyClient, CancellationToken ct) =>
{
    if (!TryUserId(http.User, out var userId)) return Results.Unauthorized();
    if (!await db.RoomMembers.AnyAsync(m => m.RoomId == roomId && m.UserId == userId &&
        m.Room.ArchivedAt == null, ct))
        return Results.Forbid();

    var members = await db.RoomMembers.AsNoTracking()
        .Where(m => m.RoomId == roomId).OrderBy(m => m.JoinedAt).ToListAsync(ct);

    var request = new GetUsersByIdsRequest();
    request.UserIds.AddRange(members.Select(m => m.UserId.ToString()));
    UsersResponse users;
    try
    {
        users = await familyClient.GetUsersByIdsAsync(
            request, deadline: DateTime.UtcNow.AddSeconds(3), cancellationToken: ct);
    }
    catch (RpcException ex) when (ex.StatusCode is StatusCode.Unavailable or StatusCode.DeadlineExceeded)
    {
        users = new UsersResponse();
    }
    var map = users.Users
        .Where(u => Guid.TryParse(u.UserId, out _))
        .ToDictionary(u => Guid.Parse(u.UserId));

    return Results.Ok(members.Select(m =>
    {
        map.TryGetValue(m.UserId, out var u);
        return new RoomMemberDto(
            m.UserId, u?.PublicId ?? "", u?.Username ?? "Usuário",
            m.Role, m.JoinedAt);
    }));
}).RequireAuthorization();

app.MapPost("/api/v1/rooms/{roomId:guid}/members/by-public-id", async (
    Guid roomId, HttpContext http, AddMemberRequest req, RoomDbContext db,
    FamilyGraphGrpc.FamilyGraphGrpcClient familyClient,
    CancellationToken ct) =>
{
    if (!TryUserId(http.User, out var userId)) return Results.Unauthorized();
    if (!RoomInput.TryPublicId(req.PublicId, out var publicId))
        return Results.BadRequest(new { error = "PublicId inválido." });
    if (!RoomInput.TryRole(req.Role, out var role))
        return Results.BadRequest(new { error = "Role inválida." });

    var room = await db.Rooms.Include(r => r.Members)
        .FirstOrDefaultAsync(r => r.Id == roomId && r.ArchivedAt == null, ct);
    if (room is null) return Results.NotFound();

    var current = room.Members.FirstOrDefault(m => m.UserId == userId);
    if (!RoomAuthorization.CanAssignRole(userId, current?.Role, room.OwnerId, role))
        return Results.Forbid();
    if (room.Members.Count >= RoomPolicy.MaxMembers)
        return Results.Conflict(new { error = $"A sala atingiu o limite de {RoomPolicy.MaxMembers} membros." });

    UserInfoResponse resolved;
    try
    {
        resolved = await familyClient.ResolvePublicIdAsync(
            new ResolvePublicIdRequest { PublicId = publicId },
            deadline: DateTime.UtcNow.AddSeconds(3), cancellationToken: ct);
    }
    catch (RpcException ex) when (ex.StatusCode is StatusCode.Unavailable or StatusCode.DeadlineExceeded)
    {
        return Results.Json(
            new { error = "Family Graph temporariamente indisponível." }, statusCode: 503);
    }
    if (!resolved.Found || !Guid.TryParse(resolved.UserId, out var targetId))
        return Results.NotFound(new { error = "PublicId não encontrado." });
    if (await db.DeletedUsers.AsNoTracking().AnyAsync(user => user.UserId == targetId, ct))
        return Results.NotFound(new { error = "PublicId não encontrado." });
    if (room.Members.Any(m => m.UserId == targetId))
        return Results.Conflict(new { error = "Usuário já está na sala." });

    var member = new RoomMember
    {
        Id = Guid.NewGuid(),
        RoomId = roomId,
        UserId = targetId,
        Role = role,
        AddedById = userId,
        JoinedAt = DateTimeOffset.UtcNow
    };
    db.RoomMembers.Add(member);
    db.Audits.Add(RoomAudit.Create(roomId, userId, targetId, "member.added", role));
    var added = new RoomMemberAddedEvent(roomId, targetId, userId, role, DateTimeOffset.UtcNow);
    db.OutboxMessages.Add(OutboxMessage.Create(Guid.NewGuid(), nameof(RoomMemberAddedEvent), added));
    await db.SaveChangesAsync(ct);

    return Results.Created($"/api/v1/rooms/{roomId}/members",
        new RoomMemberDto(targetId, resolved.PublicId, resolved.Username, role, member.JoinedAt));
}).RequireAuthorization();

app.MapPatch("/api/v1/rooms/{roomId:guid}/members/{targetId:guid}/role", async (
    Guid roomId, Guid targetId, HttpContext http, UpdateMemberRoleRequest req,
    RoomDbContext db, CancellationToken ct) =>
{
    if (!TryUserId(http.User, out var userId)) return Results.Unauthorized();
    var room = await db.Rooms.Include(r => r.Members)
        .FirstOrDefaultAsync(r => r.Id == roomId && r.ArchivedAt == null, ct);
    if (room is null) return Results.NotFound();
    var actor = room.Members.FirstOrDefault(m => m.UserId == userId);
    var target = room.Members.FirstOrDefault(m => m.UserId == targetId);
    if (target is null) return Results.NotFound();

    if (!RoomInput.TryRole(req.Role, out var role))
        return Results.BadRequest(new { error = "Role inválida." });
    var decision = RoomAuthorization.CanChangeRole(
        userId, actor?.Role, room.OwnerId, target.UserId, target.Role, role);
    if (decision == AuthorizationDecision.OwnerProtected)
        return Results.Conflict(new { error = "A role do owner não pode ser alterada." });
    if (decision != AuthorizationDecision.Allowed) return Results.Forbid();

    var previousRole = target.Role;
    if (previousRole == role)
        return Results.Ok(new { target.UserId, target.Role });

    target.Role = role;
    db.Audits.Add(RoomAudit.Create(roomId, userId, targetId, "member.role_changed", role));
    var changed = new RoomMemberRoleChangedEvent(
        roomId, targetId, userId, previousRole, role, DateTimeOffset.UtcNow);
    db.OutboxMessages.Add(OutboxMessage.Create(
        Guid.NewGuid(), nameof(RoomMemberRoleChangedEvent), changed));
    await db.SaveChangesAsync(ct);
    return Results.Ok(new { target.UserId, target.Role });
}).RequireAuthorization();

app.MapDelete("/api/v1/rooms/{roomId:guid}/members/{targetId:guid}", async (
    Guid roomId, Guid targetId, HttpContext http, RoomDbContext db, CancellationToken ct) =>
{
    if (!TryUserId(http.User, out var userId)) return Results.Unauthorized();
    var room = await db.Rooms.Include(r => r.Members)
        .FirstOrDefaultAsync(r => r.Id == roomId && r.ArchivedAt == null, ct);
    if (room is null) return Results.NotFound();
    var actor = room.Members.FirstOrDefault(m => m.UserId == userId);
    var target = room.Members.FirstOrDefault(m => m.UserId == targetId);
    if (target is null) return Results.NotFound();
    var decision = RoomAuthorization.CanRemove(
        userId, actor?.Role, room.OwnerId, target.UserId, target.Role);
    if (decision == AuthorizationDecision.OwnerProtected)
        return Results.Conflict(new { error = "O owner não pode ser removido." });
    if (decision != AuthorizationDecision.Allowed) return Results.Forbid();

    db.RoomMembers.Remove(target);
    db.Audits.Add(RoomAudit.Create(roomId, userId, targetId, "member.removed", target.Role));
    var removed = new RoomMemberRemovedEvent(
        roomId, targetId, userId, "removed", DateTimeOffset.UtcNow);
    db.OutboxMessages.Add(OutboxMessage.Create(
        Guid.NewGuid(), nameof(RoomMemberRemovedEvent), removed));
    await db.SaveChangesAsync(ct);
    return Results.NoContent();
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

record CreateRoomRequest(string Name, string? Description);
record UpdateRoomRequest(string Name, string? Description);
record AddMemberRequest(string PublicId, string? Role);
record UpdateMemberRoleRequest(string? Role);
record TransferRoomOwnerRequest(Guid UserId);
record RoomDto(Guid Id, string Name, string? Description, Guid OwnerId, int MembersCount, string Role, DateTimeOffset CreatedAt);
record RoomMemberDto(Guid UserId, string PublicId, string Username, string Role, DateTimeOffset JoinedAt);
record RoomAuditDto(Guid Id, Guid ActorId, Guid? TargetUserId, string Action, string? Detail, DateTimeOffset OccurredAt);

static class RoomPolicy
{
    public const int MaxMembers = 250;
}

static class RoomInput
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

    public static bool TryRole(string? input, out string role)
    {
        if (string.IsNullOrWhiteSpace(input))
        {
            role = "Member";
            return true;
        }

        role = input.Trim();
        return role is "Admin" or "Member" or "Muted";
    }
}

enum AuthorizationDecision { Allowed, Forbidden, OwnerProtected }
enum RoomOwnershipDecision { Allowed, Forbidden, TargetNotMember, TargetAlreadyOwner }

static class RoomAuthorization
{
    public static bool CanManageRoom(string? actorRole) => actorRole == "Admin";

    public static bool CanAssignRole(
        Guid actorId, string? actorRole, Guid ownerId, string role) =>
        CanManageRoom(actorRole) && (role != "Admin" || actorId == ownerId);

    public static AuthorizationDecision CanChangeRole(
        Guid actorId, string? actorRole, Guid ownerId,
        Guid targetId, string targetRole, string newRole)
    {
        if (!CanManageRoom(actorRole)) return AuthorizationDecision.Forbidden;
        if (targetId == ownerId) return AuthorizationDecision.OwnerProtected;
        if (actorId != ownerId && (targetRole == "Admin" || newRole == "Admin"))
            return AuthorizationDecision.Forbidden;
        return AuthorizationDecision.Allowed;
    }

    public static AuthorizationDecision CanRemove(
        Guid actorId, string? actorRole, Guid ownerId, Guid targetId, string targetRole)
    {
        if (!CanManageRoom(actorRole)) return AuthorizationDecision.Forbidden;
        if (targetId == ownerId) return AuthorizationDecision.OwnerProtected;
        if (actorId != ownerId && targetRole == "Admin") return AuthorizationDecision.Forbidden;
        return AuthorizationDecision.Allowed;
    }
}

static class RoomOwnership
{
    public static RoomOwnershipDecision Transfer(RoomEntity room, Guid actorId, Guid targetId)
    {
        if (room.OwnerId != actorId) return RoomOwnershipDecision.Forbidden;
        if (targetId == room.OwnerId) return RoomOwnershipDecision.TargetAlreadyOwner;
        var target = room.Members.FirstOrDefault(m => m.UserId == targetId);
        if (target is null) return RoomOwnershipDecision.TargetNotMember;

        var previousOwner = room.Members.FirstOrDefault(m => m.UserId == actorId);
        if (previousOwner is not null) previousOwner.Role = "Admin";
        target.Role = "Admin";
        room.OwnerId = targetId;
        return RoomOwnershipDecision.Allowed;
    }
}

class RoomEntity
{
    public Guid Id { get; set; }
    public string Name { get; set; } = "";
    public string? Description { get; set; }
    public Guid OwnerId { get; set; }
    public DateTimeOffset CreatedAt { get; set; }
    public DateTimeOffset? ArchivedAt { get; set; }
    public List<RoomMember> Members { get; set; } = [];
}

class RoomMember
{
    public Guid Id { get; set; }
    public Guid RoomId { get; set; }
    public Guid UserId { get; set; }
    public string Role { get; set; } = "Member";
    public Guid AddedById { get; set; }
    public DateTimeOffset JoinedAt { get; set; }
    public RoomEntity Room { get; set; } = null!;
}

class RoomDbContext(DbContextOptions<RoomDbContext> options) : DbContext(options)
{
    public DbSet<RoomEntity> Rooms => Set<RoomEntity>();
    public DbSet<RoomMember> RoomMembers => Set<RoomMember>();
    public DbSet<OutboxMessage> OutboxMessages => Set<OutboxMessage>();
    public DbSet<RoomAudit> Audits => Set<RoomAudit>();
    public DbSet<DeletedRoomUser> DeletedUsers => Set<DeletedRoomUser>();

    protected override void OnModelCreating(ModelBuilder b)
    {
        b.Entity<RoomEntity>().ToTable("rooms").HasKey(x => x.Id);
        b.Entity<RoomEntity>().Property(x => x.Name).HasMaxLength(100);
        b.Entity<RoomEntity>().Property(x => x.Description).HasMaxLength(1000);

        b.Entity<RoomMember>().ToTable("room_members").HasKey(x => x.Id);
        b.Entity<RoomMember>().HasIndex(x => new { x.RoomId, x.UserId }).IsUnique();
        b.Entity<RoomMember>().HasOne(x => x.Room).WithMany(x => x.Members)
            .HasForeignKey(x => x.RoomId).OnDelete(DeleteBehavior.Cascade);

        b.Entity<OutboxMessage>().ToTable("outbox_messages").HasKey(x => x.Id);
        b.Entity<OutboxMessage>().HasIndex(x => new { x.PublishedAt, x.NextAttemptAt, x.OccurredAt });
        b.Entity<OutboxMessage>().Property(x => x.Type).HasMaxLength(200);

        b.Entity<RoomAudit>().ToTable("room_audit").HasKey(x => x.Id);
        b.Entity<RoomAudit>().HasIndex(x => new { x.RoomId, x.OccurredAt });
        b.Entity<RoomAudit>().Property(x => x.Action).HasMaxLength(100);
        b.Entity<RoomAudit>().Property(x => x.Detail).HasMaxLength(500);

        b.Entity<DeletedRoomUser>().ToTable("deleted_room_users").HasKey(x => x.UserId);
        b.Entity<DeletedRoomUser>().HasIndex(x => x.CorrelationId).IsUnique();
    }
}

class DeletedRoomUser
{
    public Guid UserId { get; set; }
    public Guid CorrelationId { get; set; }
    public DateTimeOffset DeletedAt { get; set; }
}

class RoomAudit
{
    public Guid Id { get; set; }
    public Guid RoomId { get; set; }
    public Guid ActorId { get; set; }
    public Guid? TargetUserId { get; set; }
    public string Action { get; set; } = "";
    public string? Detail { get; set; }
    public DateTimeOffset OccurredAt { get; set; }

    public static RoomAudit Create(
        Guid roomId, Guid actorId, Guid? targetUserId, string action, string? detail = null) => new()
    {
        Id = Guid.NewGuid(),
        RoomId = roomId,
        ActorId = actorId,
        TargetUserId = targetUserId,
        Action = action,
        Detail = detail,
        OccurredAt = DateTimeOffset.UtcNow
    };
}

class OutboxMessage
{
    public Guid Id { get; set; }
    public string Type { get; set; } = "";
    public string Payload { get; set; } = "";
    public DateTimeOffset OccurredAt { get; set; }
    public DateTimeOffset? PublishedAt { get; set; }
    public int Attempts { get; set; }
    public DateTimeOffset? NextAttemptAt { get; set; }
    public string? LastError { get; set; }

    public static OutboxMessage Create<T>(Guid id, string type, T message) => new()
    {
        Id = id,
        Type = type,
        Payload = JsonSerializer.Serialize(message),
        OccurredAt = DateTimeOffset.UtcNow
    };
}

class UserDeletedRoomConsumer(RoomDbContext db) : IConsumer<UserDeletedEvent>
{
    public Task Consume(ConsumeContext<UserDeletedEvent> context) =>
        RoomUserDeletion.ApplyAsync(db, context.Message, context.CancellationToken);
}

static class RoomUserDeletion
{
    public static async Task<bool> ApplyAsync(
        RoomDbContext db,
        UserDeletedEvent message,
        CancellationToken ct)
    {
        if (await db.DeletedUsers.AnyAsync(user => user.UserId == message.UserId, ct))
            return false;

        var ownedRooms = await db.Rooms
            .Where(room => room.OwnerId == message.UserId && room.ArchivedAt == null)
            .ToListAsync(ct);
        var memberships = await db.RoomMembers
            .Where(member => member.UserId == message.UserId &&
                member.Room.OwnerId != message.UserId)
            .ToListAsync(ct);

        db.DeletedUsers.Add(new DeletedRoomUser
        {
            UserId = message.UserId,
            CorrelationId = message.CorrelationId,
            DeletedAt = message.OccurredAt
        });

        foreach (var room in ownedRooms)
        {
            room.ArchivedAt = message.OccurredAt;
            db.Audits.Add(RoomAudit.Create(
                room.Id, message.UserId, null, "room.archived", "owner_account_deleted"));
            var archived = new RoomArchivedEvent(
                room.Id, message.UserId, message.OccurredAt);
            db.OutboxMessages.Add(OutboxMessage.Create(
                Guid.NewGuid(), nameof(RoomArchivedEvent), archived));
        }

        foreach (var membership in memberships)
        {
            db.RoomMembers.Remove(membership);
            db.Audits.Add(RoomAudit.Create(
                membership.RoomId, message.UserId, message.UserId,
                "member.removed", "account_deleted"));
            var removed = new RoomMemberRemovedEvent(
                membership.RoomId, message.UserId, message.UserId,
                "account_deleted", message.OccurredAt);
            db.OutboxMessages.Add(OutboxMessage.Create(
                Guid.NewGuid(), nameof(RoomMemberRemovedEvent), removed));
        }

        await db.SaveChangesAsync(ct);
        return true;
    }
}

class RoomOutboxPublisher(IServiceScopeFactory scopeFactory, ILogger<RoomOutboxPublisher> logger)
    : BackgroundService
{
    protected override async Task ExecuteAsync(CancellationToken stoppingToken)
    {
        while (!stoppingToken.IsCancellationRequested)
        {
            try { await PublishBatch(stoppingToken); }
            catch (Exception ex) { logger.LogError(ex, "Room outbox batch failed"); }
            await Task.Delay(TimeSpan.FromSeconds(2), stoppingToken);
        }
    }

    async Task PublishBatch(CancellationToken ct)
    {
        await using var scope = scopeFactory.CreateAsyncScope();
        var db = scope.ServiceProvider.GetRequiredService<RoomDbContext>();
        var publish = scope.ServiceProvider.GetRequiredService<IPublishEndpoint>();
        await using var outboxLock = await PostgresAdvisoryLock.TryAcquireAsync(
            db.Database.GetDbConnection(), 0x524F4F4D4F555442, ct);
        if (outboxLock is null) return;
        var now = DateTimeOffset.UtcNow;
        var messages = await db.OutboxMessages
            .Where(x => x.PublishedAt == null &&
                (x.NextAttemptAt == null || x.NextAttemptAt <= now))
            .OrderBy(x => x.OccurredAt).Take(50).ToListAsync(ct);

        foreach (var message in messages)
        {
            try
            {
                switch (message.Type)
                {
                    case nameof(RoomCreatedEvent):
                        await publish.Publish(JsonSerializer.Deserialize<RoomCreatedEvent>(message.Payload)
                            ?? throw new InvalidOperationException("Invalid room-created payload"), ct);
                        break;
                    case nameof(RoomMemberAddedEvent):
                        await publish.Publish(JsonSerializer.Deserialize<RoomMemberAddedEvent>(message.Payload)
                            ?? throw new InvalidOperationException("Invalid member-added payload"), ct);
                        break;
                    case nameof(RoomMemberRemovedEvent):
                        await publish.Publish(JsonSerializer.Deserialize<RoomMemberRemovedEvent>(message.Payload)
                            ?? throw new InvalidOperationException("Invalid member-removed payload"), ct);
                        break;
                    case nameof(RoomMemberRoleChangedEvent):
                        await publish.Publish(JsonSerializer.Deserialize<RoomMemberRoleChangedEvent>(message.Payload)
                            ?? throw new InvalidOperationException("Invalid member-role-changed payload"), ct);
                        break;
                    case nameof(RoomArchivedEvent):
                        await publish.Publish(JsonSerializer.Deserialize<RoomArchivedEvent>(message.Payload)
                            ?? throw new InvalidOperationException("Invalid room-archived payload"), ct);
                        break;
                    default:
                        throw new InvalidOperationException($"Unknown outbox message type: {message.Type}");
                }
                message.PublishedAt = DateTimeOffset.UtcNow;
                message.NextAttemptAt = null;
                message.LastError = null;
            }
            catch (Exception ex)
            {
                message.Attempts++;
                message.NextAttemptAt = DateTimeOffset.UtcNow.Add(
                    OutboxRetry.Delay(message.Attempts));
                message.LastError = ex.Message[..Math.Min(ex.Message.Length, 2000)];
                FamilyChatMetrics.OutboxPublishFailed.Add(1,
                    new KeyValuePair<string, object?>("service", "room"),
                    new KeyValuePair<string, object?>("event_type", message.Type));
                if (message.Attempts == 20)
                {
                    FamilyChatMetrics.OutboxStalled.Add(1,
                        new KeyValuePair<string, object?>("service", "room"),
                        new KeyValuePair<string, object?>("event_type", message.Type));
                    logger.LogError("Room outbox message {MessageId} remains unpublished after 20 attempts", message.Id);
                }
                logger.LogWarning(ex, "Failed to publish outbox message {MessageId}", message.Id);
            }
            await db.SaveChangesAsync(ct);
        }
    }
}

class RoomGrpcService(RoomDbContext db) : RoomGrpc.RoomGrpcBase
{
    public override async Task<IsMemberResponse> IsMemberOfRoom(
        IsMemberRequest request, ServerCallContext context)
    {
        if (!Guid.TryParse(request.RoomId, out var roomId) ||
            !Guid.TryParse(request.UserId, out var userId))
            return new IsMemberResponse { IsMember = false, Role = "none", CanSendMessages = false };

        if (await db.DeletedUsers.AsNoTracking()
            .AnyAsync(user => user.UserId == userId, context.CancellationToken))
            return new IsMemberResponse { IsMember = false, Role = "none", CanSendMessages = false };

        var member = await db.RoomMembers.AsNoTracking()
            .Where(m => m.RoomId == roomId && m.UserId == userId &&
                m.Room.ArchivedAt == null)
            .Select(m => new
            {
                m.Role,
                m.Room.Name,
                UserIds = m.Room.Members
                    .Where(roomMember => !db.DeletedUsers.Any(
                        deleted => deleted.UserId == roomMember.UserId))
                    .Select(roomMember => roomMember.UserId).ToArray()
            })
            .FirstOrDefaultAsync(context.CancellationToken);
        var response = new IsMemberResponse
        {
            IsMember = member is not null,
            Role = member?.Role ?? "none",
            CanSendMessages = member is not null && member.Role != "Muted",
            RoomName = member?.Name ?? ""
        };
        if (member is not null)
            response.NotificationUserIds.AddRange(member.UserIds.Select(id => id.ToString()));
        return response;
    }

    public override async Task<RoomSummary> GetRoomSummary(
        GetRoomSummaryRequest request, ServerCallContext context)
    {
        if (!Guid.TryParse(request.RoomId, out var roomId))
            throw new RpcException(new Status(StatusCode.InvalidArgument, "room_id inválido"));

        var room = await db.Rooms.AsNoTracking()
            .Where(r => r.Id == roomId && r.ArchivedAt == null)
            .Select(r => new { r.Id, r.Name, Count = r.Members.Count })
            .FirstOrDefaultAsync(context.CancellationToken)
            ?? throw new RpcException(new Status(StatusCode.NotFound, "Sala não encontrada"));

        return new RoomSummary { Id = room.Id.ToString(), Name = room.Name, MembersCount = room.Count };
    }

    public override async Task<RoomNotificationTargets> GetRoomNotificationTargets(
        GetRoomNotificationTargetsRequest request, ServerCallContext context)
    {
        if (!Guid.TryParse(request.RoomId, out var roomId))
            return new RoomNotificationTargets { Found = false };

        var room = await db.Rooms.AsNoTracking()
            .Where(r => r.Id == roomId && r.ArchivedAt == null)
            .Select(r => new
            {
                r.Name,
                UserIds = r.Members
                    .Where(member => !db.DeletedUsers.Any(
                        deleted => deleted.UserId == member.UserId))
                    .Select(member => member.UserId).ToArray()
            })
            .FirstOrDefaultAsync(context.CancellationToken);
        if (room is null) return new RoomNotificationTargets { Found = false };

        var response = new RoomNotificationTargets { Found = true, RoomName = room.Name };
        response.UserIds.AddRange(room.UserIds.Select(id => id.ToString()));
        return response;
    }
}