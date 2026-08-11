using System.Diagnostics.Metrics;
using System.Data;
using System.Data.Common;
using System.Security.Cryptography;
using System.Text;
using Microsoft.AspNetCore.Builder;
using Microsoft.AspNetCore.Http;
using Microsoft.Extensions.Configuration;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Hosting;
using Microsoft.IdentityModel.Tokens;
using OpenTelemetry.Metrics;
using OpenTelemetry.Resources;
using OpenTelemetry.Trace;

namespace FamilyChat.ServiceDefaults;

public sealed class PostgresAdvisoryLock : IAsyncDisposable
{
    private readonly DbConnection _connection;
    private readonly long _key;
    private readonly bool _closeConnection;

    private PostgresAdvisoryLock(DbConnection connection, long key, bool closeConnection)
    {
        _connection = connection;
        _key = key;
        _closeConnection = closeConnection;
    }

    public static async Task<PostgresAdvisoryLock?> TryAcquireAsync(
        DbConnection connection,
        long key,
        CancellationToken cancellationToken)
    {
        var closeConnection = connection.State != ConnectionState.Open;
        if (closeConnection) await connection.OpenAsync(cancellationToken);

        await using var command = connection.CreateCommand();
        command.CommandText = "SELECT pg_try_advisory_lock(@key)";
        var parameter = command.CreateParameter();
        parameter.ParameterName = "key";
        parameter.Value = key;
        command.Parameters.Add(parameter);

        var acquired = await command.ExecuteScalarAsync(cancellationToken) is true;
        if (acquired) return new PostgresAdvisoryLock(connection, key, closeConnection);
        if (closeConnection) await connection.CloseAsync();
        return null;
    }

    public async ValueTask DisposeAsync()
    {
        if (_connection.State == ConnectionState.Open)
        {
            await using var command = _connection.CreateCommand();
            command.CommandText = "SELECT pg_advisory_unlock(@key)";
            var parameter = command.CreateParameter();
            parameter.ParameterName = "key";
            parameter.Value = _key;
            command.Parameters.Add(parameter);
            await command.ExecuteScalarAsync();
            if (_closeConnection) await _connection.CloseAsync();
        }
    }
}

public static class ServiceDefaults
{
    public static WebApplicationBuilder AddFamilyChatObservability(
        this WebApplicationBuilder builder,
        string serviceName)
    {
        builder.Services.AddOpenTelemetry()
            .ConfigureResource(r => r.AddService(serviceName))
            .WithTracing(t => t
                .AddAspNetCoreInstrumentation()
                .AddHttpClientInstrumentation()
                .AddOtlpExporter())
            .WithMetrics(m => m
                .AddMeter(FamilyChatMetrics.MeterName)
                .AddAspNetCoreInstrumentation()
                .AddRuntimeInstrumentation()
                .AddOtlpExporter());

        builder.Services.AddHealthChecks();
        return builder;
    }
}

public static class FamilyChatMetrics
{
    public const string MeterName = "FamilyChat";
    static readonly Meter Meter = new(MeterName);

    public static readonly Counter<long> MessagePublished =
        Meter.CreateCounter<long>("message_publish", unit: "messages");
    public static readonly Counter<long> MessagePersisted =
        Meter.CreateCounter<long>("message_persisted", unit: "messages");
    public static readonly Counter<long> MessagePersistenceFailed =
        Meter.CreateCounter<long>("message_persist_failed", unit: "messages");
    public static readonly Counter<long> LoginFailed =
        Meter.CreateCounter<long>("auth_login_failed", unit: "attempts");
    public static readonly Counter<long> AccountLocked =
        Meter.CreateCounter<long>("auth_account_locked", unit: "accounts");
    public static readonly Counter<long> RefreshReplayDetected =
        Meter.CreateCounter<long>("auth_refresh_replay_detected", unit: "attempts");
    public static readonly Counter<long> OutboxPublishFailed =
        Meter.CreateCounter<long>("outbox_publish_failed", unit: "attempts");
    public static readonly Counter<long> OutboxStalled =
        Meter.CreateCounter<long>("outbox_stalled", unit: "events");
    public static readonly UpDownCounter<long> SignalRActiveConnections =
        Meter.CreateUpDownCounter<long>("signalr_active_connections", unit: "connections");
    public static readonly Counter<long> SignalRConnections =
        Meter.CreateCounter<long>("signalr_connections", unit: "connections");
    public static readonly Counter<long> SignalRDisconnects =
        Meter.CreateCounter<long>("signalr_disconnects", unit: "connections");
    public static readonly Counter<long> RedisFailures =
        Meter.CreateCounter<long>("redis_failures", unit: "failures");
    public static readonly Counter<long> GrpcFailures =
        Meter.CreateCounter<long>("grpc_failures", unit: "failures");
}

public static class OutboxRetry
{
    public static TimeSpan Delay(int attempt)
    {
        var exponent = Math.Clamp(attempt, 1, 9);
        return TimeSpan.FromSeconds(Math.Min(Math.Pow(2, exponent), 300));
    }
}

public static class JwtKeyFactory
{
    public static SecurityKey SigningKey(IConfiguration configuration, IHostEnvironment environment)
    {
        var encoded = configuration["JWT:PrivateKeyBase64"];
        if (!string.IsNullOrWhiteSpace(encoded)) return ReadRsaKey(encoded, includePrivate: true);
        return DevelopmentSymmetricKey(configuration, environment);
    }

    public static SecurityKey ValidationKey(IConfiguration configuration, IHostEnvironment environment)
    {
        var encoded = configuration["JWT:PublicKeyBase64"];
        if (!string.IsNullOrWhiteSpace(encoded)) return ReadRsaKey(encoded, includePrivate: false);
        return DevelopmentSymmetricKey(configuration, environment);
    }

    public static string Algorithm(SecurityKey key) =>
        key is RsaSecurityKey ? SecurityAlgorithms.RsaSha256 : SecurityAlgorithms.HmacSha256;

    static SecurityKey ReadRsaKey(string encodedPem, bool includePrivate)
    {
        try
        {
            var pem = Encoding.UTF8.GetString(Convert.FromBase64String(encodedPem));
            var rsa = RSA.Create();
            rsa.ImportFromPem(pem);
            if (includePrivate)
            {
                _ = rsa.ExportParameters(includePrivateParameters: true);
                return new RsaSecurityKey(rsa);
            }

            var publicOnly = RSA.Create();
            publicOnly.ImportParameters(rsa.ExportParameters(includePrivateParameters: false));
            rsa.Dispose();
            return new RsaSecurityKey(publicOnly);
        }
        catch (Exception ex) when (ex is FormatException or CryptographicException)
        {
            throw new InvalidOperationException("JWT RSA key is not valid base64-encoded PEM.", ex);
        }
    }

    static SecurityKey DevelopmentSymmetricKey(
        IConfiguration configuration, IHostEnvironment environment)
    {
        if (!environment.IsDevelopment())
            throw new InvalidOperationException(
                "Production requires JWT:PrivateKeyBase64 in Identity and JWT:PublicKeyBase64 in validators.");
        var secret = configuration["JWT:Secret"]
            ?? throw new InvalidOperationException("JWT:Secret is required in Development.");
        if (Encoding.UTF8.GetByteCount(secret) < 32)
            throw new InvalidOperationException("Development JWT:Secret must contain at least 32 bytes.");
        return new SymmetricSecurityKey(Encoding.UTF8.GetBytes(secret));
    }
}

public static class InternalServiceAuth
{
    public const string HeaderName = "X-FamilyChat-Service-Token";

    public static string RequiredToken(IConfiguration configuration, string key)
    {
        var token = configuration[key];
        if (string.IsNullOrWhiteSpace(token) || Encoding.UTF8.GetByteCount(token) < 32)
            throw new InvalidOperationException($"{key} must contain at least 32 bytes.");
        return token;
    }

    public static bool IsValid(string? provided, string expected)
    {
        if (provided is null) return false;
        var providedBytes = Encoding.UTF8.GetBytes(provided);
        var expectedBytes = Encoding.UTF8.GetBytes(expected);
        return providedBytes.Length == expectedBytes.Length &&
            CryptographicOperations.FixedTimeEquals(providedBytes, expectedBytes);
    }

    public static WebApplication UseInternalGrpcAuthentication(
        this WebApplication app, PathString servicePath, string expectedToken)
    {
        app.Use(async (context, next) =>
        {
            if (context.Request.Path.StartsWithSegments(servicePath))
            {
                var provided = context.Request.Headers[HeaderName].FirstOrDefault();
                if (!IsValid(provided, expectedToken))
                {
                    context.Response.StatusCode = StatusCodes.Status401Unauthorized;
                    return;
                }
            }
            await next();
        });
        return app;
    }

    public static void AddToken(HttpClient client, string token) =>
        client.DefaultRequestHeaders.Add(HeaderName, token);
}
