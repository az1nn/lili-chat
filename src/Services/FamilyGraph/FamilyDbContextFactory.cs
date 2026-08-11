using Microsoft.EntityFrameworkCore;
using Microsoft.EntityFrameworkCore.Design;

sealed class FamilyDbContextFactory : IDesignTimeDbContextFactory<FamilyDbContext>
{
    public FamilyDbContext CreateDbContext(string[] args)
    {
        var options = new DbContextOptionsBuilder<FamilyDbContext>()
            .UseNpgsql(DesignTimeConnection.Get("familygraph"))
            .Options;
        return new FamilyDbContext(options);
    }
}

static class DesignTimeConnection
{
    public static string Get(string database) =>
        Environment.GetEnvironmentVariable("ConnectionStrings__Default")
        ?? $"Host=localhost;Database={database};Username=app";
}
