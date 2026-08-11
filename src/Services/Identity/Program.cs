using System.IdentityModel.Tokens.Jwt;
using System.Security.Claims;
using System.Security.Cryptography;
using System.Net.Mail;
using System.Text;
using System.Text.Json;
using FamilyChat.Contracts.Events;
using FamilyChat.ServiceDefaults;
using MassTransit;
using Microsoft.AspNetCore.Authentication.JwtBearer;
using Microsoft.AspNetCore.Identity;
using Microsoft.EntityFrameworkCore;
using Microsoft.IdentityModel.Tokens;

var builder = WebApplication.CreateBuilder(args);
builder.AddFamilyChatObservability("identity-svc");

builder.Services.AddDbContext<IdentityDbContext>(o =>
    o.UseNpgsql(builder.Configuration.GetConnectionString("Default")));

builder.Services.AddSingleton<IPasswordHasher<AppUser>, PasswordHasher<AppUser>>();
builder.Services.AddHostedService<IdentityOutboxPublisher>();

var jwt = JwtOptions.From(builder.Configuration, builder.Environment);
builder.Services.AddSingleton(jwt);
builder.Services.AddAuthentication(JwtBearerDefaults.AuthenticationScheme)
    .AddJwtBearer(o =>
    {
        o.MapInboundClaims = false;
        o.TokenValidationParameters = jwt.ValidationParameters(validateLifetime: true);
    });
builder.Services.AddAuthorization();

builder.Services.AddMassTransit(x =>
{
    x.UsingRabbitMq((ctx, cfg) =>
    {
        var host = builder.Configuration["RabbitMQ:Host"] ?? "rabbitmq";
        cfg.Host(host, "/", h =>
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

using (var scope = app.Services.CreateScope())
{
    var db = scope.ServiceProvider.GetRequiredService<IdentityDbContext>();
    await db.Database.MigrateAsync();
}

app.MapHealthChecks("/health");

app.MapPost("/api/v1/auth/register", async (
    RegisterRequest req,
    HttpContext http,
    IdentityDbContext db,
    IPasswordHasher<AppUser> hasher,
    JwtOptions jwtOptions,
    CancellationToken ct) =>
{
    if (!IdentityInput.TryRegistration(
        req.Username, req.Email, req.Password, out var username, out var email))
        return Results.BadRequest(new
        {
            error = "Use username de 3–100 caracteres, email válido e senha de 8–128 caracteres."
        });

    if (await db.Users.AnyAsync(u => u.Email == email || u.Username == username, ct))
        return Results.Conflict(new { error = "Email ou username já cadastrado." });

    var user = new AppUser
    {
        Id = Guid.NewGuid(),
        Username = username,
        Email = email,
        CreatedAt = DateTimeOffset.UtcNow
    };
    user.PasswordHash = hasher.HashPassword(user, req.Password);
    db.Users.Add(user);

    var tokens = TokenFactory.CreatePair(user, jwtOptions);
    db.RefreshTokens.Add(new RefreshToken
    {
        Id = Guid.NewGuid(),
        UserId = user.Id,
        FamilyId = Guid.NewGuid(),
        TokenHash = TokenFactory.Hash(tokens.RefreshToken),
        ExpiresAt = tokens.RefreshExpiresAt
    });

    var registered = new UserRegisteredEvent(
        Guid.NewGuid(), user.Id, user.Username, user.Email, DateTimeOffset.UtcNow);
    db.OutboxMessages.Add(OutboxMessage.Create(registered.CorrelationId, nameof(UserRegisteredEvent), registered));
    await db.SaveChangesAsync(ct);

    SetRefreshCookie(http.Response, tokens, app.Environment.IsDevelopment());
    return Results.Created("/api/v1/users/me", AuthResponse.From(user, tokens));
});

app.MapPost("/api/v1/auth/login", async (
    LoginRequest req,
    HttpContext http,
    IdentityDbContext db,
    IPasswordHasher<AppUser> hasher,
    JwtOptions jwtOptions,
    CancellationToken ct) =>
{
    if (!IdentityInput.TryLogin(req.Email, req.Password, out var email))
    {
        FamilyChatMetrics.LoginFailed.Add(1);
        return Results.Unauthorized();
    }
    var user = await db.Users.FirstOrDefaultAsync(u => u.Email == email, ct);
    if (user is null)
    {
        FamilyChatMetrics.LoginFailed.Add(1);
        return Results.Unauthorized();
    }

    if (user.LockoutEnd is { } lockout && lockout > DateTimeOffset.UtcNow)
    {
        FamilyChatMetrics.LoginFailed.Add(1);
        return Results.Unauthorized();
    }

    var verified = hasher.VerifyHashedPassword(user, user.PasswordHash, req.Password);
    if (verified == PasswordVerificationResult.Failed)
    {
        FamilyChatMetrics.LoginFailed.Add(1);
        user.AccessFailedCount++;
        if (user.AccessFailedCount >= 5)
        {
            user.AccessFailedCount = 0;
            user.LockoutEnd = DateTimeOffset.UtcNow.AddMinutes(15);
            FamilyChatMetrics.AccountLocked.Add(1);
        }
        await db.SaveChangesAsync(ct);
        return Results.Unauthorized();
    }

    user.AccessFailedCount = 0;
    user.LockoutEnd = null;
    user.LastLoginAt = DateTimeOffset.UtcNow;

    var tokens = TokenFactory.CreatePair(user, jwtOptions);
    db.RefreshTokens.Add(new RefreshToken
    {
        Id = Guid.NewGuid(),
        UserId = user.Id,
        FamilyId = Guid.NewGuid(),
        TokenHash = TokenFactory.Hash(tokens.RefreshToken),
        ExpiresAt = tokens.RefreshExpiresAt
    });

    await db.SaveChangesAsync(ct);
    SetRefreshCookie(http.Response, tokens, app.Environment.IsDevelopment());
    return Results.Ok(AuthResponse.From(user, tokens));
});

app.MapPost("/api/v1/auth/refresh", async (
    HttpContext http,
    IdentityDbContext db,
    JwtOptions jwtOptions,
    CancellationToken ct) =>
{
    if (!HasCsrfHeader(http.Request) ||
        !http.Request.Cookies.TryGetValue(AuthCookies.RefreshToken, out var refreshToken))
        return Results.Unauthorized();

    var hash = TokenFactory.Hash(refreshToken);
    var rotation = await RefreshTokenStore.RotateAsync(db, hash, jwtOptions, ct);
    if (rotation.Status == RefreshRotationStatus.ReplayDetected)
        FamilyChatMetrics.RefreshReplayDetected.Add(1);
    if (rotation.Status != RefreshRotationStatus.Success ||
        rotation.User is null || rotation.Tokens is null)
    {
        ClearRefreshCookie(http.Response, app.Environment.IsDevelopment());
        return Results.Unauthorized();
    }

    SetRefreshCookie(http.Response, rotation.Tokens, app.Environment.IsDevelopment());
    return Results.Ok(AuthResponse.From(rotation.User, rotation.Tokens));
});

app.MapPost("/api/v1/auth/logout", async (
    HttpContext http,
    IdentityDbContext db,
    CancellationToken ct) =>
{
    if (!HasCsrfHeader(http.Request)) return Results.Unauthorized();
    if (!TryUserId(http.User, out var userId)) return Results.Unauthorized();
    if (http.Request.Cookies.TryGetValue(AuthCookies.RefreshToken, out var refreshToken))
    {
        var hash = TokenFactory.Hash(refreshToken);
        var token = await db.RefreshTokens.FirstOrDefaultAsync(
            t => t.UserId == userId && t.TokenHash == hash, ct);
        if (token is not null)
        {
            await db.RefreshTokens
                .Where(t => t.UserId == userId && t.FamilyId == token.FamilyId && t.RevokedAt == null)
                .ExecuteUpdateAsync(setters => setters.SetProperty(
                    t => t.RevokedAt, DateTimeOffset.UtcNow), ct);
        }
    }
    ClearRefreshCookie(http.Response, app.Environment.IsDevelopment());
    return Results.NoContent();
}).RequireAuthorization();

await app.RunAsync();

static bool TryUserId(ClaimsPrincipal principal, out Guid id)
{
    var raw = principal.FindFirstValue("sub") ?? principal.FindFirstValue(ClaimTypes.NameIdentifier);
    return Guid.TryParse(raw, out id);
}

static bool HasCsrfHeader(HttpRequest request) =>
    request.Headers.TryGetValue("X-FamilyChat-CSRF", out var value) && value == "1";

static void SetRefreshCookie(HttpResponse response, TokenPair tokens, bool isDevelopment) =>
    response.Cookies.Append(AuthCookies.RefreshToken, tokens.RefreshToken, RefreshCookieOptions(
        tokens.RefreshExpiresAt, isDevelopment));

static void ClearRefreshCookie(HttpResponse response, bool isDevelopment) =>
    response.Cookies.Delete(AuthCookies.RefreshToken, RefreshCookieOptions(
        DateTimeOffset.UnixEpoch, isDevelopment));

static CookieOptions RefreshCookieOptions(DateTimeOffset expires, bool isDevelopment) => new()
{
    HttpOnly = true,
    Secure = !isDevelopment,
    SameSite = SameSiteMode.Strict,
    Path = "/api/v1/auth",
    Expires = expires,
    IsEssential = true
};

record RegisterRequest(string Username, string Email, string Password);
record LoginRequest(string Email, string Password);
static class IdentityInput
{
    public static bool TryRegistration(
        string? usernameInput,
        string? emailInput,
        string? password,
        out string username,
        out string email)
    {
        username = usernameInput?.Trim() ?? "";
        email = NormalizeEmail(emailInput);
        return username.Length is >= 3 and <= 100
            && username.All(c => char.IsLetterOrDigit(c) || c is '_' or '-' or '.')
            && email.Length > 0
            && password?.Length is >= 8 and <= 128;
    }

    public static bool TryLogin(string? emailInput, string? password, out string email)
    {
        email = NormalizeEmail(emailInput);
        return email.Length > 0 && password?.Length is >= 1 and <= 128;
    }

    private static string NormalizeEmail(string? input)
    {
        var candidate = input?.Trim() ?? "";
        if (candidate.Length is < 3 or > 255 ||
            !MailAddress.TryCreate(candidate, out var parsed) ||
            !string.Equals(parsed.Address, candidate, StringComparison.OrdinalIgnoreCase))
            return "";
        return candidate.ToLowerInvariant();
    }
}
static class AuthCookies
{
    public const string RefreshToken = "familychat.refresh";
}

record AuthUser(Guid Id, string Username, string Email);
record AuthResponse(
    string AccessToken,
    DateTimeOffset AccessExpiresAt,
    AuthUser User)
{
    public static AuthResponse From(AppUser u, TokenPair t) =>
        new(t.AccessToken, t.AccessExpiresAt, new AuthUser(u.Id, u.Username, u.Email));
}

class AppUser
{
    public Guid Id { get; set; }
    public string Username { get; set; } = "";
    public string Email { get; set; } = "";
    public string PasswordHash { get; set; } = "";
    public int AccessFailedCount { get; set; }
    public DateTimeOffset? LockoutEnd { get; set; }
    public DateTimeOffset CreatedAt { get; set; }
    public DateTimeOffset? LastLoginAt { get; set; }
    public List<RefreshToken> RefreshTokens { get; set; } = [];
}

class RefreshToken
{
    public Guid Id { get; set; }
    public Guid UserId { get; set; }
    public Guid FamilyId { get; set; }
    public string TokenHash { get; set; } = "";
    public DateTimeOffset ExpiresAt { get; set; }
    public DateTimeOffset? RevokedAt { get; set; }
    public AppUser User { get; set; } = null!;
}

class IdentityDbContext(DbContextOptions<IdentityDbContext> options) : DbContext(options)
{
    public DbSet<AppUser> Users => Set<AppUser>();
    public DbSet<RefreshToken> RefreshTokens => Set<RefreshToken>();
    public DbSet<OutboxMessage> OutboxMessages => Set<OutboxMessage>();

    protected override void OnModelCreating(ModelBuilder b)
    {
        b.Entity<AppUser>().ToTable("users").HasKey(x => x.Id);
        b.Entity<AppUser>().HasIndex(x => x.Email).IsUnique();
        b.Entity<AppUser>().HasIndex(x => x.Username).IsUnique();
        b.Entity<AppUser>().Property(x => x.Email).HasMaxLength(255);
        b.Entity<AppUser>().Property(x => x.Username).HasMaxLength(100);
        b.Entity<AppUser>().Property(x => x.PasswordHash).HasMaxLength(512);

        b.Entity<RefreshToken>().ToTable("refresh_tokens").HasKey(x => x.Id);
        b.Entity<RefreshToken>().HasIndex(x => x.TokenHash).IsUnique();
        b.Entity<RefreshToken>().HasIndex(x => new { x.UserId, x.FamilyId });
        b.Entity<RefreshToken>().HasOne(x => x.User).WithMany(x => x.RefreshTokens)
            .HasForeignKey(x => x.UserId).OnDelete(DeleteBehavior.Cascade);

        b.Entity<OutboxMessage>().ToTable("outbox_messages").HasKey(x => x.Id);
        b.Entity<OutboxMessage>().HasIndex(x => new { x.PublishedAt, x.NextAttemptAt, x.OccurredAt });
        b.Entity<OutboxMessage>().Property(x => x.Type).HasMaxLength(200);
    }
}

enum RefreshRotationStatus { Success, Invalid, Expired, ReplayDetected }

record RefreshRotationResult(
    RefreshRotationStatus Status,
    AppUser? User = null,
    TokenPair? Tokens = null);

static class RefreshTokenStore
{
    public static async Task<RefreshRotationResult> RotateAsync(
        IdentityDbContext db,
        string tokenHash,
        JwtOptions jwtOptions,
        CancellationToken ct)
    {
        await using var transaction = await db.Database.BeginTransactionAsync(ct);
        var stored = await db.RefreshTokens
            .FromSqlInterpolated($"SELECT * FROM refresh_tokens WHERE \"TokenHash\" = {tokenHash} FOR UPDATE")
            .Include(t => t.User)
            .SingleOrDefaultAsync(ct);

        if (stored is null)
            return new RefreshRotationResult(RefreshRotationStatus.Invalid);

        var now = DateTimeOffset.UtcNow;
        if (stored.ExpiresAt <= now)
        {
            stored.RevokedAt ??= now;
            await db.SaveChangesAsync(ct);
            await transaction.CommitAsync(ct);
            return new RefreshRotationResult(RefreshRotationStatus.Expired);
        }

        if (stored.RevokedAt is not null)
        {
            await db.RefreshTokens
                .Where(t => t.UserId == stored.UserId &&
                    t.FamilyId == stored.FamilyId && t.RevokedAt == null)
                .ExecuteUpdateAsync(setters => setters.SetProperty(t => t.RevokedAt, now), ct);
            await transaction.CommitAsync(ct);
            return new RefreshRotationResult(RefreshRotationStatus.ReplayDetected);
        }

        stored.RevokedAt = now;
        var tokens = TokenFactory.CreatePair(stored.User, jwtOptions);
        db.RefreshTokens.Add(new RefreshToken
        {
            Id = Guid.NewGuid(),
            UserId = stored.UserId,
            FamilyId = stored.FamilyId,
            TokenHash = TokenFactory.Hash(tokens.RefreshToken),
            ExpiresAt = tokens.RefreshExpiresAt
        });
        await db.SaveChangesAsync(ct);
        await transaction.CommitAsync(ct);
        return new RefreshRotationResult(RefreshRotationStatus.Success, stored.User, tokens);
    }
}

class OutboxMessage
{
    public Guid Id { get; set; }
    public string Type { get; set; } = "";
    public string Payload { get; set; } = "";
    public DateTimeOffset OccurredAt { get; set; }
    public DateTimeOffset? PublishedAt { get; set; }
    public int Attempts { get; set; }
    public DateTimeOffset? NextAttemptAt { get; set; }
    public string? LastError { get; set; }

    public static OutboxMessage Create<T>(Guid id, string type, T message) => new()
    {
        Id = id,
        Type = type,
        Payload = JsonSerializer.Serialize(message),
        OccurredAt = DateTimeOffset.UtcNow
    };
}

class IdentityOutboxPublisher(IServiceScopeFactory scopeFactory, ILogger<IdentityOutboxPublisher> logger)
    : BackgroundService
{
    protected override async Task ExecuteAsync(CancellationToken stoppingToken)
    {
        while (!stoppingToken.IsCancellationRequested)
        {
            try { await PublishBatch(stoppingToken); }
            catch (Exception ex) { logger.LogError(ex, "Identity outbox batch failed"); }
            await Task.Delay(TimeSpan.FromSeconds(2), stoppingToken);
        }
    }

    async Task PublishBatch(CancellationToken ct)
    {
        await using var scope = scopeFactory.CreateAsyncScope();
        var db = scope.ServiceProvider.GetRequiredService<IdentityDbContext>();
        var publish = scope.ServiceProvider.GetRequiredService<IPublishEndpoint>();
        await using var outboxLock = await PostgresAdvisoryLock.TryAcquireAsync(
            db.Database.GetDbConnection(), 0x4944454E54495459, ct);
        if (outboxLock is null) return;
        var now = DateTimeOffset.UtcNow;
        var messages = await db.OutboxMessages
            .Where(x => x.PublishedAt == null &&
                (x.NextAttemptAt == null || x.NextAttemptAt <= now))
            .OrderBy(x => x.OccurredAt).Take(50).ToListAsync(ct);

        foreach (var message in messages)
        {
            try
            {
                if (message.Type != nameof(UserRegisteredEvent))
                    throw new InvalidOperationException($"Unknown outbox message type: {message.Type}");
                var payload = JsonSerializer.Deserialize<UserRegisteredEvent>(message.Payload)
                    ?? throw new InvalidOperationException("Invalid outbox payload");
                await publish.Publish(payload, ct);
                message.PublishedAt = DateTimeOffset.UtcNow;
                message.NextAttemptAt = null;
                message.LastError = null;
            }
            catch (Exception ex)
            {
                message.Attempts++;
                message.NextAttemptAt = DateTimeOffset.UtcNow.Add(
                    OutboxRetry.Delay(message.Attempts));
                message.LastError = ex.Message[..Math.Min(ex.Message.Length, 2000)];
                FamilyChatMetrics.OutboxPublishFailed.Add(1,
                    new KeyValuePair<string, object?>("service", "identity"),
                    new KeyValuePair<string, object?>("event_type", message.Type));
                if (message.Attempts == 20)
                {
                    FamilyChatMetrics.OutboxStalled.Add(1,
                        new KeyValuePair<string, object?>("service", "identity"),
                        new KeyValuePair<string, object?>("event_type", message.Type));
                    logger.LogError("Identity outbox message {MessageId} remains unpublished after 20 attempts", message.Id);
                }
                logger.LogWarning(ex, "Failed to publish outbox message {MessageId}", message.Id);
            }
            await db.SaveChangesAsync(ct);
        }
    }
}

record JwtOptions(SecurityKey SigningKey, string Issuer, string Audience, int AccessMinutes, int RefreshDays)
{
    public static JwtOptions From(IConfiguration c, IHostEnvironment environment) => new(
        JwtKeyFactory.SigningKey(c, environment),
        c["JWT:Issuer"] ?? "familychat",
        c["JWT:Audience"] ?? "familychat-web",
        int.TryParse(c["JWT:AccessMinutes"], out var a) ? a : 15,
        int.TryParse(c["JWT:RefreshDays"], out var r) ? r : 30);

    public TokenValidationParameters ValidationParameters(bool validateLifetime) => new()
    {
        ValidateIssuer = true,
        ValidateAudience = true,
        ValidateIssuerSigningKey = true,
        ValidateLifetime = validateLifetime,
        ValidIssuer = Issuer,
        ValidAudience = Audience,
        IssuerSigningKey = SigningKey,
        ClockSkew = TimeSpan.FromSeconds(10)
    };
}

record TokenPair(
    string AccessToken, DateTimeOffset AccessExpiresAt,
    string RefreshToken, DateTimeOffset RefreshExpiresAt);

static class TokenFactory
{
    public static TokenPair CreatePair(AppUser user, JwtOptions o)
    {
        var accessExp = DateTimeOffset.UtcNow.AddMinutes(o.AccessMinutes);
        var claims = new[]
        {
            new Claim(JwtRegisteredClaimNames.Sub, user.Id.ToString()),
            new Claim(JwtRegisteredClaimNames.Email, user.Email),
            new Claim("username", user.Username),
            new Claim(JwtRegisteredClaimNames.Jti, Guid.NewGuid().ToString())
        };
        var creds = new SigningCredentials(
            o.SigningKey, JwtKeyFactory.Algorithm(o.SigningKey));
        var jwt = new JwtSecurityToken(
            o.Issuer, o.Audience, claims, expires: accessExp.UtcDateTime, signingCredentials: creds);
        var access = new JwtSecurityTokenHandler().WriteToken(jwt);

        var refreshBytes = RandomNumberGenerator.GetBytes(64);
        var refresh = Convert.ToBase64String(refreshBytes);
        return new(access, accessExp, refresh, DateTimeOffset.UtcNow.AddDays(o.RefreshDays));
    }

    public static string Hash(string token) =>
        Convert.ToHexString(SHA256.HashData(Encoding.UTF8.GetBytes(token)));

    public static Guid? ReadUserIdIgnoringExpiry(string token, JwtOptions o)
    {
        try
        {
            var handler = new JwtSecurityTokenHandler { MapInboundClaims = false };
            var principal = handler.ValidateToken(token, o.ValidationParameters(false), out _);
            var raw = principal.FindFirstValue("sub") ?? principal.FindFirstValue(ClaimTypes.NameIdentifier);
            return Guid.TryParse(raw, out var id) ? id : null;
        }
        catch { return null; }
    }
}
