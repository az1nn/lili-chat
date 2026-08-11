static class CorsOrigins
{
    public static string[] Load(IConfiguration configuration, bool isDevelopment)
    {
        var values = new List<string>();
        var scalar = configuration["Cors:AllowedOrigins"];
        if (!string.IsNullOrWhiteSpace(scalar))
            values.AddRange(scalar.Split(',', StringSplitOptions.TrimEntries | StringSplitOptions.RemoveEmptyEntries));
        values.AddRange(configuration.GetSection("Cors:AllowedOrigins").GetChildren()
            .Select(child => child.Value)
            .Where(value => !string.IsNullOrWhiteSpace(value))
            .Select(value => value!));
        if (values.Count == 0 && isDevelopment) values.Add("http://localhost:3000");
        if (values.Count == 0)
            throw new InvalidOperationException("Cors:AllowedOrigins is required outside Development.");

        return values.Select(value => Normalize(value, isDevelopment))
            .Distinct(StringComparer.OrdinalIgnoreCase)
            .ToArray();
    }

    static string Normalize(string value, bool isDevelopment)
    {
        if (!Uri.TryCreate(value, UriKind.Absolute, out var uri) ||
            (!uri.Scheme.Equals(Uri.UriSchemeHttps, StringComparison.OrdinalIgnoreCase) &&
             !(isDevelopment && uri.Scheme.Equals(Uri.UriSchemeHttp, StringComparison.OrdinalIgnoreCase))) ||
            !string.IsNullOrEmpty(uri.UserInfo) ||
            uri.AbsolutePath != "/" ||
            !string.IsNullOrEmpty(uri.Query) ||
            !string.IsNullOrEmpty(uri.Fragment))
            throw new InvalidOperationException(
                $"Invalid CORS origin '{value}'. Use an HTTPS origin without path, query, or wildcard.");

        return uri.GetLeftPart(UriPartial.Authority);
    }
}
