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

    [Fact]
    public void Production_LoadsArrayOriginsWithoutBroadeningThem()
    {
        var configuration = new ConfigurationBuilder().AddInMemoryCollection(
            new Dictionary<string, string?>
            {
                ["Cors:AllowedOrigins:0"] = "https://app.example.com",
                ["Cors:AllowedOrigins:1"] = "https://admin.example.com/"
            }).Build();

        Assert.Equal(
            ["https://app.example.com", "https://admin.example.com"],
            CorsOrigins.Load(configuration, false));
    }

    [Theory]
    [InlineData("*")]
    [InlineData("https://app.example.com/path")]
    [InlineData("https://app.example.com?preview=1")]
    [InlineData("https://app.example.com/#fragment")]
    [InlineData("https://user:password@app.example.com")]
    [InlineData("http://app.example.com")]
    [InlineData("ws://app.example.com")]
    [InlineData("wss://app.example.com")]
    [InlineData("ftp://app.example.com")]
    public void Production_RejectsValuesThatAreNotExactHttpsOrigins(string value)
    {
        Assert.Throws<InvalidOperationException>(() => CorsOrigins.Load(Configuration(value), false));
    }

    [Fact]
    public void Production_DeduplicatesOriginsCaseInsensitively()
    {
        var origins = CorsOrigins.Load(Configuration(
            "https://APP.example.com,https://app.example.com/"), false);

        Assert.Single(origins);
        Assert.Equal("https://app.example.com", origins[0], ignoreCase: true);
    }

    static IConfiguration Configuration(string? origins = null) =>
        new ConfigurationBuilder().AddInMemoryCollection(new Dictionary<string, string?>
        {
            ["Cors:AllowedOrigins"] = origins
        }).Build();
}
