using System.Security.Claims;
using System.Text;
using FamilyChat.Contracts.Events;
using FamilyChat.Contracts.Room;
using FamilyChat.ServiceDefaults;
using Grpc.Core;
using MassTransit;
using Microsoft.AspNetCore.Authentication.JwtBearer;
using Microsoft.AspNetCore.Authorization;
using Microsoft.AspNetCore.SignalR;
using Microsoft.IdentityModel.Tokens;
using StackExchange.Redis;

var builder = WebApplication.CreateBuilder(args);
builder.Logging.AddFilter("Microsoft.AspNetCore.Hosting.Diagnostics", LogLevel.Warning);
builder.AddFamilyChatObservability("realtime-hub");

var redis = builder.Configuration["Redis:Connection"] ?? "redis:6379";
builder.Services.AddSingleton<IConnectionMultiplexer>(_ =>
    ConnectionMultiplexer.Connect(redis));

builder.Services.AddSignalR(o =>
{
    o.MaximumReceiveMessageSize = 64 * 1024;
    o.KeepAliveInterval = TimeSpan.FromSeconds(15);
    o.ClientTimeoutInterval = TimeSpan.FromSeconds(30);
}).AddStackExchangeRedis(redis);

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
        o.Events = new JwtBearerEvents
        {
            OnMessageReceived = context =>
            {
                var accessToken = context.Request.Query["access_token"];
                if (!string.IsNullOrEmpty(accessToken) &&
                    context.HttpContext.Request.Path.StartsWithSegments("/hubs/chat"))
                    context.Token = accessToken;
                return Task.CompletedTask;
            }
        };
    });
builder.Services.AddAuthorization();

builder.Services.AddMassTransit(x =>
{
    x.AddConsumer<MessagePersistedConsumer>();
    x.AddConsumer<RoomMemberRemovedConsumer>();
    x.AddConsumer<RoomMemberRoleChangedConsumer>();
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
app.UseAuthentication();
app.UseAuthorization();
app.MapHealthChecks("/health");
app.MapHub<ChatHub>("/hubs/chat");
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

[Authorize]
class ChatHub(
    RoomGrpc.RoomGrpcClient roomClient,
    IPublishEndpoint publish,
    IConnectionMultiplexer redis,
    ILogger<ChatHub> logger) : Hub
{
    string UserIdString =>
        Context.User?.FindFirstValue("sub")
        ?? Context.User?.FindFirstValue(ClaimTypes.NameIdentifier)
        ?? throw new HubException("Usuário não autenticado");

    Guid UserId => Guid.Parse(UserIdString);

    public override async Task OnConnectedAsync()
    {
        await base.OnConnectedAsync();
        FamilyChatMetrics.SignalRConnections.Add(1);
        FamilyChatMetrics.SignalRActiveConnections.Add(1);
    }

    public async Task<object> JoinRoom(Guid roomId)
    {
        IsMemberResponse permission;
        try
        {
            permission = await roomClient.IsMemberOfRoomAsync(new IsMemberRequest
            {
                RoomId = roomId.ToString(),
                UserId = UserIdString
            }, deadline: DateTime.UtcNow.AddSeconds(3),
                cancellationToken: Context.ConnectionAborted);
        }
        catch (RpcException ex) when (ex.StatusCode is StatusCode.Unavailable or StatusCode.DeadlineExceeded)
        {
            RecordGrpcFailure("join_room", ex.StatusCode);
            return new { success = false, error = "Autorização de sala indisponível." };
        }

        if (!permission.IsMember)
            return new { success = false, error = "Sem permissão para esta sala." };

        try
        {
            await Groups.AddToGroupAsync(Context.ConnectionId, roomId.ToString());
            var db = redis.GetDatabase();
            var presenceKey = $"familychat:room:{roomId}:connections";
            await db.HashSetAsync(presenceKey, Context.ConnectionId, UserIdString);

            var rooms = Context.Items.TryGetValue("rooms", out var current)
                ? (HashSet<Guid>)current!
                : new HashSet<Guid>();
            rooms.Add(roomId);
            Context.Items["rooms"] = rooms;

            var online = (await db.HashValuesAsync(presenceKey))
                .Select(v => v.ToString()).Distinct().ToArray();

            await Clients.Group(roomId.ToString()).SendAsync(
                "PresenceUpdated", new { roomId, onlineUsers = online });

            return new { success = true, data = new { role = permission.Role, onlineUsers = online } };
        }
        catch (RedisException ex)
        {
            RecordRedisFailure("join_room", ex);
            Context.Abort();
            return new { success = false, error = "Realtime temporariamente indisponível." };
        }
    }

    public async Task LeaveRoom(Guid roomId)
    {
        try
        {
            await Groups.RemoveFromGroupAsync(Context.ConnectionId, roomId.ToString());
            var db = redis.GetDatabase();
            var presenceKey = $"familychat:room:{roomId}:connections";
            await db.HashDeleteAsync(presenceKey, Context.ConnectionId);
            if (Context.Items.TryGetValue("rooms", out var current))
                ((HashSet<Guid>)current!).Remove(roomId);
            var online = (await db.HashValuesAsync(presenceKey))
                .Select(v => v.ToString()).Distinct().ToArray();
            await Clients.Group(roomId.ToString()).SendAsync(
                "PresenceUpdated", new { roomId, onlineUsers = online });
        }
        catch (RedisException ex)
        {
            RecordRedisFailure("leave_room", ex);
            Context.Abort();
        }
    }

    public async Task<object> SendMessage(Guid roomId, string content, Guid? clientMessageId = null)
    {
        if (content is null || content.Length > 2000)
            return new { success = false, error = "Mensagem deve ter entre 1 e 2000 caracteres." };
        var text = content.Trim();
        if (text.Length < 1)
            return new { success = false, error = "Mensagem deve ter entre 1 e 2000 caracteres." };

        var redisDb = redis.GetDatabase();
        try
        {
            var bucket = DateTimeOffset.UtcNow.ToUnixTimeSeconds() / 10;
            var rateKey = $"familychat:ratelimit:send:{UserId}:{roomId}:{bucket}";
            var count = await redisDb.StringIncrementAsync(rateKey);
            if (count == 1) await redisDb.KeyExpireAsync(rateKey, TimeSpan.FromSeconds(15));
            if (count > 20)
                return new { success = false, error = "Muitas mensagens. Aguarde alguns segundos." };
        }
        catch (RedisException ex)
        {
            RecordRedisFailure("send_rate_limit", ex);
            return new { success = false, error = "Realtime temporariamente indisponível." };
        }

        IsMemberResponse permission;
        try
        {
            permission = await roomClient.IsMemberOfRoomAsync(new IsMemberRequest
            {
                RoomId = roomId.ToString(),
                UserId = UserIdString
            }, deadline: DateTime.UtcNow.AddSeconds(3),
                cancellationToken: Context.ConnectionAborted);
        }
        catch (RpcException ex) when (ex.StatusCode is StatusCode.Unavailable or StatusCode.DeadlineExceeded)
        {
            RecordGrpcFailure("send_message", ex.StatusCode);
            return new { success = false, error = "Autorização de sala indisponível." };
        }

        if (!permission.IsMember || !permission.CanSendMessages)
            return new { success = false, error = "Sem permissão para enviar." };

        var now = DateTimeOffset.UtcNow;
        var messageId = Guid.NewGuid();
        RedisKey? idempotencyKey = null;
        if (clientMessageId is not null)
        {
            idempotencyKey = $"familychat:message-client:{UserId}:{clientMessageId}";
            try
            {
                var reserved = await redisDb.StringSetAsync(
                    idempotencyKey.Value, messageId.ToString(), TimeSpan.FromDays(1), When.NotExists);
                if (!reserved)
                {
                    var existing = await redisDb.StringGetAsync(idempotencyKey.Value);
                    return new { success = true, data = new { messageId = existing.ToString(), duplicate = true } };
                }
            }
            catch (RedisException ex)
            {
                RecordRedisFailure("send_idempotency", ex);
                return new { success = false, error = "Realtime temporariamente indisponível." };
            }
        }

        try
        {
            await publish.Publish(new MessageCreatedEvent(
                messageId, roomId, UserId, text, now, Guid.NewGuid(),
                permission.RoomName, permission.NotificationUserIds.ToArray()),
                Context.ConnectionAborted);
            FamilyChatMetrics.MessagePublished.Add(1);
        }
        catch
        {
            if (idempotencyKey is not null)
            {
                try { await redisDb.KeyDeleteAsync(idempotencyKey.Value); }
                catch (RedisException ex) { RecordRedisFailure("send_rollback", ex); }
            }
            throw;
        }

        var dto = new
        {
            id = messageId,
            roomId,
            senderId = UserId,
            content = text,
            sentAt = now,
            status = "accepted",
            clientMessageId
        };
        await Clients.Group(roomId.ToString()).SendAsync("MessageReceived", dto);
        logger.LogInformation("Message published {MessageId} room {RoomId}", messageId, roomId);
        return new { success = true, data = new { messageId } };
    }

    public override async Task OnDisconnectedAsync(Exception? exception)
    {
        try
        {
            if (Context.Items.TryGetValue("rooms", out var current))
            {
                var db = redis.GetDatabase();
                foreach (var roomId in (HashSet<Guid>)current!)
                {
                    var presenceKey = $"familychat:room:{roomId}:connections";
                    await db.HashDeleteAsync(presenceKey, Context.ConnectionId);
                    var online = (await db.HashValuesAsync(presenceKey))
                        .Select(v => v.ToString()).Distinct().ToArray();
                    await Clients.Group(roomId.ToString()).SendAsync(
                        "PresenceUpdated", new { roomId, onlineUsers = online });
                }
            }
        }
        catch (RedisException ex)
        {
            RecordRedisFailure("disconnect", ex);
        }
        finally
        {
            FamilyChatMetrics.SignalRDisconnects.Add(1);
            FamilyChatMetrics.SignalRActiveConnections.Add(-1);
            await base.OnDisconnectedAsync(exception);
        }
    }

    void RecordRedisFailure(string operation, RedisException exception)
    {
        FamilyChatMetrics.RedisFailures.Add(1,
            new KeyValuePair<string, object?>("service", "realtime"),
            new KeyValuePair<string, object?>("operation", operation));
        logger.LogWarning(exception, "Redis unavailable during {Operation}", operation);
    }

    static void RecordGrpcFailure(string operation, StatusCode statusCode) =>
        FamilyChatMetrics.GrpcFailures.Add(1,
            new KeyValuePair<string, object?>("service", "realtime"),
            new KeyValuePair<string, object?>("operation", operation),
            new KeyValuePair<string, object?>("status", statusCode.ToString()));
}

class MessagePersistedConsumer(IHubContext<ChatHub> hub) : IConsumer<MessagePersistedEvent>
{
    public Task Consume(ConsumeContext<MessagePersistedEvent> context)
    {
        var message = context.Message;
        return hub.Clients.Group(message.RoomId.ToString()).SendAsync(
            "MessagePersisted",
            new
            {
                messageId = message.MessageId,
                roomId = message.RoomId,
                persistedAt = message.PersistedAt,
                status = "persisted"
            },
            context.CancellationToken);
    }
}

class RoomMemberRemovedConsumer(
    IHubContext<ChatHub> hub,
    IConnectionMultiplexer redis) : IConsumer<RoomMemberRemovedEvent>
{
    public async Task Consume(ConsumeContext<RoomMemberRemovedEvent> context)
    {
        var message = context.Message;
        var db = redis.GetDatabase();
        var presenceKey = $"familychat:room:{message.RoomId}:connections";
        var entries = await db.HashGetAllAsync(presenceKey);
        var connectionIds = entries
            .Where(entry => entry.Value == message.UserId.ToString())
            .Select(entry => entry.Name.ToString())
            .ToArray();

        foreach (var connectionId in connectionIds)
        {
            await hub.Clients.Client(connectionId).SendAsync(
                "RoomAccessRevoked",
                new { roomId = message.RoomId, reason = message.Reason },
                context.CancellationToken);
            await hub.Groups.RemoveFromGroupAsync(
                connectionId, message.RoomId.ToString(), context.CancellationToken);
            await db.HashDeleteAsync(presenceKey, connectionId);
        }

        var online = (await db.HashValuesAsync(presenceKey))
            .Select(value => value.ToString()).Distinct().ToArray();
        await hub.Clients.Group(message.RoomId.ToString()).SendAsync(
            "PresenceUpdated",
            new { roomId = message.RoomId, onlineUsers = online },
            context.CancellationToken);
    }
}

class RoomMemberRoleChangedConsumer(
    IHubContext<ChatHub> hub,
    IConnectionMultiplexer redis) : IConsumer<RoomMemberRoleChangedEvent>
{
    public async Task Consume(ConsumeContext<RoomMemberRoleChangedEvent> context)
    {
        var message = context.Message;
        var db = redis.GetDatabase();
        var entries = await db.HashGetAllAsync($"familychat:room:{message.RoomId}:connections");
        var connectionIds = entries
            .Where(entry => entry.Value == message.UserId.ToString())
            .Select(entry => entry.Name.ToString())
            .ToArray();

        foreach (var connectionId in connectionIds)
        {
            await hub.Clients.Client(connectionId).SendAsync(
                "RoomRoleChanged",
                new
                {
                    roomId = message.RoomId,
                    userId = message.UserId,
                    previousRole = message.PreviousRole,
                    role = message.Role
                },
                context.CancellationToken);
        }
    }
}
