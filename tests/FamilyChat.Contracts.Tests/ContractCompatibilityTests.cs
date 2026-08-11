using System.Text.Json;
using FamilyChat.Contracts.Events;
using FamilyChat.Contracts.Family;
using FamilyChat.Contracts.Room;
using Google.Protobuf.Reflection;

namespace FamilyChat.Contracts.Tests;

public class ContractCompatibilityTests
{
    [Fact]
    public void UserInfoResponse_PreservesPublishedFieldNumbers()
    {
        AssertFields(UserInfoResponse.Descriptor,
            ("found", 1), ("user_id", 2), ("public_id", 3),
            ("username", 4), ("email", 5), ("avatar_url", 6));
    }

    [Fact]
    public void IsMemberResponse_PreservesAuthorizationFieldNumbers()
    {
        AssertFields(IsMemberResponse.Descriptor,
            ("is_member", 1), ("role", 2), ("can_send_messages", 3));
    }

    [Fact]
    public void RoomSummary_PreservesPublishedFieldNumbers()
    {
        AssertFields(RoomSummary.Descriptor,
            ("id", 1), ("name", 2), ("members_count", 3));
    }

    [Theory]
    [MemberData(nameof(Events))]
    public void SharedEvent_RoundTripsWithSystemTextJson(object integrationEvent)
    {
        var json = JsonSerializer.Serialize(integrationEvent, integrationEvent.GetType());
        var restored = JsonSerializer.Deserialize(json, integrationEvent.GetType());

        Assert.Equal(integrationEvent, restored);
    }

    [Theory]
    [MemberData(nameof(EventShapes))]
    public void SharedEvent_PreservesPublishedJsonProperties(
        object integrationEvent, string[] requiredProperties)
    {
        using var document = JsonDocument.Parse(
            JsonSerializer.Serialize(integrationEvent, integrationEvent.GetType()));
        var actual = document.RootElement.EnumerateObject()
            .Select(property => property.Name).ToHashSet(StringComparer.Ordinal);

        Assert.All(requiredProperties, property => Assert.Contains(property, actual));
    }

    public static TheoryData<object> Events => new()
    {
        new UserRegisteredEvent(Guid.NewGuid(), Guid.NewGuid(), "alice", "alice@example.test", DateTimeOffset.UtcNow),
        new RoomCreatedEvent(Guid.NewGuid(), Guid.NewGuid(), "Família", DateTimeOffset.UtcNow),
        new RoomMemberAddedEvent(Guid.NewGuid(), Guid.NewGuid(), Guid.NewGuid(), "Member", DateTimeOffset.UtcNow),
        new RoomMemberRemovedEvent(Guid.NewGuid(), Guid.NewGuid(), Guid.NewGuid(), "removed", DateTimeOffset.UtcNow),
        new MessageCreatedEvent(Guid.NewGuid(), Guid.NewGuid(), Guid.NewGuid(), "olá", DateTimeOffset.UtcNow, Guid.NewGuid()),
        new MessagePersistedEvent(Guid.NewGuid(), Guid.NewGuid(), DateTimeOffset.UtcNow, Guid.NewGuid())
    };

    public static TheoryData<object, string[]> EventShapes => new()
    {
        { new UserRegisteredEvent(Guid.NewGuid(), Guid.NewGuid(), "alice", "alice@example.test", DateTimeOffset.UtcNow), ["CorrelationId", "UserId", "Username", "Email", "OccurredAt"] },
        { new RoomCreatedEvent(Guid.NewGuid(), Guid.NewGuid(), "Família", DateTimeOffset.UtcNow), ["RoomId", "OwnerId", "Name", "OccurredAt"] },
        { new RoomMemberAddedEvent(Guid.NewGuid(), Guid.NewGuid(), Guid.NewGuid(), "Member", DateTimeOffset.UtcNow), ["RoomId", "UserId", "AddedById", "Role", "OccurredAt"] },
        { new RoomMemberRemovedEvent(Guid.NewGuid(), Guid.NewGuid(), Guid.NewGuid(), "removed", DateTimeOffset.UtcNow), ["RoomId", "UserId", "RemovedById", "Reason", "OccurredAt"] },
        { new MessageCreatedEvent(Guid.NewGuid(), Guid.NewGuid(), Guid.NewGuid(), "olá", DateTimeOffset.UtcNow, Guid.NewGuid()), ["MessageId", "RoomId", "SenderId", "Content", "SentAt", "CorrelationId"] },
        { new MessagePersistedEvent(Guid.NewGuid(), Guid.NewGuid(), DateTimeOffset.UtcNow, Guid.NewGuid()), ["MessageId", "RoomId", "PersistedAt", "CorrelationId"] }
    };

    static void AssertFields(MessageDescriptor descriptor, params (string Name, int Number)[] expected)
    {
        foreach (var (name, number) in expected)
        {
            var field = descriptor.FindFieldByNumber(number);
            Assert.NotNull(field);
            Assert.Equal(name, field.Name);
        }
    }
}
