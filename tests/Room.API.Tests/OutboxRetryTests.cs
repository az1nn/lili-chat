using FamilyChat.ServiceDefaults;

public class OutboxRetryTests
{
    [Theory]
    [InlineData(1, 2)]
    [InlineData(4, 16)]
    [InlineData(8, 256)]
    [InlineData(9, 300)]
    [InlineData(50, 300)]
    public void Delay_UsesExponentialBackoffWithFiveMinuteCap(
        int attempt, int expectedSeconds)
    {
        Assert.Equal(TimeSpan.FromSeconds(expectedSeconds), OutboxRetry.Delay(attempt));
    }
}
