public class IdentityInputTests
{
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
}
