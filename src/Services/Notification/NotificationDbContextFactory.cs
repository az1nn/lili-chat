using Microsoft.EntityFrameworkCore;
using Microsoft.EntityFrameworkCore.Design;

sealed class NotificationDbContextFactory : IDesignTimeDbContextFactory<NotificationDbContext>
{
    public NotificationDbContext CreateDbContext(string[] args)
    {
        var options = new DbContextOptionsBuilder<NotificationDbContext>()
            .UseNpgsql(DesignTimeConnection.Get("notification"))
            .Options;
        return new NotificationDbContext(options);
    }
}

static class DesignTimeConnection
{
    public static string Get(string database) =>
        Environment.GetEnvironmentVariable("ConnectionStrings__Default")
        ?? $"Host=localhost;Database={database};Username=app";
}
