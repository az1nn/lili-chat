using Microsoft.EntityFrameworkCore;
using Microsoft.EntityFrameworkCore.Design;

sealed class IdentityDbContextFactory : IDesignTimeDbContextFactory<IdentityDbContext>
{
    public IdentityDbContext CreateDbContext(string[] args)
    {
        var options = new DbContextOptionsBuilder<IdentityDbContext>()
            .UseNpgsql(DesignTimeConnection.Get("identity"))
            .Options;
        return new IdentityDbContext(options);
    }
}

static class DesignTimeConnection
{
    public static string Get(string database) =>
        Environment.GetEnvironmentVariable("ConnectionStrings__Default")
        ?? $"Host=localhost;Database={database};Username=app";
}
