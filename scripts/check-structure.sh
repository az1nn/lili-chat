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
test -f src/Web/tests/e2e/chat-flow.spec.ts
test -f tests/Persistence.IntegrationTests/PostgresMigrationTests.cs
test -f tests/Identity.API.Tests/IdentityInputTests.cs
test -f tests/FamilyGraph.API.Tests/FamilyInputTests.cs
test -f deploy/grafana/provisioning/dashboards/family-chat.json
test -f deploy/prometheus/rules/family-chat.yml
rg -q '^rule_files:' deploy/prometheus/prometheus.yml
for svc in Identity FamilyGraph Room Message RealtimeHub Notification; do
  test -f "src/Services/$svc/Dockerfile"
  rg -q '^USER app$' "src/Services/$svc/Dockerfile"
done
rg -q '^USER app$' src/Gateway/Dockerfile
for svc in Identity FamilyGraph Room Message Notification; do
  test -n "$(find "src/Services/$svc/Migrations" -name '*_InitialCreate.cs' -type f -print -quit)"
  test -n "$(find "src/Services/$svc/Migrations" -name '*ModelSnapshot.cs' -type f -print -quit)"
done
if rg -q "EnsureCreated" src/Services -g '*.cs'; then
  echo "EnsureCreated não é permitido; use migrations versionadas" >&2
  exit 1
fi
echo "Estrutura OK"
