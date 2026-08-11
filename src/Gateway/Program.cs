using System.Threading.RateLimiting;
using FamilyChat.ServiceDefaults;
using Microsoft.AspNetCore.Server.Kestrel.Core;

var builder = WebApplication.CreateBuilder(args);
builder.WebHost.ConfigureKestrel(options =>
    options.Limits.MaxRequestBodySize = 64 * 1024);
builder.Logging.AddFilter("Microsoft.AspNetCore.Hosting.Diagnostics", LogLevel.Warning);
builder.AddFamilyChatObservability("gateway");

builder.Services.AddReverseProxy()
    .LoadFromConfig(builder.Configuration.GetSection("ReverseProxy"));

var allowedOrigins = CorsOrigins.Load(builder.Configuration, builder.Environment.IsDevelopment());
builder.Services.AddCors(o => o.AddDefaultPolicy(p => p
    .WithOrigins(allowedOrigins)
    .AllowAnyHeader()
    .AllowAnyMethod()
    .AllowCredentials()));

builder.Services.AddRateLimiter(options =>
{
    options.GlobalLimiter = PartitionedRateLimiter.Create<HttpContext, string>(ctx =>
    {
        var (policy, permitLimit, queueLimit) = RateLimitPolicy(ctx.Request.Path);
        var ip = ctx.Connection.RemoteIpAddress?.ToString() ?? "unknown";
        return RateLimitPartition.GetFixedWindowLimiter(
            $"{policy}:{ip}",
            _ => new FixedWindowRateLimiterOptions
            {
                PermitLimit = permitLimit,
                Window = TimeSpan.FromMinutes(1),
                QueueLimit = queueLimit,
                QueueProcessingOrder = QueueProcessingOrder.OldestFirst,
                AutoReplenishment = true
            });
    });
    options.RejectionStatusCode = StatusCodes.Status429TooManyRequests;
});

var app = builder.Build();
if (!app.Environment.IsDevelopment()) app.UseHsts();
app.Use(async (context, next) =>
{
    context.Response.Headers["X-Content-Type-Options"] = "nosniff";
    context.Response.Headers["X-Frame-Options"] = "DENY";
    context.Response.Headers["Referrer-Policy"] = "no-referrer";
    await next();
});
app.UseCors();
app.UseRateLimiter();
app.MapHealthChecks("/health");
app.MapReverseProxy();
await app.RunAsync();

static (string Policy, int PermitLimit, int QueueLimit) RateLimitPolicy(PathString path)
{
    var value = path.Value ?? "";
    if (value is "/api/v1/auth/login" or "/api/v1/auth/register") return ("auth", 10, 0);
    if (value == "/api/v1/auth/refresh") return ("refresh", 30, 0);
    if (value.StartsWith("/api/v1/users/by-public-id/", StringComparison.OrdinalIgnoreCase))
        return ("public-id", 60, 0);
    if (value.StartsWith("/api/v1/messages/room/", StringComparison.OrdinalIgnoreCase))
        return ("history", 120, 10);
    if (value.StartsWith("/hubs/chat", StringComparison.OrdinalIgnoreCase))
        return ("signalr-connect", 60, 0);
    return ("global", 300, 20);
}
