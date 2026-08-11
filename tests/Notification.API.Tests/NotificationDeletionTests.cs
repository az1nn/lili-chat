using FamilyChat.Contracts.Events;
using Microsoft.EntityFrameworkCore;
using Xunit;

namespace Notification.API.Tests;

public sealed class NotificationDeletionTests
{
    [Fact]
    public void Deletion_CreatesPermanentTombstoneAndLateRegistrationCannotRestorePii()
    {
        using var db = Context();
        var userId = Guid.NewGuid();
        var deletedAt = DateTimeOffset.UtcNow;
        NotificationContactProjection.ApplyDeletion(db, null,
            new UserDeletedEvent(Guid.NewGuid(), userId, deletedAt));
        var tombstone = Assert.Single(db.Contacts.Local);

        NotificationContactProjection.ApplyRegistration(db, tombstone,
            new UserRegisteredEvent(Guid.NewGuid(), userId, "late", "late@example.test",
                deletedAt.AddMinutes(1)));

        Assert.Equal("", tombstone.Email);
        Assert.Equal(deletedAt, tombstone.DeletedAt);
        Assert.Equal(deletedAt, tombstone.UpdatedAt);
    }

    [Fact]
    public void DuplicateDeletion_IsIdempotentAndErasesExistingContact()
    {
        using var db = Context();
        var occurredAt = DateTimeOffset.UtcNow;
        var contact = new NotificationContact
        {
            UserId = Guid.NewGuid(), Email = "person@example.test",
            UpdatedAt = occurredAt.AddMinutes(-1)
        };
        var message = new UserDeletedEvent(Guid.NewGuid(), contact.UserId, occurredAt);

        NotificationContactProjection.ApplyDeletion(db, contact, message);
        NotificationContactProjection.ApplyDeletion(db, contact, message);

        Assert.Equal("", contact.Email);
        Assert.Equal(occurredAt, contact.DeletedAt);
        Assert.Equal(occurredAt, contact.UpdatedAt);
        Assert.Empty(db.Contacts.Local);
    }

    [Fact]
    public void RecipientSelection_ExcludesDeletedContacts()
    {
        var deleted = new NotificationContact
        {
            UserId = Guid.NewGuid(), Email = "", UpdatedAt = DateTimeOffset.UtcNow,
            DeletedAt = DateTimeOffset.UtcNow
        };
        var active = new NotificationContact
        {
            UserId = Guid.NewGuid(), Email = "active@example.test",
            UpdatedAt = DateTimeOffset.UtcNow
        };

        var recipient = Assert.Single(
            NotificationContactProjection.ActiveRecipients([deleted, active]));

        Assert.Equal(active.UserId, recipient.UserId);
        Assert.Equal(active.Email, recipient.Email);
    }

    [Theory]
    [InlineData("queued", "recipient-deleted")]
    [InlineData("failed", "recipient-deleted")]
    [InlineData("delivered", "delivered")]
    public void DeliveryErasure_RemovesPiiButPreservesLedgerIdentity(
        string originalStatus, string expectedStatus)
    {
        var id = Guid.NewGuid();
        var auditId = Guid.NewGuid();
        var userId = Guid.NewGuid();
        var delivery = new NotificationDelivery
        {
            Id = id, AuditId = auditId, UserId = userId,
            Recipient = "person@example.test", Status = originalStatus,
            Attempts = 2, LastError = "old failure"
        };

        NotificationPrivacy.EraseRecipient(delivery);
        NotificationPrivacy.EraseRecipient(delivery);

        Assert.Equal(id, delivery.Id);
        Assert.Equal(auditId, delivery.AuditId);
        Assert.Equal(userId, delivery.UserId);
        Assert.Equal(2, delivery.Attempts);
        Assert.Equal("", delivery.Recipient);
        Assert.Equal(expectedStatus, delivery.Status);
        Assert.Null(delivery.LastError);
        Assert.True(NotificationPrivacy.IsTerminal(delivery));
    }

    static NotificationDbContext Context()
    {
        var options = new DbContextOptionsBuilder<NotificationDbContext>()
            .UseNpgsql("Host=localhost;Database=not-used;Username=test;Password=test")
            .Options;
        return new NotificationDbContext(options);
    }
}
