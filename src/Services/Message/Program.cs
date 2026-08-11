using System.Security.Claims;
using System.Text;
using System.Text.Json;
using FamilyChat.Contracts.Events;
using FamilyChat.Contracts.Room;
using FamilyChat.ServiceDefaults;
using Grpc.Core;
using MassTransit;
using Microsoft.AspNetCore.Authentication.JwtBearer;
using Microsoft.EntityFrameworkCore;
using Microsoft.IdentityModel.Tokens;
using Npgsql;

var builder = WebApplication.CreateBuilder(args);
builder.AddFamilyChatObservability("message-svc");

builder.Services.AddDbContext<MessageDbContext>(o =>
    o.UseNpgsql(builder.Configuration.GetConnectionString("Default")));
builder.Services.AddHostedService<MessageOutboxPublisher>();
builder.Services.AddSingleton(MessageRetentionPolicy.From(builder.Configuration));
builder.Services.AddHostedService<MessageRetentionWorker>();
var roomToken = InternalServiceAuth.RequiredToken(builder.Configuration, "InternalAuth:RoomToken");
builder.Services.AddGrpcClient<RoomGrpc.RoomGrpcClient>(o =>
    o.Address = new Uri(builder.Configuration["Services:Room"] ?? "http://room-svc:8081"))
    .ConfigureChannel(o => o.UnsafeUseInsecureChannelCallCredentials = true)
    .AddCallCredentials((_, metadata) =>
    {
        metadata.Add(InternalServiceAuth.HeaderName, roomToken);
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
    x.AddConsumer<MessageCreatedConsumer>();
    x.AddConsumer<UserDeletedMessageConsumer>();
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
app.UseAuthorization();

using (var scope = app.Services.CreateScope())
{
    await scope.ServiceProvider.GetRequiredService<MessageDbContext>().Database.MigrateAsync();
}

app.MapHealthChecks("/health");

app.MapGet("/api/v1/messages/room/{roomId:guid}", async (
    Guid roomId, int? take, DateTimeOffset? beforeSentAt, Guid? beforeId,
    HttpContext http, MessageDbContext db,
    RoomGrpc.RoomGrpcClient roomClient, CancellationToken ct) =>
{
    if (!TryUserId(http.User, out var userId)) return Results.Unauthorized();
    IsMemberResponse perm;
    try
    {
        perm = await roomClient.IsMemberOfRoomAsync(new IsMemberRequest
        {
            RoomId = roomId.ToString(),
            UserId = userId.ToString()
        }, deadline: DateTime.UtcNow.AddSeconds(3), cancellationToken: ct);
    }
    catch (RpcException ex) when (ex.StatusCode is StatusCode.Unavailable or StatusCode.DeadlineExceeded)
    {
        return Results.Json(
            new { error = "Autorização de sala temporariamente indisponível." }, statusCode: 503);
    }
    if (!perm.IsMember) return Results.Forbid();

    var limit = Math.Clamp(take ?? 100, 1, 200);
    var query = db.Messages.AsNoTracking().Where(m => m.RoomId == roomId);
    if (beforeSentAt is not null)
    {
        var sentAt = beforeSentAt.Value;
        query = beforeId is null
            ? query.Where(m => m.SentAt < sentAt)
            : query.Where(m => m.SentAt < sentAt ||
                (m.SentAt == sentAt && m.Id.CompareTo(beforeId.Value) < 0));
    }

    var rows = await query
        .OrderByDescending(m => m.SentAt)
        .ThenByDescending(m => m.Id)
        .Take(limit)
        .OrderBy(m => m.SentAt)
        .ThenBy(m => m.Id)
        .Select(m => new MessageDto(m.Id, m.RoomId, m.SenderId, m.Content, m.SentAt))
        .ToListAsync(ct);
    return Results.Ok(rows);
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

record MessageDto(Guid Id, Guid RoomId, Guid SenderId, string Content, DateTimeOffset SentAt);

class MessageEntity
{
    public Guid Id { get; set; }
    public Guid RoomId { get; set; }
    public Guid SenderId { get; set; }
    public string Content { get; set; } = "";
    public DateTimeOffset SentAt { get; set; }
}

class MessageDbContext(DbContextOptions<MessageDbContext> options) : DbContext(options)
{
    public DbSet<MessageEntity> Messages => Set<MessageEntity>();
    public DbSet<MessageOutboxMessage> OutboxMessages => Set<MessageOutboxMessage>();
    public DbSet<DeletedMessageUser> DeletedUsers => Set<DeletedMessageUser>();
    protected override void OnModelCreating(ModelBuilder b)
    {
        b.Entity<MessageEntity>().ToTable("messages").HasKey(x => x.Id);
        b.Entity<MessageEntity>().HasIndex(x => new { x.RoomId, x.SentAt, x.Id });
        b.Entity<MessageEntity>().HasIndex(x => x.SentAt);
        b.Entity<MessageEntity>().HasIndex(x => x.SenderId);
        b.Entity<MessageEntity>().Property(x => x.Content).HasMaxLength(2000);
        b.Entity<MessageOutboxMessage>().ToTable("outbox_messages").HasKey(x => x.Id);
        b.Entity<MessageOutboxMessage>().HasIndex(x => new { x.PublishedAt, x.NextAttemptAt, x.OccurredAt });
        b.Entity<MessageOutboxMessage>().Property(x => x.Type).HasMaxLength(200);
        b.Entity<DeletedMessageUser>().ToTable("deleted_users").HasKey(x => x.UserId);
    }
}

class DeletedMessageUser
{
    public Guid UserId { get; set; }
    public DateTimeOffset DeletedAt { get; set; }
}

class MessageCreatedConsumer(MessageDbContext db, ILogger<MessageCreatedConsumer> logger)
    : IConsumer<MessageCreatedEvent>
{
    public async Task Consume(ConsumeContext<MessageCreatedEvent> context)
    {
        try
        {
            var e = context.Message;
            await using var userLock = await PostgresAdvisoryLock.TryAcquireAsync(
                db.Database.GetDbConnection(), MessageUserDeletion.LockKey(e.SenderId),
                context.CancellationToken)
                ?? throw new InvalidOperationException($"Message sender {e.SenderId} is being deleted.");
            var inserted = await MessagePersistence.TryPersistAsync(
                db, e, context.CancellationToken);
            if (!inserted)
            {
                logger.LogInformation(
                    "Duplicate MessageCreatedEvent ignored for {MessageId}", e.MessageId);
                return;
            }
            FamilyChatMetrics.MessagePersisted.Add(1);
            logger.LogInformation("Message persisted {MessageId} room {RoomId}", e.MessageId, e.RoomId);
        }
        catch
        {
            FamilyChatMetrics.MessagePersistenceFailed.Add(1);
            throw;
        }
    }
}

class UserDeletedMessageConsumer(
    MessageDbContext db,
    ILogger<UserDeletedMessageConsumer> logger) : IConsumer<UserDeletedEvent>
{
    public async Task Consume(ConsumeContext<UserDeletedEvent> context)
    {
        var message = context.Message;
        await using var userLock = await PostgresAdvisoryLock.TryAcquireAsync(
            db.Database.GetDbConnection(), MessageUserDeletion.LockKey(message.UserId),
            context.CancellationToken)
            ?? throw new InvalidOperationException($"Messages for user {message.UserId} are being changed.");
        var result = await MessageUserDeletion.ApplyAsync(
            db, message.UserId, message.OccurredAt, context.CancellationToken);
        if (result.Applied)
            logger.LogInformation("Erased {MessageCount} messages for deleted user {UserId}",
                result.DeletedMessages, message.UserId);
        else
            logger.LogInformation("Duplicate UserDeletedEvent ignored for {UserId}", message.UserId);
    }
}

static class MessagePersistence
{
    public static async Task<bool> TryPersistAsync(
        MessageDbContext db,
        MessageCreatedEvent message,
        CancellationToken ct)
    {
        if (await db.DeletedUsers.AnyAsync(user => user.UserId == message.SenderId, ct))
            return false;
        if (await db.Messages.AnyAsync(m => m.Id == message.MessageId, ct))
            return false;

        db.Messages.Add(new MessageEntity
        {
            Id = message.MessageId,
            RoomId = message.RoomId,
            SenderId = message.SenderId,
            Content = message.Content,
            SentAt = message.SentAt
        });
        var persisted = new MessagePersistedEvent(
            message.MessageId, message.RoomId, DateTimeOffset.UtcNow, message.CorrelationId);
        db.OutboxMessages.Add(MessageOutboxMessage.Create(
            Guid.NewGuid(), nameof(MessagePersistedEvent), persisted));

        try
        {
            await db.SaveChangesAsync(ct);
            return true;
        }
        catch (DbUpdateException ex) when (
            ex.InnerException is PostgresException
            {
                SqlState: PostgresErrorCodes.UniqueViolation,
                ConstraintName: "PK_messages"
            })
        {
            db.ChangeTracker.Clear();
            return false;
        }
    }
}

readonly record struct MessageDeletionResult(bool Applied, int DeletedMessages);

static class MessageUserDeletion
{
    public static long LockKey(Guid userId) => BitConverter.ToInt64(userId.ToByteArray());

    public static async Task<MessageDeletionResult> ApplyAsync(
        MessageDbContext db, Guid userId, DateTimeOffset deletedAt, CancellationToken ct)
    {
        if (await db.DeletedUsers.AnyAsync(user => user.UserId == userId, ct))
            return new MessageDeletionResult(false, 0);

        await using var transaction = await db.Database.BeginTransactionAsync(ct);
        var deleted = await db.Messages
            .Where(message => message.SenderId == userId)
            .ExecuteDeleteAsync(ct);
        db.DeletedUsers.Add(new DeletedMessageUser
        {
            UserId = userId,
            DeletedAt = deletedAt
        });
        await db.SaveChangesAsync(ct);
        await transaction.CommitAsync(ct);
        return new MessageDeletionResult(true, deleted);
    }
}

class MessageOutboxMessage
{
    public Guid Id { get; set; }
    public string Type { get; set; } = "";
    public string Payload { get; set; } = "";
    public DateTimeOffset OccurredAt { get; set; }
    public DateTimeOffset? PublishedAt { get; set; }
    public int Attempts { get; set; }
    public DateTimeOffset? NextAttemptAt { get; set; }
    public string? LastError { get; set; }

    public static MessageOutboxMessage Create<T>(Guid id, string type, T message) => new()
    {
        Id = id,
        Type = type,
        Payload = JsonSerializer.Serialize(message),
        OccurredAt = DateTimeOffset.UtcNow
    };
}

class MessageOutboxPublisher(IServiceScopeFactory scopeFactory, ILogger<MessageOutboxPublisher> logger)
    : BackgroundService
{
    protected override async Task ExecuteAsync(CancellationToken stoppingToken)
    {
        while (!stoppingToken.IsCancellationRequested)
        {
            try { await PublishBatch(stoppingToken); }
            catch (Exception ex) { logger.LogError(ex, "Message outbox batch failed"); }
            await Task.Delay(TimeSpan.FromSeconds(2), stoppingToken);
        }
    }

    async Task PublishBatch(CancellationToken ct)
    {
        await using var scope = scopeFactory.CreateAsyncScope();
        var db = scope.ServiceProvider.GetRequiredService<MessageDbContext>();
        var publish = scope.ServiceProvider.GetRequiredService<IPublishEndpoint>();
        await using var outboxLock = await PostgresAdvisoryLock.TryAcquireAsync(
            db.Database.GetDbConnection(), 0x4D4553534147454F, ct);
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
                if (message.Type != nameof(MessagePersistedEvent))
                    throw new InvalidOperationException($"Unknown outbox message type: {message.Type}");
                var payload = JsonSerializer.Deserialize<MessagePersistedEvent>(message.Payload)
                    ?? throw new InvalidOperationException("Invalid outbox payload");
                await publish.Publish(payload, ct);
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
                    new KeyValuePair<string, object?>("service", "message"),
                    new KeyValuePair<string, object?>("event_type", message.Type));
                if (message.Attempts == 20)
                {
                    FamilyChatMetrics.OutboxStalled.Add(1,
                        new KeyValuePair<string, object?>("service", "message"),
                        new KeyValuePair<string, object?>("event_type", message.Type));
                    logger.LogError("Message outbox item {MessageId} remains unpublished after 20 attempts", message.Id);
                }
                logger.LogWarning(ex, "Failed to publish message outbox {MessageId}", message.Id);
            }
            await db.SaveChangesAsync(ct);
        }
    }
}
