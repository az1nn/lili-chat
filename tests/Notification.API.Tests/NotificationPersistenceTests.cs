using Microsoft.EntityFrameworkCore;
using Xunit;

namespace Notification.API.Tests;

public class NotificationPersistenceTests
{
    [Fact]
    public void ExplicitlyAddedDelivery_IsTrackedAsAdded()
    {
        var options = new DbContextOptionsBuilder<NotificationDbContext>()
            .UseNpgsql("Host=localhost;Database=notification;Username=app;Password=test")
            .Options;
        using var db = new NotificationDbContext(options);

        var audit = new NotificationAudit
        {
            Id = Guid.NewGuid(),
            MessageId = Guid.NewGuid(),
            RoomId = Guid.NewGuid(),
            SenderId = Guid.NewGuid(),
            CreatedAt = DateTimeOffset.UtcNow,
            Status = NotificationStatuses.CreatingDeliveries
        };
        db.Attach(audit);

        var delivery = new NotificationDelivery
        {
            Id = Guid.NewGuid(),
            AuditId = audit.Id,
            UserId = Guid.NewGuid(),
            Recipient = "recipient@example.test",
            Status = NotificationStatuses.Queued
        };

        audit.Deliveries.Add(delivery);
        db.Deliveries.Add(delivery);

        Assert.Equal(EntityState.Added, db.Entry(delivery).State);
        Assert.Same(audit, delivery.Audit);
    }
}
