using System.Data.Common;
using FamilyChat.ServiceDefaults;
using Microsoft.AspNetCore.Builder;
using Xunit;

public class PlatformConfigurationTests
{
    [Fact]
    public void RenderUrlsAndHostsAreNormalizedBeforeServicesReadConfiguration()
    {
        var builder = WebApplication.CreateBuilder();
        builder.Configuration["ConnectionStrings:Default"] =
            "postgresql://app:p%40ss@db.internal:5432/chat";
        builder.Configuration["Redis:Connection"] = "redis://redis.internal:6379";
        builder.Configuration["Render:IdentityHost"] = "identity-abc";
        builder.Configuration["Render:FamilyHost"] = "family-abc";
        builder.Configuration["Render:RoomHost"] = "room-abc";
        builder.Configuration["Render:MessageHost"] = "message-abc";
        builder.Configuration["Render:RealtimeHost"] = "realtime-abc";

        builder.AddFamilyChatObservability("platform-config-test");

        AssertPostgres(
            builder.Configuration["ConnectionStrings:Default"]!,
            host: "db.internal",
            port: "5432",
            database: "chat",
            username: "app",
            password: "p@ss");

        Assert.Equal("redis.internal:6379,abortConnect=false",
            builder.Configuration["Redis:Connection"]);
        Assert.Equal("http://identity-abc:8080",
            builder.Configuration["ReverseProxy:Clusters:identity:Destinations:d1:Address"]);
        Assert.Equal("http://family-abc:8080",
            builder.Configuration["ReverseProxy:Clusters:family:Destinations:d1:Address"]);
        Assert.Equal("http://room-abc:8080",
            builder.Configuration["ReverseProxy:Clusters:room:Destinations:d1:Address"]);
        Assert.Equal("http://message-abc:8080",
            builder.Configuration["ReverseProxy:Clusters:message:Destinations:d1:Address"]);
        Assert.Equal("http://realtime-abc:8080",
            builder.Configuration["ReverseProxy:Clusters:realtime:Destinations:d1:Address"]);
        Assert.Equal("http://family-abc:8081",
            builder.Configuration["Services:FamilyGraph"]);
        Assert.Equal("http://room-abc:8081",
            builder.Configuration["Services:Room"]);
    }

    [Theory]
    [InlineData(
        "postgres://app:secret@db.internal/chat",
        "db.internal", "5432", "chat", "app", "secret")]
    [InlineData(
        "postgresql://user%2Bname:p%40ss%3Aword@db.internal:6543/family%20chat",
        "db.internal", "6543", "family chat", "user+name", "p@ss:word")]
    public void PostgreSqlRenderUrlsSupportBothSchemesPortsAndEncodedCredentials(
        string value,
        string host,
        string port,
        string database,
        string username,
        string password)
    {
        var builder = WebApplication.CreateBuilder();
        builder.Configuration["ConnectionStrings:Default"] = value;

        builder.AddFamilyChatObservability("postgres-config-test");

        AssertPostgres(
            builder.Configuration["ConnectionStrings:Default"]!,
            host,
            port,
            database,
            username,
            password);
    }

    [Theory]
    [InlineData(
        "redis://redis.internal",
        "redis.internal:6379,abortConnect=false")]
    [InlineData(
        "redis://user:p%40ss@redis.internal:6380",
        "redis.internal:6380,user=user,password=p@ss,abortConnect=false")]
    [InlineData(
        "rediss://user:p%40ss@redis.internal",
        "redis.internal:6379,user=user,password=p@ss,ssl=true,abortConnect=false")]
    [InlineData(
        "rediss://:secret@redis.internal:6380",
        "redis.internal:6380,password=secret,ssl=true,abortConnect=false")]
    public void RedisRenderUrlsSupportTlsAuthenticationDefaultPortsAndEncodedCredentials(
        string value,
        string expected)
    {
        var builder = WebApplication.CreateBuilder();
        builder.Configuration["Redis:Connection"] = value;

        builder.AddFamilyChatObservability("redis-config-test");

        Assert.Equal(expected, builder.Configuration["Redis:Connection"]);
    }

    [Theory]
    [InlineData("postgresql://db.internal/chat")]
    [InlineData("postgresql://app:secret@db.internal/")]
    public void InvalidPostgreSqlRenderUrlsFailFast(string value)
    {
        var builder = WebApplication.CreateBuilder();
        builder.Configuration["ConnectionStrings:Default"] = value;

        var exception = Assert.Throws<InvalidOperationException>(() =>
            builder.AddFamilyChatObservability("invalid-postgres-config-test"));

        Assert.Contains("PostgreSQL URL must include", exception.Message);
    }

    [Theory]
    [InlineData("redis://")]
    [InlineData("rediss://")]
    public void InvalidRedisRenderUrlsFailFast(string value)
    {
        var builder = WebApplication.CreateBuilder();
        builder.Configuration["Redis:Connection"] = value;

        var exception = Assert.Throws<InvalidOperationException>(() =>
            builder.AddFamilyChatObservability("invalid-redis-config-test"));

        Assert.Contains("Redis URL", exception.Message);
    }

    [Fact]
    public void RenderHostsAreTrimmedBeforeServiceAddressesAreInjected()
    {
        var builder = WebApplication.CreateBuilder();
        builder.Configuration["Render:IdentityHost"] = "  identity-abc.internal  ";
        builder.Configuration["Render:FamilyHost"] = "  family-abc.internal  ";
        builder.Configuration["Render:RoomHost"] = "  room-abc.internal  ";

        builder.AddFamilyChatObservability("trimmed-host-config-test");

        Assert.Equal("http://identity-abc.internal:8080",
            builder.Configuration["ReverseProxy:Clusters:identity:Destinations:d1:Address"]);
        Assert.Equal("http://family-abc.internal:8080",
            builder.Configuration["ReverseProxy:Clusters:family:Destinations:d1:Address"]);
        Assert.Equal("http://family-abc.internal:8081",
            builder.Configuration["Services:FamilyGraph"]);
        Assert.Equal("http://room-abc.internal:8081",
            builder.Configuration["Services:Room"]);
    }

    [Fact]
    public void MissingRenderHostsDoNotOverwriteExistingServiceDiscovery()
    {
        var builder = WebApplication.CreateBuilder();
        const string identity = "http://identity-svc:8080";
        const string familyGrpc = "http://family-svc:8081";
        const string roomGrpc = "http://room-svc:8081";
        builder.Configuration["ReverseProxy:Clusters:identity:Destinations:d1:Address"] = identity;
        builder.Configuration["Services:FamilyGraph"] = familyGrpc;
        builder.Configuration["Services:Room"] = roomGrpc;
        builder.Configuration["Render:IdentityHost"] = "   ";
        builder.Configuration["Render:FamilyHost"] = string.Empty;

        builder.AddFamilyChatObservability("existing-discovery-config-test");

        Assert.Equal(identity,
            builder.Configuration["ReverseProxy:Clusters:identity:Destinations:d1:Address"]);
        Assert.Equal(familyGrpc, builder.Configuration["Services:FamilyGraph"]);
        Assert.Equal(roomGrpc, builder.Configuration["Services:Room"]);
    }

    [Fact]
    public void ExistingComposeConnectionStringsRemainUnchanged()
    {
        var builder = WebApplication.CreateBuilder();
        const string postgres = "Host=postgres;Port=5432;Database=identity;Username=app;Password=dev";
        const string redis = "redis:6379";
        builder.Configuration["ConnectionStrings:Default"] = postgres;
        builder.Configuration["Redis:Connection"] = redis;

        builder.AddFamilyChatObservability("compose-config-test");

        Assert.Equal(postgres, builder.Configuration["ConnectionStrings:Default"]);
        Assert.Equal(redis, builder.Configuration["Redis:Connection"]);
    }

    [Fact]
    public void ExistingNonUrlRedisConfigurationRemainsUnchanged()
    {
        var builder = WebApplication.CreateBuilder();
        const string redis = "cache.internal:6380,password=secret,ssl=true,abortConnect=false";
        builder.Configuration["Redis:Connection"] = redis;

        builder.AddFamilyChatObservability("redis-existing-config-test");

        Assert.Equal(redis, builder.Configuration["Redis:Connection"]);
    }

    [Fact]
    public void NonPostgreSqlConnectionStringRemainsUnchanged()
    {
        var builder = WebApplication.CreateBuilder();
        const string connectionString =
            "Host=database.internal;Port=5432;Database=chat;Username=app;Password=secret";
        builder.Configuration["ConnectionStrings:Default"] = connectionString;

        builder.AddFamilyChatObservability("postgres-existing-config-test");

        Assert.Equal(connectionString, builder.Configuration["ConnectionStrings:Default"]);
    }

    private static void AssertPostgres(
        string connectionString,
        string host,
        string port,
        string database,
        string username,
        string password)
    {
        var postgres = new DbConnectionStringBuilder
        {
            ConnectionString = connectionString
        };

        Assert.Equal(host, postgres["Host"]?.ToString());
        Assert.Equal(port, postgres["Port"]?.ToString());
        Assert.Equal(database, postgres["Database"]?.ToString());
        Assert.Equal(username, postgres["Username"]?.ToString());
        Assert.Equal(password, postgres["Password"]?.ToString());
    }
}
