using FamilyChat.Contracts.Events;
using FamilyChat.ServiceDefaults;
using MassTransit;
using Microsoft.EntityFrameworkCore;
using Npgsql;

var builder = WebApplication.CreateBuilder(args);
builder.AddFamilyChatObservability("notification-svc");

builder.Services.AddDbContext<NotificationDbContext>(o =>
    o.UseNpgsql(builder.Configuration.GetConnectionString("Default")));

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
    public DateTimeOffset CreatedAt { get; set; }
    public string Status { get; set; } = "queued";
}

class NotificationDbContext(DbContextOptions<NotificationDbContext> options) : DbContext(options)
{
    public DbSet<NotificationAudit> Audits => Set<NotificationAudit>();
    protected override void OnModelCreating(ModelBuilder b)
    {
        b.Entity<NotificationAudit>().ToTable("notification_audit").HasKey(x => x.Id);
        b.Entity<NotificationAudit>().HasIndex(x => x.MessageId).IsUnique();
    }
}

class MessageNotificationConsumer(
    NotificationDbContext db,
    ILogger<MessageNotificationConsumer> logger) : IConsumer<MessageCreatedEvent>
{
    public async Task Consume(ConsumeContext<MessageCreatedEvent> context)
    {
        var e = context.Message;
        if (await db.Audits.AnyAsync(x => x.MessageId == e.MessageId, context.CancellationToken))
            return;

        db.Audits.Add(new NotificationAudit
        {
            Id = Guid.NewGuid(),
            MessageId = e.MessageId,
            RoomId = e.RoomId,
            SenderId = e.SenderId,
            CreatedAt = DateTimeOffset.UtcNow,
            Status = "queued-provider-not-configured"
        });
        try
        {
            await db.SaveChangesAsync(context.CancellationToken);
        }
        catch (DbUpdateException ex) when (
            ex.InnerException is PostgresException
            {
                SqlState: PostgresErrorCodes.UniqueViolation,
                ConstraintName: "IX_notification_audit_MessageId"
            })
        {
            db.ChangeTracker.Clear();
            logger.LogInformation(
                "Duplicate notification event ignored for message {MessageId}", e.MessageId);
            return;
        }

        logger.LogInformation(
            "Notification event recorded for message {MessageId}. Configure FCM/APNs/email provider to deliver.",
            e.MessageId);
    }
}
