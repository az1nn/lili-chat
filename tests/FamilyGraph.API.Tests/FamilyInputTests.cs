public class FamilyInputTests
{
    [Fact]
    public void Text_NormalizesValidValues()
    {
        Assert.True(FamilyInput.TryText("  Casa  ", "  Principal  ", out var name, out var description));
        Assert.Equal("Casa", name);
        Assert.Equal("Principal", description);
    }

    [Theory]
    [InlineData(null)]
    [InlineData("")]
    [InlineData("   ")]
    public void Text_RejectsMissingName(string? name) =>
        Assert.False(FamilyInput.TryText(name, null, out _, out _));

    [Fact]
    public void Text_RejectsOversizedValues()
    {
        Assert.False(FamilyInput.TryText(new string('x', 101), null, out _, out _));
        Assert.False(FamilyInput.TryText("family", new string('x', 1001), out _, out _));
    }

    [Theory]
    [InlineData("ABCD2345", true)]
    [InlineData(" abcd2345 ", true)]
    [InlineData("ABCDI345", false)]
    [InlineData("too-short", false)]
    [InlineData(null, false)]
    public void PublicId_ValidatesAlphabetAndLength(string? input, bool expected) =>
        Assert.Equal(expected, FamilyInput.TryPublicId(input, out _));
}
