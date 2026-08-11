#!/usr/bin/env sh
set -eu
test -f docker-compose.yml
test -f FamilyChat.sln
test -f .config/dotnet-tools.json
test -f src/Web/package.json
test -f src/Web/package-lock.json
test -f src/Web/playwright.config.ts
test -f src/Web/src/App.test.tsx
test -f src/Web/src/api.test.ts
grep -q '^log_format familychat_safe ' src/Web/nginx.conf
grep -q '^access_log .* familychat_safe;' src/Web/nginx.conf
if grep -Eq '\$(request_uri|args|query_string)([^A-Za-z_]|$)' src/Web/nginx.conf; then
  echo "Nginx não pode registrar query strings; elas podem conter o token SignalR" >&2
  exit 1
fi
test -f src/Web/tests/e2e/chat-flow.spec.ts
test -f src/Web/src/profileProjection.test.ts
test -f tests/Persistence.IntegrationTests/PostgresMigrationTests.cs
test -x scripts/check-migrations.sh
test -x scripts/backup-databases.sh
test -x scripts/restore-databases.sh
grep -q '_restore_\[a-z0-9_\]' scripts/restore-databases.sh
test -f tests/Identity.API.Tests/IdentityInputTests.cs
test -f tests/FamilyGraph.API.Tests/FamilyInputTests.cs
test -f tests/Message.API.Tests/MessageRetentionPolicyTests.cs
test -f tests/Notification.API.Tests/NotificationDeliveryTests.cs
test -f scripts/check-notification-delivery.mjs
test -f deploy/grafana/provisioning/dashboards/family-chat.json
test -f deploy/prometheus/rules/family-chat.yml
grep -q '^rule_files:' deploy/prometheus/prometheus.yml
for svc in Identity FamilyGraph Room Message RealtimeHub Notification; do
  test -f "src/Services/$svc/Dockerfile"
  grep -q '^USER app$' "src/Services/$svc/Dockerfile"
done
grep -q '^USER app$' src/Gateway/Dockerfile
for svc in Identity FamilyGraph Room Message Notification; do
  test -n "$(find "src/Services/$svc" -maxdepth 1 -name '*DbContextFactory.cs' -type f -print -quit)"
  test -n "$(find "src/Services/$svc/Migrations" -name '*_InitialCreate.cs' -type f -print -quit)"
  test -n "$(find "src/Services/$svc/Migrations" -name '*ModelSnapshot.cs' -type f -print -quit)"
done
if find src/Services -name '*.cs' -type f -exec grep -q "EnsureCreated" {} \; -print -quit | grep -q .; then
  echo "EnsureCreated não é permitido; use migrations versionadas" >&2
  exit 1
fi
echo "Estrutura OK"
