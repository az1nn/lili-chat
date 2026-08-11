using FamilyChat.Contracts.Events;
using Microsoft.Data.Sqlite;
using Microsoft.EntityFrameworkCore;

namespace Message.API.Tests;

public sealed class MessageUserDeletionTests : IAsyncLifetime
{
    readonly SqliteConnection connection = new("Data Source=:memory:");
    MessageDbContext db = null!;

    public async Task InitializeAsync()
    {
        await connection.OpenAsync();
        db = new MessageDbContext(new DbContextOptionsBuilder<MessageDbContext>()
            .UseSqlite(connection)
            .Options);
        await db.Database.EnsureCreatedAsync();
    }

    public async Task DisposeAsync()
    {
        await db.DisposeAsync();
        await connection.DisposeAsync();
    }

    [Fact]
    public async Task Delete_RemovesOnlyAuthoredContentAndCreatesTombstone()
    {
        var deletedUserId = Guid.NewGuid();
        var retainedUserId = Guid.NewGuid();
        var deletedAt = DateTimeOffset.UtcNow;
        db.Messages.AddRange(
            Message(deletedUserId, "private one"),
            Message(deletedUserId, "private two"),
            Message(retainedUserId, "retained"));
        await db.SaveChangesAsync();

        var result = await MessageUserDeletion.ApplyAsync(
            db, deletedUserId, deletedAt, CancellationToken.None);

        Assert.True(result.Applied);
        Assert.Equal(2, result.DeletedMessages);
        Assert.False(await db.Messages.AnyAsync(message => message.SenderId == deletedUserId));
        Assert.Equal("retained", (await db.Messages.SingleAsync()).Content);
        var tombstone = await db.DeletedUsers.SingleAsync();
        Assert.Equal(deletedUserId, tombstone.UserId);
        Assert.Equal(deletedAt, tombstone.DeletedAt);
    }

    [Fact]
    public async Task Delete_ReplayIsIdempotent()
    {
        var userId = Guid.NewGuid();
        var deletedAt = DateTimeOffset.UtcNow;

        var first = await MessageUserDeletion.ApplyAsync(
            db, userId, deletedAt, CancellationToken.None);
        var replay = await MessageUserDeletion.ApplyAsync(
            db, userId, deletedAt.AddMinutes(1), CancellationToken.None);

        Assert.True(first.Applied);
        Assert.False(replay.Applied);
        Assert.Equal(0, replay.DeletedMessages);
        Assert.Equal(1, await db.DeletedUsers.CountAsync());
        Assert.Equal(deletedAt, (await db.DeletedUsers.SingleAsync()).DeletedAt);
    }

    [Fact]
    public async Task Persist_AfterDeletionDoesNotRecreateContentOrOutbox()
    {
        var userId = Guid.NewGuid();
        await MessageUserDeletion.ApplyAsync(
            db, userId, DateTimeOffset.UtcNow, CancellationToken.None);
        var message = new MessageCreatedEvent(
            Guid.NewGuid(), Guid.NewGuid(), userId, "must not survive",
            DateTimeOffset.UtcNow, Guid.NewGuid());

        var inserted = await MessagePersistence.TryPersistAsync(
            db, message, CancellationToken.None);

        Assert.False(inserted);
        Assert.Empty(await db.Messages.ToListAsync());
        Assert.Empty(await db.OutboxMessages.ToListAsync());
    }

    [Fact]
    public void Model_IndexesSenderForErasureQueries()
    {
        var entity = db.Model.FindEntityType(typeof(MessageEntity));
        Assert.Contains(entity!.GetIndexes(), index =>
            index.Properties.Select(property => property.Name)
                .SequenceEqual([nameof(MessageEntity.SenderId)]));
    }

    static MessageEntity Message(Guid senderId, string content) => new()
    {
        Id = Guid.NewGuid(),
        RoomId = Guid.NewGuid(),
        SenderId = senderId,
        Content = content,
        SentAt = DateTimeOffset.UtcNow
    };
}
