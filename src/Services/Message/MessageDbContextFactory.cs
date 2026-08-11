using Microsoft.EntityFrameworkCore;
using Microsoft.EntityFrameworkCore.Design;

sealed class MessageDbContextFactory : IDesignTimeDbContextFactory<MessageDbContext>
{
    public MessageDbContext CreateDbContext(string[] args)
    {
        var options = new DbContextOptionsBuilder<MessageDbContext>()
            .UseNpgsql(DesignTimeConnection.Get("message"))
            .Options;
        return new MessageDbContext(options);
    }
}

static class DesignTimeConnection
{
    public static string Get(string database) =>
        Environment.GetEnvironmentVariable("ConnectionStrings__Default")
        ?? $"Host=localhost;Database={database};Username=app";
}
