using Microsoft.EntityFrameworkCore;
using Npgsql;
using Testcontainers.PostgreSql;
using FamilyChat.ServiceDefaults;
using FamilyChat.Contracts.Events;
using Microsoft.IdentityModel.Tokens;
using System.Text;

namespace Persistence.IntegrationTests;

public sealed class IntegrationFactAttribute : FactAttribute
{
    public IntegrationFactAttribute()
    {
        if (Environment.GetEnvironmentVariable("RUN_INTEGRATION_TESTS") != "1")
            Skip = "Set RUN_INTEGRATION_TESTS=1 to run Docker-backed integration tests.";
    }
}

public sealed class PostgresFixture : IAsyncLifetime
{
    private PostgreSqlContainer? _postgres;

    public bool Enabled => Environment.GetEnvironmentVariable("RUN_INTEGRATION_TESTS") == "1";

    public async Task InitializeAsync()
    {
        if (!Enabled) return;
        _postgres = new PostgreSqlBuilder("postgres:16-alpine")
            .WithDatabase("postgres")
            .WithUsername("postgres")
            .WithPassword("integration-tests-only")
            .Build();
        await _postgres.StartAsync();
        await using var connection = new NpgsqlConnection(_postgres.GetConnectionString());
        await connection.OpenAsync();
        foreach (var database in Databases)
        {
            await using var command = connection.CreateCommand();
            command.CommandText = $"CREATE DATABASE {database}";
            await command.ExecuteNonQueryAsync();
        }
    }

    public async Task DisposeAsync()
    {
        if (_postgres is not null) await _postgres.DisposeAsync();
    }

    public string ConnectionString(string database)
    {
        var builder = new NpgsqlConnectionStringBuilder(
            (_postgres ?? throw new InvalidOperationException("PostgreSQL fixture is not running."))
            .GetConnectionString())
        {
            Database = database
        };
        return builder.ConnectionString;
    }

    private static readonly string[] Databases =
        ["identity_test", "family_test", "room_test", "message_test", "notification_test"];
}

public sealed class PostgresMigrationTests(PostgresFixture fixture) : IClassFixture<PostgresFixture>
{
    [IntegrationFact]
    public async Task AllServiceMigrations_ApplyToIsolatedPostgresDatabases()
    {
        await using var identity = new IdentityDbContext(Options<IdentityDbContext>("identity_test"));
        await identity.Database.MigrateAsync();
        Assert.Empty(await identity.Database.GetPendingMigrationsAsync());
        Assert.Equal(0, await identity.Users.CountAsync());
        Assert.Equal(0, await identity.RefreshTokens.CountAsync());
        Assert.Equal(0, await identity.OutboxMessages.CountAsync());

        var userId = Guid.NewGuid();
        var familyId = Guid.NewGuid();
        const string tokenHash = "concurrent-refresh-token-hash";
        identity.Users.Add(new AppUser
        {
            Id = userId,
            Username = "refresh-race-user",
            Email = "refresh-race@example.test",
            PasswordHash = "not-used-by-this-test",
            CreatedAt = DateTimeOffset.UtcNow
        });
        identity.RefreshTokens.Add(new RefreshToken
        {
            Id = Guid.NewGuid(),
            UserId = userId,
            FamilyId = familyId,
            TokenHash = tokenHash,
            ExpiresAt = DateTimeOffset.UtcNow.AddDays(1)
        });
        await identity.SaveChangesAsync();

        var jwt = new JwtOptions(
            new SymmetricSecurityKey(Encoding.UTF8.GetBytes(new string('x', 64))),
            "integration-tests", "integration-tests", 15, 30);
        await using var firstRotationDb = new IdentityDbContext(
            Options<IdentityDbContext>("identity_test"));
        await using var secondRotationDb = new IdentityDbContext(
            Options<IdentityDbContext>("identity_test"));
        var rotations = await Task.WhenAll(
            RefreshTokenStore.RotateAsync(firstRotationDb, tokenHash, jwt, CancellationToken.None),
            RefreshTokenStore.RotateAsync(secondRotationDb, tokenHash, jwt, CancellationToken.None));
        Assert.Contains(rotations, result => result.Status == RefreshRotationStatus.Success);
        Assert.Contains(rotations, result => result.Status == RefreshRotationStatus.ReplayDetected);

        await using var verificationDb = new IdentityDbContext(
            Options<IdentityDbContext>("identity_test"));
        Assert.Equal(0, await verificationDb.RefreshTokens.CountAsync(
            token => token.FamilyId == familyId && token.RevokedAt == null));

        var logoutFamilyId = Guid.NewGuid();
        const string logoutTokenHash = "logout-refresh-token-hash";
        verificationDb.RefreshTokens.AddRange(
            new RefreshToken
            {
                Id = Guid.NewGuid(), UserId = userId, FamilyId = logoutFamilyId,
                TokenHash = logoutTokenHash, ExpiresAt = DateTimeOffset.UtcNow.AddDays(1)
            },
            new RefreshToken
            {
                Id = Guid.NewGuid(), UserId = userId, FamilyId = logoutFamilyId,
                TokenHash = "logout-rotated-token-hash", ExpiresAt = DateTimeOffset.UtcNow.AddDays(1)
            });
        await verificationDb.SaveChangesAsync();

        Assert.True(await RefreshTokenStore.RevokeFamilyAsync(
            verificationDb, logoutTokenHash, CancellationToken.None));
        Assert.False(await RefreshTokenStore.RevokeFamilyAsync(
            verificationDb, "unknown-token-hash", CancellationToken.None));
        Assert.Equal(0, await verificationDb.RefreshTokens.CountAsync(
            token => token.FamilyId == logoutFamilyId && token.RevokedAt == null));

        var deletionEvent = await AccountDeletion.DeleteAsync(
            verificationDb,
            await verificationDb.Users.SingleAsync(user => user.Id == userId),
            CancellationToken.None);
        verificationDb.ChangeTracker.Clear();
        Assert.False(await verificationDb.Users.AnyAsync(user => user.Id == userId));
        var deletionOutbox = await verificationDb.OutboxMessages.SingleAsync(
            message => message.Id == deletionEvent.CorrelationId);
        Assert.Equal(nameof(UserDeletedEvent), deletionOutbox.Type);
        Assert.Equal(deletionEvent, Assert.IsType<UserDeletedEvent>(
            IdentityOutbox.Deserialize(deletionOutbox)));

        await using var family = new FamilyDbContext(Options<FamilyDbContext>("family_test"));
        await family.Database.MigrateAsync();
        Assert.Empty(await family.Database.GetPendingMigrationsAsync());
        Assert.Equal(0, await family.Users.CountAsync());
        Assert.Equal(0, await family.Families.CountAsync());
        Assert.Equal(0, await family.DeletedUsers.CountAsync());

        var deletedFamilyUserId = Guid.NewGuid();
        var successorId = Guid.NewGuid();
        var otherMemberId = Guid.NewGuid();
        var sharedFamilyId = Guid.NewGuid();
        var emptyFamilyId = Guid.NewGuid();
        var joinedAt = DateTimeOffset.UtcNow.AddDays(-2);
        family.Users.AddRange(
            FamilyUser(deletedFamilyUserId, "DELETED2", "deleted"),
            FamilyUser(successorId, "SUCCESS2", "successor"),
            FamilyUser(otherMemberId, "OTHERM22", "other"));
        family.Families.AddRange(
            new FamilyEntity
            {
                Id = sharedFamilyId, Name = "Shared", CreatedAt = joinedAt,
                Members =
                [
                    FamilyMember(sharedFamilyId, deletedFamilyUserId, "Head", deletedFamilyUserId, joinedAt),
                    FamilyMember(sharedFamilyId, successorId, "Member", deletedFamilyUserId, joinedAt.AddHours(1)),
                    FamilyMember(sharedFamilyId, otherMemberId, "Member", deletedFamilyUserId, joinedAt.AddHours(2))
                ]
            },
            new FamilyEntity
            {
                Id = emptyFamilyId, Name = "Only deleted user", CreatedAt = joinedAt,
                Members =
                [
                    FamilyMember(emptyFamilyId, deletedFamilyUserId, "Head", deletedFamilyUserId, joinedAt)
                ]
            });
        await family.SaveChangesAsync();

        var deletionTime = DateTimeOffset.UtcNow;
        Assert.True(await FamilyUserDeletion.ApplyAsync(
            family, deletedFamilyUserId, deletionTime, CancellationToken.None));
        Assert.False(await FamilyUserDeletion.ApplyAsync(
            family, deletedFamilyUserId, deletionTime, CancellationToken.None));
        family.ChangeTracker.Clear();
        Assert.False(await family.Users.AnyAsync(user => user.Id == deletedFamilyUserId));
        Assert.False(await family.Families.AnyAsync(item => item.Id == emptyFamilyId));
        Assert.Equal("Head", (await family.FamilyMembers.SingleAsync(
            member => member.FamilyId == sharedFamilyId && member.UserId == successorId)).Role);
        Assert.All(await family.FamilyMembers.Where(member => member.FamilyId == sharedFamilyId).ToListAsync(),
            member => Assert.Equal(Guid.Empty, member.AddedById));
        Assert.Equal(deletionTime, (await family.DeletedUsers.SingleAsync(
            deleted => deleted.UserId == deletedFamilyUserId)).DeletedAt);

        var lateRegistration = await UserProjectionRegistration.ApplyAsync(
            family,
            new UserRegisteredEvent(Guid.NewGuid(), deletedFamilyUserId,
                "resurrected", "resurrected@example.test", deletionTime.AddMinutes(-1)),
            CancellationToken.None);
        Assert.Equal(ProjectionRegistrationResult.Deleted, lateRegistration);
        Assert.False(await family.Users.AnyAsync(user => user.Id == deletedFamilyUserId));

        await using var room = new RoomDbContext(Options<RoomDbContext>("room_test"));
        await room.Database.MigrateAsync();
        Assert.Empty(await room.Database.GetPendingMigrationsAsync());
        Assert.Equal(0, await room.Rooms.CountAsync());
        Assert.Equal(0, await room.OutboxMessages.CountAsync());

        await using var message = new MessageDbContext(Options<MessageDbContext>("message_test"));
        await message.Database.MigrateAsync();
        Assert.Empty(await message.Database.GetPendingMigrationsAsync());
        Assert.Equal(0, await message.Messages.CountAsync());
        Assert.Equal(0, await message.OutboxMessages.CountAsync());
        var historyIndex = Assert.Single(message.Model.FindEntityType(typeof(MessageEntity))!
            .GetIndexes(), index => index.Properties
                .Select(property => property.Name)
                .SequenceEqual([nameof(MessageEntity.RoomId), nameof(MessageEntity.SentAt), nameof(MessageEntity.Id)]));
        Assert.False(historyIndex.IsUnique);

        var duplicateEvent = new MessageCreatedEvent(
            Guid.NewGuid(), Guid.NewGuid(), Guid.NewGuid(), "duplicate delivery",
            DateTimeOffset.UtcNow, Guid.NewGuid());
        await using var firstMessageDb = new MessageDbContext(
            Options<MessageDbContext>("message_test"));
        await using var secondMessageDb = new MessageDbContext(
            Options<MessageDbContext>("message_test"));
        var persistenceResults = await Task.WhenAll(
            MessagePersistence.TryPersistAsync(firstMessageDb, duplicateEvent, CancellationToken.None),
            MessagePersistence.TryPersistAsync(secondMessageDb, duplicateEvent, CancellationToken.None));
        Assert.Single(persistenceResults, inserted => inserted);
        Assert.Single(persistenceResults, inserted => !inserted);

        await using var messageVerificationDb = new MessageDbContext(
            Options<MessageDbContext>("message_test"));
        Assert.Equal(1, await messageVerificationDb.Messages.CountAsync(
            persisted => persisted.Id == duplicateEvent.MessageId));
        Assert.Equal(1, await messageVerificationDb.OutboxMessages.CountAsync());

        var expiredMessageId = Guid.NewGuid();
        var retainedMessageId = Guid.NewGuid();
        messageVerificationDb.Messages.AddRange(
            new MessageEntity
            {
                Id = expiredMessageId,
                RoomId = Guid.NewGuid(),
                SenderId = Guid.NewGuid(),
                Content = "expired",
                SentAt = DateTimeOffset.UtcNow.AddDays(-366)
            },
            new MessageEntity
            {
                Id = retainedMessageId,
                RoomId = Guid.NewGuid(),
                SenderId = Guid.NewGuid(),
                Content = "retained",
                SentAt = DateTimeOffset.UtcNow.AddDays(-364)
            });
        await messageVerificationDb.SaveChangesAsync();

        var deleted = await MessageRetention.DeleteBatchAsync(
            messageVerificationDb, DateTimeOffset.UtcNow.AddDays(-365), 100,
            CancellationToken.None);
        Assert.Equal(1, deleted);
        Assert.False(await messageVerificationDb.Messages.AnyAsync(m => m.Id == expiredMessageId));
        Assert.True(await messageVerificationDb.Messages.AnyAsync(m => m.Id == retainedMessageId));

        await using var notification = new NotificationDbContext(
            Options<NotificationDbContext>("notification_test"));
        await notification.Database.MigrateAsync();
        Assert.Empty(await notification.Database.GetPendingMigrationsAsync());
        Assert.Equal(0, await notification.Audits.CountAsync());
        Assert.Equal(0, await notification.Deliveries.CountAsync());
        Assert.Equal(0, await notification.Contacts.CountAsync());

        await using var firstConnection = new NpgsqlConnection(fixture.ConnectionString("identity_test"));
        await using var secondConnection = new NpgsqlConnection(fixture.ConnectionString("identity_test"));
        await using (var first = await PostgresAdvisoryLock.TryAcquireAsync(
            firstConnection, 123456789, CancellationToken.None))
        {
            Assert.NotNull(first);
            Assert.Null(await PostgresAdvisoryLock.TryAcquireAsync(
                secondConnection, 123456789, CancellationToken.None));
        }
        await using var acquiredAfterRelease = await PostgresAdvisoryLock.TryAcquireAsync(
            secondConnection, 123456789, CancellationToken.None);
        Assert.NotNull(acquiredAfterRelease);
    }

    private DbContextOptions<TContext> Options<TContext>(string database)
        where TContext : DbContext => new DbContextOptionsBuilder<TContext>()
            .UseNpgsql(fixture.ConnectionString(database))
            .Options;

    private static UserProjection FamilyUser(Guid id, string publicId, string username) => new()
    {
        Id = id,
        PublicId = publicId,
        Username = username,
        Email = $"{username}@example.test",
        CreatedAt = DateTimeOffset.UtcNow.AddDays(-2)
    };

    private static FamilyMember FamilyMember(
        Guid familyId, Guid userId, string role, Guid addedById, DateTimeOffset joinedAt) => new()
    {
        Id = Guid.NewGuid(),
        FamilyId = familyId,
        UserId = userId,
        Role = role,
        AddedById = addedById,
        JoinedAt = joinedAt
    };
}
