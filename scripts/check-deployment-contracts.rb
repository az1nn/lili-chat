#!/usr/bin/env ruby
# frozen_string_literal: true

require 'json'
require 'yaml'

errors = []
check = lambda do |condition, message|
  errors << message unless condition
end

annotate = lambda do |message|
  escaped = message.to_s.gsub('%', '%25').gsub("\r", '%0D').gsub("\n", '%0A')
  warn "::error title=Deployment contract invalid::#{escaped}"
end

def env_var(service, key)
  Array(service['envVars']).find { |entry| entry['key'] == key }
end

def generated?(service, key)
  env_var(service, key)&.fetch('generateValue', false) == true
end

def manual_secret?(service, key)
  env_var(service, key)&.fetch('sync', true) == false
end

def from_service?(service, key, type:, name:, property: nil, env_var_key: nil)
  source = env_var(service, key)&.fetch('fromService', nil)
  return false unless source.is_a?(Hash)
  return false unless source['type'] == type && source['name'] == name
  return false if property && source['property'] != property
  return false if env_var_key && source['envVarKey'] != env_var_key

  true
end

def from_database?(service, key, name)
  source = env_var(service, key)&.fetch('fromDatabase', nil)
  source.is_a?(Hash) && source['name'] == name && source['property'] == 'connectionString'
end

begin
  render = YAML.safe_load(File.read('render.yaml'), aliases: true)
rescue StandardError => e
  annotate.call("render.yaml could not be parsed: #{e.message}")
  exit 1
end

services = Array(render['services'])
service_by_name = services.to_h { |service| [service['name'], service] }
expected_services = %w[
  rabbitmq redis identity-svc family-svc room-svc message-svc
  realtime-hub notification-svc gateway
]
check.call(service_by_name.keys.sort == expected_services.sort,
           "render.yaml service set changed: expected #{expected_services.sort.join(', ')}")
check.call(['off', false].include?(render.dig('previews', 'generation')),
           'Render preview environments must stay disabled by default')

services.each do |service|
  name = service['name'] || '<unnamed>'
  check.call(service['region'] == 'oregon', "#{name} must stay in the oregon region")
end

docker_services = services.reject { |service| service['type'] == 'keyvalue' }
docker_services.each do |service|
  name = service['name'] || '<unnamed>'
  check.call(service['runtime'] == 'docker', "#{name} must use the Docker runtime")
  check.call(service['autoDeployTrigger'] == 'checksPass',
             "#{name} must deploy only after repository checks pass")
end

gateway = service_by_name['gateway'] || {}
check.call(gateway['type'] == 'web', 'gateway must be the only public Render web service')
check.call(gateway['healthCheckPath'] == '/health', 'gateway must expose /health to Render')
check.call(services.count { |service| service['type'] == 'web' } == 1,
           'only gateway may be a public Render web service')
check.call(manual_secret?(gateway, 'Cors__AllowedOrigins'),
           'gateway CORS origins must be supplied explicitly at deploy time')
check.call(env_var(gateway, 'ASPNETCORE_ENVIRONMENT')&.dig('value') == 'Production',
           'gateway must run with ASPNETCORE_ENVIRONMENT=Production')

private_dotnet = %w[identity-svc family-svc room-svc message-svc realtime-hub notification-svc]
private_dotnet.each do |name|
  service = service_by_name[name] || {}
  check.call(service['type'] == 'pserv', "#{name} must remain a private service")
  check.call(env_var(service, 'ASPNETCORE_ENVIRONMENT')&.dig('value') == 'Production',
             "#{name} must run with ASPNETCORE_ENVIRONMENT=Production")
end

expected_gateway_hosts = {
  'Render__IdentityHost' => 'identity-svc',
  'Render__FamilyHost' => 'family-svc',
  'Render__RoomHost' => 'room-svc',
  'Render__MessageHost' => 'message-svc',
  'Render__RealtimeHost' => 'realtime-hub'
}
expected_gateway_hosts.each do |key, target|
  check.call(from_service?(gateway, key, type: 'pserv', name: target, property: 'host'),
             "gateway #{key} must resolve the private host for #{target}")
end

identity = service_by_name['identity-svc'] || {}
check.call(manual_secret?(identity, 'JWT__PrivateKeyBase64'),
           'identity private JWT key must never be committed or generated in the Blueprint')
check.call(manual_secret?(identity, 'JWT__PublicKeyBase64'),
           'identity public JWT key must be supplied alongside the private key')

%w[family-svc room-svc message-svc realtime-hub].each do |name|
  service = service_by_name[name] || {}
  check.call(from_service?(service, 'JWT__PublicKeyBase64', type: 'pserv', name: 'identity-svc',
                           env_var_key: 'JWT__PublicKeyBase64'),
             "#{name} must consume JWT__PublicKeyBase64 from identity-svc")
end

family = service_by_name['family-svc'] || {}
room = service_by_name['room-svc'] || {}
message = service_by_name['message-svc'] || {}
realtime = service_by_name['realtime-hub'] || {}
notification = service_by_name['notification-svc'] || {}

check.call(generated?(family, 'InternalAuth__Token'), 'family-svc internal token must be generated')
check.call(generated?(room, 'InternalAuth__Token'), 'room-svc internal token must be generated')
check.call(from_service?(room, 'InternalAuth__FamilyToken', type: 'pserv', name: 'family-svc',
                         env_var_key: 'InternalAuth__Token'),
           'room-svc must consume family-svc internal token')
check.call(from_service?(room, 'Render__FamilyHost', type: 'pserv', name: 'family-svc', property: 'host'),
           'room-svc must resolve family-svc private host')

{
  'message-svc' => message,
  'realtime-hub' => realtime,
  'notification-svc' => notification
}.each do |name, service|
  check.call(from_service?(service, 'InternalAuth__RoomToken', type: 'pserv', name: 'room-svc',
                           env_var_key: 'InternalAuth__Token'),
             "#{name} must consume room-svc internal token")
  check.call(from_service?(service, 'Render__RoomHost', type: 'pserv', name: 'room-svc', property: 'host'),
             "#{name} must resolve room-svc private host")
end

expected_databases = {
  'identity-svc' => 'identity-db',
  'family-svc' => 'family-db',
  'room-svc' => 'room-db',
  'message-svc' => 'message-db',
  'notification-svc' => 'notification-db'
}
expected_databases.each do |service_name, database_name|
  service = service_by_name[service_name] || {}
  check.call(from_database?(service, 'ConnectionStrings__Default', database_name),
             "#{service_name} must use #{database_name} through its private connection string")
end

rabbitmq = service_by_name['rabbitmq'] || {}
check.call(rabbitmq['type'] == 'pserv', 'RabbitMQ must remain a private service')
check.call(rabbitmq.dig('disk', 'mountPath') == '/var/lib/rabbitmq',
           'RabbitMQ data must stay on a persistent disk')
check.call(rabbitmq.dig('disk', 'sizeGB').to_i >= 1, 'RabbitMQ persistent disk must be at least 1 GB')
check.call(generated?(rabbitmq, 'RABBITMQ_DEFAULT_PASS'), 'RabbitMQ password must be generated')

%w[identity-svc family-svc room-svc message-svc realtime-hub notification-svc].each do |name|
  service = service_by_name[name] || {}
  check.call(from_service?(service, 'RabbitMQ__Host', type: 'pserv', name: 'rabbitmq', property: 'host'),
             "#{name} must resolve RabbitMQ through the private network")
  check.call(from_service?(service, 'RabbitMQ__User', type: 'pserv', name: 'rabbitmq',
                           env_var_key: 'RABBITMQ_DEFAULT_USER'),
             "#{name} must consume RabbitMQ username from rabbitmq")
  check.call(from_service?(service, 'RabbitMQ__Pass', type: 'pserv', name: 'rabbitmq',
                           env_var_key: 'RABBITMQ_DEFAULT_PASS'),
             "#{name} must consume RabbitMQ password from rabbitmq")
end

redis = service_by_name['redis'] || {}
check.call(redis['type'] == 'keyvalue', 'redis must remain a Render Key Value service')
check.call(redis['ipAllowList'] == [], 'redis must not expose a public IP allow list')
check.call(redis['maxmemoryPolicy'] == 'noeviction', 'redis must keep noeviction semantics')
check.call(from_service?(realtime, 'Redis__Connection', type: 'keyvalue', name: 'redis',
                         property: 'connectionString'),
           'realtime-hub must consume the private Redis connection string')

check.call(env_var(notification, 'Notifications__Provider')&.dig('value') == 'Disabled',
           'production notifications must remain disabled until explicitly configured')
check.call(env_var(notification, 'Notifications__AllowStub')&.dig('value') == 'false',
           'notification stub provider must be disabled in production')
check.call(env_var(notification, 'Notifications__IncludeContent')&.dig('value') == 'false',
           'notification content must remain excluded by default')

databases = Array(render['databases'])
database_by_name = databases.to_h { |database| [database['name'], database] }
expected_database_names = expected_databases.values.sort
check.call(database_by_name.keys.sort == expected_database_names,
           "Render database set changed: expected #{expected_database_names.join(', ')}")
databases.each do |database|
  name = database['name'] || '<unnamed>'
  check.call(database['region'] == 'oregon', "#{name} must stay in the oregon region")
  check.call(database['postgresMajorVersion'].to_s == '16', "#{name} must stay on PostgreSQL 16")
  check.call(database['ipAllowList'] == [], "#{name} must not be publicly allow-listed")
end

begin
  vercel = JSON.parse(File.read('src/Web/vercel.json'))
rescue StandardError => e
  annotate.call("src/Web/vercel.json could not be parsed: #{e.message}")
  exit 1
end

check.call(vercel['framework'] == 'vite', 'Vercel framework must remain Vite')
check.call(vercel['buildCommand'] == 'npm run build', 'Vercel must build with npm run build')
check.call(vercel['outputDirectory'] == 'dist', 'Vercel output directory must remain dist')
rewrites = Array(vercel['rewrites'])
check.call(rewrites.any? { |rule| rule['source'] == '/(.*)' && rule['destination'] == '/index.html' },
           'Vercel must preserve the SPA fallback to /index.html')

headers = Array(vercel['headers'])
all_route = headers.find { |entry| entry['source'] == '/(.*)' } || {}
security_headers = Array(all_route['headers']).to_h { |header| [header['key'], header['value']] }
{
  'X-Content-Type-Options' => 'nosniff',
  'X-Frame-Options' => 'DENY',
  'Referrer-Policy' => 'no-referrer'
}.each do |key, value|
  check.call(security_headers[key] == value, "Vercel #{key} header must remain #{value}")
end
csp = security_headers['Content-Security-Policy'].to_s
check.call(csp.include?("default-src 'self'"), "Vercel CSP must default to 'self'")
check.call(csp.include?("frame-ancestors 'none'"), 'Vercel CSP must block framing')
check.call(csp.include?('connect-src') && csp.include?('https:') && csp.include?('wss:'),
           'Vercel CSP must allow HTTPS API calls and secure SignalR WebSockets')

assets = headers.find { |entry| entry['source'] == '/assets/(.*)' } || {}
asset_headers = Array(assets['headers']).to_h { |header| [header['key'], header['value']] }
check.call(asset_headers['Cache-Control'] == 'public, max-age=31536000, immutable',
           'hashed Vite assets must retain immutable one-year caching')

env_example = File.read('src/Web/.env.example')
check.call(env_example.match?(/^VITE_API_URL=\S+/),
           'src/Web/.env.example must document VITE_API_URL')

if errors.empty?
  puts 'Deployment contracts validated successfully.'
  exit 0
end

errors.each { |message| annotate.call(message) }
warn "#{errors.length} deployment contract violation(s) found."
exit 1
