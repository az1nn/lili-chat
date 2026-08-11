using Microsoft.Extensions.Configuration;

namespace Message.API.Tests;

public class MessageRetentionPolicyTests
{
    [Fact]
    public void DefaultsBoundWorkAndRetainMessagesForOneYear()
    {
        var policy = MessageRetentionPolicy.From(Configuration());
        var now = new DateTimeOffset(2026, 8, 11, 12, 0, 0, TimeSpan.Zero);

        Assert.Equal(365, policy.Days);
        Assert.Equal(1000, policy.BatchSize);
        Assert.Equal(10, policy.MaxBatchesPerRun);
        Assert.Equal(TimeSpan.FromHours(6), policy.Interval);
        Assert.Equal(now.AddDays(-365), policy.Cutoff(now));
    }

    [Fact]
    public void ReadsExplicitRetentionControls()
    {
        var policy = MessageRetentionPolicy.From(Configuration(new()
        {
            ["MessageRetention:Days"] = "30",
            ["MessageRetention:BatchSize"] = "250",
            ["MessageRetention:MaxBatchesPerRun"] = "4",
            ["MessageRetention:IntervalMinutes"] = "60"
        }));

        Assert.Equal(30, policy.Days);
        Assert.Equal(250, policy.BatchSize);
        Assert.Equal(4, policy.MaxBatchesPerRun);
        Assert.Equal(TimeSpan.FromHours(1), policy.Interval);
    }

    [Theory]
    [InlineData("MessageRetention:Days", "0")]
    [InlineData("MessageRetention:Days", "3651")]
    [InlineData("MessageRetention:BatchSize", "0")]
    [InlineData("MessageRetention:BatchSize", "10001")]
    [InlineData("MessageRetention:MaxBatchesPerRun", "101")]
    [InlineData("MessageRetention:IntervalMinutes", "1441")]
    public void RejectsUnsafeBounds(string key, string value)
    {
        var error = Assert.Throws<InvalidOperationException>(() =>
            MessageRetentionPolicy.From(Configuration(new() { [key] = value })));

        Assert.Contains(key, error.Message);
    }

    static IConfiguration Configuration(Dictionary<string, string?>? values = null) =>
        new ConfigurationBuilder()
            .AddInMemoryCollection(values ?? new Dictionary<string, string?>())
            .Build();
}
