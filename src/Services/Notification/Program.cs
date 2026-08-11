using FamilyChat.Contracts.Events;
using FamilyChat.Contracts.Family;
using FamilyChat.Contracts.Room;
using FamilyChat.ServiceDefaults;
using MassTransit;
using Microsoft.EntityFrameworkCore;
using Npgsql;

var builder = WebApplication.CreateBuilder(args);
builder.AddFamilyChatObservability("notification-svc");

builder.Services.AddDbContext<NotificationDbContext>(o =>
    o.UseNpgsql(builder.Configuration.GetConnectionString("Default")));
var roomToken = InternalServiceAuth.RequiredToken(builder.Configuration, "InternalAuth:RoomToken");
var familyToken = InternalServiceAuth.RequiredToken(builder.Configuration, "InternalAuth:FamilyToken");
builder.Services.AddGrpcClient<RoomGrpc.RoomGrpcClient>(o =>
    o.Address = new Uri(builder.Configuration["Services:Room"] ?? "http://room-svc:8081"))
    .ConfigureChannel(o => o.UnsafeUseInsecureChannelCallCredentials = true)
    .AddCallCredentials((_, metadata) =>
    {
        metadata.Add(InternalServiceAuth.HeaderName, roomToken);
        return Task.CompletedTask;
    });
builder.Services.AddGrpcClient<FamilyGraphGrpc.FamilyGraphGrpcClient>(o =>
    o.Address = new Uri(builder.Configuration["Services:FamilyGraph"] ?? "http://family-svc:8081"))
    .ConfigureChannel(o => o.UnsafeUseInsecureChannelCallCredentials = true)
    .AddCallCredentials((_, metadata) =>
    {
        metadata.Add(InternalServiceAuth.HeaderName, familyToken);
        return Task.CompletedTask;
    });
var notificationOptions = NotificationOptions.Load(builder.Configuration);
builder.Services.AddSingleton(notificationOptions);
builder.Services.AddSingleton<INotificationSender, SmtpNotificationSender>();

builder.Services.AddMassTransit(x =>
{
    x.AddConsumer<MessageNotificationConsumer>();
    x.UsingRabbitMq((ctx, cfg) =>
    {
        cfg.Host(builder.Configuration["RabbitMQ:Host"] ?? "rabbitmq", "/", h =>
        {
            h.Username(builder.Configuration["RabbitMQ:User"] ?? "guest");
            h.Password(builder.Configuration["RabbitMQ:Pass"] ?? "guest");
        });
        cfg.UseMessageRetry(r => r.Exponential(
            retryLimit: 5,
            minInterval: TimeSpan.FromSeconds(1),
            maxInterval: TimeSpan.FromMinutes(1),
            intervalDelta: TimeSpan.FromSeconds(5)));
        cfg.ConfigureEndpoints(ctx);
    });
});

var app = builder.Build();
using (var scope = app.Services.CreateScope())
{
    await scope.ServiceProvider.GetRequiredService<NotificationDbContext>()
        .Database.MigrateAsync();
}
app.MapHealthChecks("/health");
app.MapGet("/", () => Results.Ok(new { service = "notification-svc", status = "ok" }));
await app.RunAsync();

class NotificationAudit
{
    public Guid Id { get; set; }
    public Guid MessageId { get; set; }
    public Guid RoomId { get; set; }
    public Guid SenderId { get; set; }
    public string RoomName { get; set; } = "";
    public DateTimeOffset CreatedAt { get; set; }
    public string Status { get; set; } = "queued";
    public DateTimeOffset? CompletedAt { get; set; }
    public List<NotificationDelivery> Deliveries { get; set; } = [];
}

class NotificationDelivery
{
    public Guid Id { get; set; }
    public Guid AuditId { get; set; }
    public Guid UserId { get; set; }
    public string Recipient { get; set; } = "";
    public string Status { get; set; } = "queued";
    public int Attempts { get; set; }
    public DateTimeOffset? LastAttemptAt { get; set; }
    public DateTimeOffset? DeliveredAt { get; set; }
    public string? LastError { get; set; }
    public NotificationAudit Audit { get; set; } = null!;
}

class NotificationDbContext(DbContextOptions<NotificationDbContext> options) : DbContext(options)
{
    public DbSet<NotificationAudit> Audits => Set<NotificationAudit>();
    public DbSet<NotificationDelivery> Deliveries => Set<NotificationDelivery>();
    protected override void OnModelCreating(ModelBuilder b)
    {
        b.Entity<NotificationAudit>().ToTable("notification_audit").HasKey(x => x.Id);
        b.Entity<NotificationAudit>().HasIndex(x => x.MessageId).IsUnique();
        b.Entity<NotificationAudit>().Property(x => x.Status).HasMaxLength(40);
        b.Entity<NotificationAudit>().Property(x => x.RoomName).HasMaxLength(100);
        b.Entity<NotificationDelivery>().ToTable("notification_delivery").HasKey(x => x.Id);
        b.Entity<NotificationDelivery>().HasIndex(x => new { x.AuditId, x.UserId }).IsUnique();
        b.Entity<NotificationDelivery>().Property(x => x.Recipient).HasMaxLength(255);
        b.Entity<NotificationDelivery>().Property(x => x.Status).HasMaxLength(40);
        b.Entity<NotificationDelivery>().Property(x => x.LastError).HasMaxLength(500);
        b.Entity<NotificationDelivery>().HasOne(x => x.Audit).WithMany(x => x.Deliveries)
            .HasForeignKey(x => x.AuditId).OnDelete(DeleteBehavior.Cascade);
    }
}

class MessageNotificationConsumer(
    NotificationDbContext db,
    RoomGrpc.RoomGrpcClient roomClient,
    FamilyGraphGrpc.FamilyGraphGrpcClient familyClient,
    INotificationSender sender,
    NotificationOptions options,
    ILogger<MessageNotificationConsumer> logger) : IConsumer<MessageCreatedEvent>
{
    public async Task Consume(ConsumeContext<MessageCreatedEvent> context)
    {
        var e = context.Message;
        await using var messageLock = await PostgresAdvisoryLock.TryAcquireAsync(
            db.Database.GetDbConnection(),
            BitConverter.ToInt64(e.MessageId.ToByteArray()),
            context.CancellationToken)
            ?? throw new InvalidOperationException(
                $"Notification message {e.MessageId} is already being processed.");
        var audit = await GetOrCreateAudit(e, context.CancellationToken);
        if (audit.CompletedAt is not null) return;
        if (!options.Enabled)
        {
            audit.Status = NotificationStatuses.ProviderDisabled;
            audit.CompletedAt = DateTimeOffset.UtcNow;
            await db.SaveChangesAsync(context.CancellationToken);
            return;
        }

        if (audit.Deliveries.Count == 0)
            await CreateDeliveries(audit, e, context.CancellationToken);
        if (audit.Deliveries.Count == 0)
        {
            audit.Status = NotificationStatuses.NoRecipients;
            audit.CompletedAt = DateTimeOffset.UtcNow;
            await db.SaveChangesAsync(context.CancellationToken);
            return;
        }

        Exception? failure = null;
        foreach (var delivery in audit.Deliveries.Where(x => x.Status != NotificationStatuses.Delivered))
        {
            delivery.Attempts++;
            delivery.LastAttemptAt = DateTimeOffset.UtcNow;
            try
            {
                await sender.SendAsync(new NotificationEnvelope(
                    e.MessageId, delivery.Recipient, audit.RoomName, e.Content),
                    context.CancellationToken);
                delivery.Status = NotificationStatuses.Delivered;
                delivery.DeliveredAt = DateTimeOffset.UtcNow;
                delivery.LastError = null;
                delivery.Recipient = "";
                FamilyChatMetrics.NotificationDelivered.Add(1);
            }
            catch (Exception ex) when (ex is not OperationCanceledException)
            {
                delivery.Status = NotificationStatuses.Failed;
                delivery.LastError = NotificationFailure.SafeMessage(ex);
                failure ??= ex;
                FamilyChatMetrics.NotificationFailed.Add(1);
                logger.LogWarning(ex, "Notification delivery failed for message {MessageId} recipient {UserId}",
                    e.MessageId, delivery.UserId);
            }
            await db.SaveChangesAsync(context.CancellationToken);
        }

        audit.Status = audit.Deliveries.All(x => x.Status == NotificationStatuses.Delivered)
            ? NotificationStatuses.Delivered
            : NotificationStatuses.Failed;
        audit.CompletedAt = audit.Status == NotificationStatuses.Delivered
            ? DateTimeOffset.UtcNow
            : null;
        await db.SaveChangesAsync(context.CancellationToken);
        if (failure is not null) throw failure;
    }

    async Task<NotificationAudit> GetOrCreateAudit(MessageCreatedEvent e, CancellationToken ct)
    {
        var existing = await db.Audits.Include(x => x.Deliveries)
            .FirstOrDefaultAsync(x => x.MessageId == e.MessageId, ct);
        if (existing is not null) return existing;

        var audit = new NotificationAudit
        {
            Id = Guid.NewGuid(), MessageId = e.MessageId, RoomId = e.RoomId,
            SenderId = e.SenderId, CreatedAt = DateTimeOffset.UtcNow,
            Status = NotificationStatuses.Queued
        };
        db.Audits.Add(audit);
        try
        {
            await db.SaveChangesAsync(ct);
            return audit;
        }
        catch (DbUpdateException ex) when (
            ex.InnerException is PostgresException
            {
                SqlState: PostgresErrorCodes.UniqueViolation,
                ConstraintName: "IX_notification_audit_MessageId"
            })
        {
            db.ChangeTracker.Clear();
            return await db.Audits.Include(x => x.Deliveries)
                .SingleAsync(x => x.MessageId == e.MessageId, ct);
        }
    }

    async Task CreateDeliveries(NotificationAudit audit, MessageCreatedEvent e, CancellationToken ct)
    {
        var room = await roomClient.GetRoomNotificationTargetsAsync(
            new GetRoomNotificationTargetsRequest { RoomId = e.RoomId.ToString() },
            deadline: DateTime.UtcNow.AddSeconds(3), cancellationToken: ct);
        if (!room.Found) return;
        audit.RoomName = room.RoomName;

        var request = new GetUsersByIdsRequest();
        request.UserIds.AddRange(room.UserIds);
        if (request.UserIds.Count == 0) return;
        var users = await familyClient.GetUsersByIdsAsync(request,
            deadline: DateTime.UtcNow.AddSeconds(3), cancellationToken: ct);
        foreach (var recipient in NotificationRecipients.Valid(users.Users, e.SenderId))
        {
            audit.Deliveries.Add(new NotificationDelivery
            {
                Id = Guid.NewGuid(), AuditId = audit.Id, UserId = recipient.UserId,
                Recipient = recipient.Email, Status = NotificationStatuses.Queued
            });
        }
        await db.SaveChangesAsync(ct);
    }
}
