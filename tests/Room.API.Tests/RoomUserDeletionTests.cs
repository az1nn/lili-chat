using System.Text.Json;
using FamilyChat.Contracts.Events;
using Microsoft.EntityFrameworkCore;

namespace Room.API.Tests;

public class RoomUserDeletionTests
{
    [Fact]
    public async Task Apply_ArchivesOwnedRooms_RemovesOtherMemberships_AndIsIdempotent()
    {
        var options = new DbContextOptionsBuilder<RoomDbContext>()
            .UseInMemoryDatabase(Guid.NewGuid().ToString())
            .Options;
        await using var db = new RoomDbContext(options);
        var deletedUserId = Guid.NewGuid();
        var ownedRoom = Room(deletedUserId, deletedUserId);
        var joinedRoom = Room(Guid.NewGuid(), deletedUserId);
        db.Rooms.AddRange(ownedRoom, joinedRoom);
        await db.SaveChangesAsync();

        var deletion = new UserDeletedEvent(
            Guid.NewGuid(), deletedUserId, DateTimeOffset.UtcNow);

        Assert.True(await RoomUserDeletion.ApplyAsync(db, deletion, CancellationToken.None));
        Assert.False(await RoomUserDeletion.ApplyAsync(db, deletion, CancellationToken.None));

        Assert.Equal(deletion.OccurredAt, ownedRoom.ArchivedAt);
        Assert.Contains(ownedRoom.Members, member => member.UserId == deletedUserId);
        Assert.DoesNotContain(joinedRoom.Members, member => member.UserId == deletedUserId);
        Assert.Single(db.DeletedUsers, user => user.UserId == deletedUserId);
        Assert.Equal(2, await db.OutboxMessages.CountAsync());
        Assert.Single(db.OutboxMessages, message => message.Type == nameof(RoomArchivedEvent));
        var removal = Assert.Single(db.OutboxMessages,
            message => message.Type == nameof(RoomMemberRemovedEvent));
        var payload = JsonSerializer.Deserialize<RoomMemberRemovedEvent>(removal.Payload);
        Assert.NotNull(payload);
        Assert.Equal("account_deleted", payload.Reason);
        Assert.Equal(deletedUserId, payload.UserId);
    }

    static RoomEntity Room(Guid ownerId, Guid memberId)
    {
        var room = new RoomEntity
        {
            Id = Guid.NewGuid(),
            Name = "Sala",
            OwnerId = ownerId,
            CreatedAt = DateTimeOffset.UtcNow
        };
        room.Members.Add(new RoomMember
        {
            Id = Guid.NewGuid(),
            RoomId = room.Id,
            UserId = memberId,
            Role = ownerId == memberId ? "Admin" : "Member",
            AddedById = ownerId,
            JoinedAt = DateTimeOffset.UtcNow,
            Room = room
        });
        return room;
    }
}
