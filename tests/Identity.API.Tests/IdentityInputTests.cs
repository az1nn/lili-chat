using System.Reflection;
using Microsoft.AspNetCore.Http;

public class IdentityInputTests
{
    [Fact]
    public void AccountDeletion_ConfirmsOnlyCurrentPassword()
    {
        var user = new AppUser { Id = Guid.NewGuid() };
        var hasher = new Microsoft.AspNetCore.Identity.PasswordHasher<AppUser>();
        user.PasswordHash = hasher.HashPassword(user, "StrongPassword!123");

        Assert.True(AccountDeletion.ConfirmPassword(hasher, user, "StrongPassword!123"));
        Assert.False(AccountDeletion.ConfirmPassword(hasher, user, "WrongPassword!123"));
        Assert.False(AccountDeletion.ConfirmPassword(hasher, user, null));
    }

    [Fact]
    public void IdentityOutbox_DeserializesUserDeletion()
    {
        var expected = new FamilyChat.Contracts.Events.UserDeletedEvent(
            Guid.NewGuid(), Guid.NewGuid(), DateTimeOffset.UtcNow);
        var message = OutboxMessage.Create(
            expected.CorrelationId,
            nameof(FamilyChat.Contracts.Events.UserDeletedEvent),
            expected);

        var actual = Assert.IsType<FamilyChat.Contracts.Events.UserDeletedEvent>(
            IdentityOutbox.Deserialize(message));

        Assert.Equal(expected, actual);
    }

    [Fact]
    public void Registration_NormalizesValidInput()
    {
        var valid = IdentityInput.TryRegistration(
            "  Alice_01  ", "  ALICE@example.test ", "StrongPassword!123",
            out var username, out var email);

        Assert.True(valid);
        Assert.Equal("Alice_01", username);
        Assert.Equal("alice@example.test", email);
    }

    [Theory]
    [InlineData(null, "alice@example.test", "StrongPassword!123")]
    [InlineData("ab", "alice@example.test", "StrongPassword!123")]
    [InlineData("alice admin", "alice@example.test", "StrongPassword!123")]
    [InlineData("alice", "not-an-email", "StrongPassword!123")]
    [InlineData("alice", "alice@example.test", "short")]
    public void Registration_RejectsInvalidInput(
        string? username, string? email, string? password)
    {
        Assert.False(IdentityInput.TryRegistration(
            username, email, password, out _, out _));
    }

    [Fact]
    public void Registration_RejectsOversizedValues()
    {
        Assert.False(IdentityInput.TryRegistration(
            new string('a', 101), "alice@example.test", new string('x', 129), out _, out _));
    }

    [Theory]
    [InlineData(null, "password")]
    [InlineData("bad-email", "password")]
    [InlineData("alice@example.test", "")]
    public void Login_RejectsMalformedInput(string? email, string? password)
    {
        Assert.False(IdentityInput.TryLogin(email, password, out _));
    }

    [Fact]
    public void ProductionRefreshCookie_IsHttpOnlySecureStrictAndAuthScoped()
    {
        var expires = DateTimeOffset.UtcNow.AddDays(30);
        var options = RefreshCookieOptions(expires, isDevelopment: false);

        Assert.True(options.HttpOnly);
        Assert.True(options.Secure);
        Assert.Equal(SameSiteMode.Strict, options.SameSite);
        Assert.Equal("/api/v1/auth", options.Path);
        Assert.Equal(expires, options.Expires);
        Assert.True(options.IsEssential);
    }

    [Fact]
    public void DevelopmentRefreshCookie_OnlyRelaxesSecureTransportFlag()
    {
        var expires = DateTimeOffset.UtcNow.AddDays(30);
        var options = RefreshCookieOptions(expires, isDevelopment: true);

        Assert.True(options.HttpOnly);
        Assert.False(options.Secure);
        Assert.Equal(SameSiteMode.Strict, options.SameSite);
        Assert.Equal("/api/v1/auth", options.Path);
        Assert.True(options.IsEssential);
    }

    [Theory]
    [InlineData(null, false)]
    [InlineData("", false)]
    [InlineData("0", false)]
    [InlineData("true", false)]
    [InlineData("01", false)]
    [InlineData("1", true)]
    public void CsrfHeader_RequiresExactSentinel(string? value, bool expected)
    {
        var context = new DefaultHttpContext();
        if (value is not null)
            context.Request.Headers["X-FamilyChat-CSRF"] = value;

        Assert.Equal(expected, HasCsrfHeader(context.Request));
    }

    [Fact]
    public void RefreshCookieName_RemainsStableForClientAndLogoutCompatibility()
    {
        Assert.Equal("familychat.refresh", AuthCookies.RefreshToken);
    }

    static CookieOptions RefreshCookieOptions(DateTimeOffset expires, bool isDevelopment)
    {
        var method = ProgramMethod(typeof(CookieOptions), typeof(DateTimeOffset), typeof(bool));
        return Assert.IsType<CookieOptions>(method.Invoke(null, [expires, isDevelopment]));
    }

    static bool HasCsrfHeader(HttpRequest request)
    {
        var method = ProgramMethod(typeof(bool), typeof(HttpRequest));
        return Assert.IsType<bool>(method.Invoke(null, [request]));
    }

    static MethodInfo ProgramMethod(Type returnType, params Type[] parameterTypes)
    {
        var program = typeof(IdentityInput).Assembly.GetType("Program")
            ?? throw new InvalidOperationException("Identity top-level Program type was not found.");

        return program.GetMethods(BindingFlags.Static | BindingFlags.NonPublic)
            .Single(method =>
                method.ReturnType == returnType &&
                method.GetParameters().Select(parameter => parameter.ParameterType)
                    .SequenceEqual(parameterTypes));
    }
}
