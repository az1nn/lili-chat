using FamilyChat.ServiceDefaults;
using Microsoft.Extensions.Configuration;
using Xunit;

namespace Gateway.Tests;

public class InfrastructureCredentialsTests
{
    [Fact]
    public void Development_AllowsLocalGuestFallback()
    {
        var credentials = RabbitMqCredentials.Load(Configuration(), true);
        Assert.Equal("guest", credentials.Username);
        Assert.Equal("guest", credentials.Password);
    }

    [Theory]
    [InlineData(null, null)]
    [InlineData("guest", "strong-password")]
    [InlineData("familychat", "guest")]
    public void Production_RejectsMissingOrGuestCredentials(string? username, string? password)
    {
        Assert.Throws<InvalidOperationException>(() => RabbitMqCredentials.Load(
            Configuration(username, password), false));
    }

    [Fact]
    public void Production_AcceptsExplicitNonGuestCredentials()
    {
        var credentials = RabbitMqCredentials.Load(
            Configuration("familychat", "strong-password"), false);
        Assert.Equal("familychat", credentials.Username);
        Assert.Equal("strong-password", credentials.Password);
    }

    static IConfiguration Configuration(string? username = null, string? password = null) =>
        new ConfigurationBuilder().AddInMemoryCollection(new Dictionary<string, string?>
        {
            ["RabbitMQ:User"] = username,
            ["RabbitMQ:Pass"] = password
        }).Build();
}
