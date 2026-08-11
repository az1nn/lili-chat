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
}
