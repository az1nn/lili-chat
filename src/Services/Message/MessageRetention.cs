using FamilyChat.ServiceDefaults;
using Microsoft.EntityFrameworkCore;

sealed record MessageRetentionPolicy(
    int Days,
    int BatchSize,
    int MaxBatchesPerRun,
    TimeSpan Interval)
{
    public DateTimeOffset Cutoff(DateTimeOffset now) => now.AddDays(-Days);

    public static MessageRetentionPolicy From(IConfiguration configuration)
    {
        var days = configuration.GetValue("MessageRetention:Days", 365);
        var batchSize = configuration.GetValue("MessageRetention:BatchSize", 1000);
        var maxBatches = configuration.GetValue("MessageRetention:MaxBatchesPerRun", 10);
        var intervalMinutes = configuration.GetValue("MessageRetention:IntervalMinutes", 360);

        if (days is < 1 or > 3650)
            throw new InvalidOperationException("MessageRetention:Days must be between 1 and 3650.");
        if (batchSize is < 1 or > 10000)
            throw new InvalidOperationException("MessageRetention:BatchSize must be between 1 and 10000.");
        if (maxBatches is < 1 or > 100)
            throw new InvalidOperationException("MessageRetention:MaxBatchesPerRun must be between 1 and 100.");
        if (intervalMinutes is < 1 or > 1440)
            throw new InvalidOperationException("MessageRetention:IntervalMinutes must be between 1 and 1440.");

        return new MessageRetentionPolicy(
            days, batchSize, maxBatches, TimeSpan.FromMinutes(intervalMinutes));
    }
}

sealed class MessageRetentionWorker(
    IServiceScopeFactory scopeFactory,
    MessageRetentionPolicy policy,
    ILogger<MessageRetentionWorker> logger) : BackgroundService
{
    protected override async Task ExecuteAsync(CancellationToken stoppingToken)
    {
        while (!stoppingToken.IsCancellationRequested)
        {
            try
            {
                await DeleteExpiredBatches(stoppingToken);
            }
            catch (Exception ex) when (!stoppingToken.IsCancellationRequested)
            {
                FamilyChatMetrics.MessageRetentionFailed.Add(1);
                logger.LogError(ex, "Message retention run failed");
            }

            await Task.Delay(policy.Interval, stoppingToken);
        }
    }

    async Task DeleteExpiredBatches(CancellationToken ct)
    {
        var cutoff = policy.Cutoff(DateTimeOffset.UtcNow);
        long total = 0;

        for (var batch = 0; batch < policy.MaxBatchesPerRun; batch++)
        {
            await using var scope = scopeFactory.CreateAsyncScope();
            var db = scope.ServiceProvider.GetRequiredService<MessageDbContext>();
            var deleted = await MessageRetention.DeleteBatchAsync(
                db, cutoff, policy.BatchSize, ct);
            if (deleted == 0) break;
            total += deleted;
            FamilyChatMetrics.MessageRetentionDeleted.Add(deleted);

            if (deleted < policy.BatchSize) break;
            await Task.Yield();
        }

        if (total > 0)
            logger.LogInformation("Deleted {MessageCount} messages older than {Cutoff}", total, cutoff);
    }
}

static class MessageRetention
{
    public static async Task<int> DeleteBatchAsync(
        MessageDbContext db,
        DateTimeOffset cutoff,
        int batchSize,
        CancellationToken ct)
    {
        var ids = await db.Messages.AsNoTracking()
            .Where(message => message.SentAt < cutoff)
            .OrderBy(message => message.SentAt)
            .Select(message => message.Id)
            .Take(batchSize)
            .ToListAsync(ct);

        if (ids.Count == 0) return 0;
        return await db.Messages
            .Where(message => ids.Contains(message.Id))
            .ExecuteDeleteAsync(ct);
    }
}
