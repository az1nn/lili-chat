public class RoomInputTests
{
    [Fact]
    public void Text_NormalizesValidValues()
    {
        Assert.True(RoomInput.TryText("  Família  ", "  Conversas  ", out var name, out var description));
        Assert.Equal("Família", name);
        Assert.Equal("Conversas", description);
    }

    [Theory]
    [InlineData(null)]
    [InlineData("")]
    [InlineData("   ")]
    public void Text_RejectsMissingName(string? name)
    {
        Assert.False(RoomInput.TryText(name, null, out _, out _));
    }

    [Fact]
    public void Text_RejectsOversizedValues()
    {
        Assert.False(RoomInput.TryText(new string('x', 101), null, out _, out _));
        Assert.False(RoomInput.TryText("room", new string('x', 1001), out _, out _));
    }

    [Theory]
    [InlineData("ABCD2345", true)]
    [InlineData(" abcd2345 ", true)]
    [InlineData("ABCDO345", false)]
    [InlineData("short", false)]
    [InlineData(null, false)]
    public void PublicId_ValidatesAlphabetAndLength(string? input, bool expected)
    {
        Assert.Equal(expected, RoomInput.TryPublicId(input, out _));
    }

    [Theory]
    [InlineData(null, true, "Member")]
    [InlineData("", true, "Member")]
    [InlineData(" Member ", true, "Member")]
    [InlineData("Admin", true, "Admin")]
    [InlineData("Muted", true, "Muted")]
    [InlineData("Owner", false, "Owner")]
    [InlineData("admin", false, "admin")]
    public void Role_RejectsUnknownValuesAndDefaultsOnlyMissingInput(
        string? input, bool expected, string expectedRole)
    {
        Assert.Equal(expected, RoomInput.TryRole(input, out var role));
        Assert.Equal(expectedRole, role);
    }
}
