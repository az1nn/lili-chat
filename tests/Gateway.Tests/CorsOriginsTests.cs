using Microsoft.Extensions.Configuration;
using Xunit;

namespace Gateway.Tests;

public class CorsOriginsTests
{
    [Fact]
    public void Development_DefaultsToLocalWeb()
    {
        Assert.Equal(["http://localhost:3000"], CorsOrigins.Load(Configuration(), true));
    }

    [Fact]
    public void Production_RequiresExplicitHttpsOrigins()
    {
        Assert.Throws<InvalidOperationException>(() => CorsOrigins.Load(Configuration(), false));
        Assert.Throws<InvalidOperationException>(() => CorsOrigins.Load(Configuration(
            "http://app.example.com"), false));
    }

    [Fact]
    public void Production_LoadsAndDeduplicatesCommaSeparatedOrigins()
    {
        var origins = CorsOrigins.Load(Configuration(
            "https://app.example.com, https://admin.example.com/,https://APP.example.com"), false);

        Assert.Equal(["https://app.example.com", "https://admin.example.com"], origins);
    }

    [Theory]
    [InlineData("*")]
    [InlineData("https://app.example.com/path")]
    [InlineData("https://app.example.com?preview=1")]
    public void RejectsValuesThatAreNotExactOrigins(string value)
    {
        Assert.Throws<InvalidOperationException>(() => CorsOrigins.Load(Configuration(value), false));
    }

    static IConfiguration Configuration(string? origins = null) =>
        new ConfigurationBuilder().AddInMemoryCollection(new Dictionary<string, string?>
        {
            ["Cors:AllowedOrigins"] = origins
        }).Build();
}
