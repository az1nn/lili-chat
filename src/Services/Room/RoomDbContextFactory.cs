using Microsoft.EntityFrameworkCore;
using Microsoft.EntityFrameworkCore.Design;

sealed class RoomDbContextFactory : IDesignTimeDbContextFactory<RoomDbContext>
{
    public RoomDbContext CreateDbContext(string[] args)
    {
        var options = new DbContextOptionsBuilder<RoomDbContext>()
            .UseNpgsql(DesignTimeConnection.Get("room"))
            .Options;
        return new RoomDbContext(options);
    }
}

static class DesignTimeConnection
{
    public static string Get(string database) =>
        Environment.GetEnvironmentVariable("ConnectionStrings__Default")
        ?? $"Host=localhost;Database={database};Username=app";
}
