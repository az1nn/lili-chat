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

        var postgres = new DbConnectionStringBuilder
        {
            ConnectionString = builder.Configuration.GetConnectionString("Default")!
        };
        Assert.Equal("db.internal", postgres["Host"]?.ToString());
        Assert.Equal("5432", postgres["Port"]?.ToString());
        Assert.Equal("chat", postgres["Database"]?.ToString());
        Assert.Equal("app", postgres["Username"]?.ToString());
        Assert.Equal("p@ss", postgres["Password"]?.ToString());

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

    [Fact]
    public void ExistingComposeConnectionStringsRemainUnchanged()
    {
        var builder = WebApplication.CreateBuilder();
        const string postgres = "Host=postgres;Port=5432;Database=identity;Username=app;Password=dev";
        const string redis = "redis:6379";
        builder.Configuration["ConnectionStrings:Default"] = postgres;
        builder.Configuration["Redis:Connection"] = redis;

        builder.AddFamilyChatObservability("compose-config-test");

        Assert.Equal(postgres, builder.Configuration.GetConnectionString("Default"));
        Assert.Equal(redis, builder.Configuration["Redis:Connection"]);
    }
}
