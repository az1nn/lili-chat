using System.Reflection;
using Microsoft.AspNetCore.Http;
using Xunit;

namespace Gateway.Tests;

public class GatewaySecurityPolicyTests
{
    [Theory]
    [InlineData("/api/v1/auth/login", "auth", 10, 0)]
    [InlineData("/api/v1/auth/register", "auth", 10, 0)]
    [InlineData("/api/v1/auth/refresh", "refresh", 30, 0)]
    [InlineData("/api/v1/auth/account", "account-delete", 5, 0)]
    [InlineData("/api/v1/users/by-public-id/abc", "public-id", 60, 0)]
    [InlineData("/api/v1/messages/room/abc", "history", 120, 10)]
    [InlineData("/hubs/chat", "signalr-connect", 60, 0)]
    [InlineData("/hubs/chat/negotiate", "signalr-connect", 60, 0)]
    [InlineData("/api/v1/other", "global", 300, 20)]
    public void SensitiveRoutes_KeepDedicatedRateLimitBudgets(
        string path,
        string expectedPolicy,
        int expectedPermitLimit,
        int expectedQueueLimit)
    {
        var actual = RateLimitPolicy(new PathString(path));

        Assert.Equal(expectedPolicy, actual.Policy);
        Assert.Equal(expectedPermitLimit, actual.PermitLimit);
        Assert.Equal(expectedQueueLimit, actual.QueueLimit);
    }

    [Fact]
    public void SimilarButDifferentAuthPath_DoesNotAccidentallyReceiveLoginBudget()
    {
        var actual = RateLimitPolicy(new PathString("/api/v1/auth/login-extra"));

        Assert.Equal("global", actual.Policy);
        Assert.Equal(300, actual.PermitLimit);
        Assert.Equal(20, actual.QueueLimit);
    }

    static (string Policy, int PermitLimit, int QueueLimit) RateLimitPolicy(PathString path)
    {
        var program = typeof(CorsOrigins).Assembly.GetType("Program")
            ?? throw new InvalidOperationException("Gateway top-level Program type was not found.");
        var method = program.GetMethods(BindingFlags.Static | BindingFlags.NonPublic)
            .Single(candidate =>
                candidate.ReturnType == typeof(ValueTuple<string, int, int>) &&
                candidate.GetParameters().Length == 1 &&
                candidate.GetParameters()[0].ParameterType == typeof(PathString));

        return Assert.IsType<ValueTuple<string, int, int>>(method.Invoke(null, [path]));
    }
}
