#!/usr/bin/env ruby
# frozen_string_literal: true

errors = []
check = lambda do |condition, message|
  errors << message unless condition
end

annotate = lambda do |message|
  escaped = message.to_s.gsub('%', '%25').gsub("\r", '%0D').gsub("\n", '%0A')
  warn "::error title=Security contract invalid::#{escaped}"
end

def endpoint_section(source, marker, next_markers)
  start = source.index(marker)
  return nil unless start

  endings = next_markers.filter_map { |next_marker| source.index(next_marker, start + marker.length) }
  finish = endings.min || source.length
  source[start...finish]
end

identity = File.read('src/Services/Identity/Program.cs')
gateway = File.read('src/Gateway/Program.cs')
realtime = File.read('src/Services/RealtimeHub/Program.cs')
service_defaults = File.read('src/Shared/FamilyChat.ServiceDefaults/ServiceDefaults.cs')

# Refresh tokens must stay inaccessible to JavaScript and scoped to auth endpoints.
check.call(identity.include?('HttpOnly = true'), 'refresh cookie must remain HttpOnly')
check.call(identity.include?('Secure = !isDevelopment'), 'refresh cookie must be Secure outside Development')
check.call(identity.include?('SameSite = SameSiteMode.Strict'), 'refresh cookie must remain SameSite=Strict')
check.call(identity.include?('Path = "/api/v1/auth"'), 'refresh cookie must remain scoped to /api/v1/auth')
check.call(identity.include?('IsEssential = true'), 'refresh cookie must remain essential for authentication')
check.call(identity.include?('public const string RefreshToken = "familychat.refresh"'),
           'refresh cookie name must remain familychat.refresh')

# Cookie-backed state-changing auth routes require the explicit CSRF sentinel.
refresh = endpoint_section(identity, 'app.MapPost("/api/v1/auth/refresh"',
                           ['app.MapPost("/api/v1/auth/logout"'])
logout = endpoint_section(identity, 'app.MapPost("/api/v1/auth/logout"',
                          ['app.MapDelete("/api/v1/auth/account"'])
account = endpoint_section(identity, 'app.MapDelete("/api/v1/auth/account"', ['await app.RunAsync();'])
check.call(refresh&.include?('HasCsrfHeader(http.Request)'), 'refresh endpoint must require CSRF header')
check.call(logout&.include?('HasCsrfHeader(http.Request)'), 'logout endpoint must require CSRF header')
check.call(account&.include?('HasCsrfHeader(http.Request)'), 'account deletion must require CSRF header')
check.call(identity.include?('request.Headers.TryGetValue("X-FamilyChat-CSRF"') &&
           identity.include?('value == "1"'),
           'CSRF protection must require the exact X-FamilyChat-CSRF: 1 sentinel')

# Gateway CORS must stay origin-specific because credentials are forwarded.
check.call(gateway.include?('.WithOrigins(allowedOrigins)'), 'gateway CORS must use the validated origin allowlist')
check.call(gateway.include?('.AllowCredentials()'), 'gateway CORS must keep credential support for refresh cookies')
check.call(!gateway.include?('.AllowAnyOrigin()'), 'gateway must never combine credentials with AllowAnyOrigin')
check.call(gateway.include?('if (!app.Environment.IsDevelopment()) app.UseHsts();'),
           'gateway must keep HSTS enabled outside Development')

# SignalR must authenticate every hub connection and accept query tokens only on the hub path.
check.call(realtime.match?(/\[Authorize\]\s*class ChatHub/), 'ChatHub must remain protected by [Authorize]')
check.call(realtime.include?('context.Request.Query["access_token"]'),
           'SignalR JWT handler must read the access_token query parameter')
check.call(realtime.include?('Request.Path.StartsWithSegments("/hubs/chat")'),
           'query-string access tokens must only be accepted for /hubs/chat')
check.call(realtime.include?('ValidateLifetime = true'), 'SignalR JWT validation must validate token lifetime')
check.call(realtime.include?('IssuerSigningKey = JwtKeyFactory.ValidationKey'),
           'SignalR must use the JWT validation/public key path')

# Internal gRPC authentication must keep minimum entropy and fixed-time comparison.
check.call(service_defaults.include?('Encoding.UTF8.GetByteCount(token) < 32'),
           'internal service tokens must require at least 32 bytes')
check.call(service_defaults.include?('providedBytes.Length == expectedBytes.Length'),
           'internal token comparison must reject length mismatches')
check.call(service_defaults.include?('CryptographicOperations.FixedTimeEquals'),
           'internal service tokens must use fixed-time comparison')
check.call(service_defaults.include?('StatusCodes.Status401Unauthorized'),
           'invalid internal gRPC tokens must remain unauthorized')

# Production JWT key handling must not silently fall back to the development symmetric secret.
check.call(service_defaults.include?('if (!environment.IsDevelopment())') &&
           service_defaults.include?('Production requires JWT:PrivateKeyBase64'),
           'production JWT validation/signing must fail fast without RSA key material')
check.call(service_defaults.include?('key is RsaSecurityKey ? SecurityAlgorithms.RsaSha256'),
           'RSA JWT keys must continue using RS256')

if errors.empty?
  puts 'Security contracts validated successfully.'
  exit 0
end

errors.each { |message| annotate.call(message) }
warn "#{errors.length} security contract violation(s) found."
exit 1
