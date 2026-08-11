namespace FamilyChat.Contracts.Events;

public record UserRegisteredEvent(
    Guid CorrelationId,
    Guid UserId,
    string Username,
    string Email,
    DateTimeOffset OccurredAt);

public record UserDeletedEvent(
    Guid CorrelationId,
    Guid UserId,
    DateTimeOffset OccurredAt);

public record MessageCreatedEvent(
    Guid MessageId,
    Guid RoomId,
    Guid SenderId,
    string Content,
    DateTimeOffset SentAt,
    Guid CorrelationId,
    string? RoomName = null,
    string[]? NotificationUserIds = null);

public record MessagePersistedEvent(
    Guid MessageId,
    Guid RoomId,
    DateTimeOffset PersistedAt,
    Guid CorrelationId);

public record RoomCreatedEvent(
    Guid RoomId,
    Guid OwnerId,
    string Name,
    DateTimeOffset OccurredAt);

public record RoomMemberAddedEvent(
    Guid RoomId,
    Guid UserId,
    Guid AddedById,
    string Role,
    DateTimeOffset OccurredAt);

public record RoomMemberRemovedEvent(
    Guid RoomId,
    Guid UserId,
    Guid RemovedById,
    string Reason,
    DateTimeOffset OccurredAt);

public record RoomMemberRoleChangedEvent(
    Guid RoomId,
    Guid UserId,
    Guid ChangedById,
    string PreviousRole,
    string Role,
    DateTimeOffset OccurredAt);

public record RoomArchivedEvent(
    Guid RoomId,
    Guid ArchivedById,
    DateTimeOffset OccurredAt);
