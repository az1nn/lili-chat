# Validation notes

Checks executed locally and in GitHub Actions through 2026-08-11:

- `./scripts/check-structure.sh`: PASS
- `docker compose config --quiet`: PASS
- Compose services define readiness checks; CI uses `docker compose up --wait` before Playwright and k6
- All .NET runtime Dockerfiles select the built-in non-root `app` user; the structure gate enforces this
- `dotnet restore FamilyChat.sln`: PASS (NuGet audit disabled locally due feed latency)
- `dotnet tool restore`: PASS; repository-pinned `dotnet-ef` 8.0.11 restored
- `./scripts/check-migrations.sh`: PASS for Identity, Family Graph, Room, Message, and Notification using dedicated design-time factories; CI rejects unversioned model drift
- `dotnet build FamilyChat.sln --no-restore`: PASS, zero warnings
- `dotnet test FamilyChat.sln --no-build --no-restore`: PASS locally, 78 contract/authorization/security/input/retry/retention tests; Docker-backed migration test skipped locally by design
- NuGet vulnerability audit: no known vulnerable direct or transitive packages across runtime and test projects after upgrading OpenTelemetry to 1.17.0, pinning MessagePack 2.5.302, aligning EF Core/Npgsql EF/dotnet-ef on 8.0.11, and updating xUnit to 2.9.3 with its Visual Studio runner 3.1.5
- `npm ci`: PASS
- `npm run typecheck`: PASS
- `npm test`: PASS, 13 React/API/history/authorization tests across 5 files
- `npm run build`: PASS
- Nginx configuration uses the query-free `familychat_safe` access-log format; CI validates syntax with `nginx -t`
- `npx playwright test --list`: PASS, two-user E2E discovered and transpiled
- The E2E requires a live `accepted` → `persisted` acknowledgement before validating persistence after reload
- The E2E requires an actively connected removed member to be evicted from the SignalR room without reloading
- The E2E requires mute/unmute role changes to update an actively connected member's composer without reloading
- `node --check deploy/k6/smoke.js`: PASS; the smoke gate measures register, login, room creation, and authorized history independently
- Grafana dashboard JSON parse: PASS; message consistency, authentication-security, SignalR connection, and realtime dependency panels are provisioned
- Prometheus rules are mounted by Compose; CI validates configuration and PromQL with `promtool` 2.54.1
- Internal gRPC calls use three-second deadlines and controlled unavailable responses

Distributed runtime evidence:

- [GitHub Actions run 31449917275](https://github.com/az1nn/lili-chat/actions/runs/31449917275): PASS for both `validate` and `e2e` jobs.
- CI built every Docker image, applied startup migrations, and waited until the complete Compose stack was healthy.
- Playwright passed the two-user register → invite → SignalR message → persistence after reload → active access revocation flow.
- k6 passed registration, login, room creation, and authorized history smoke checks against the live stack.
- With `RUN_INTEGRATION_TESTS=1`, CI applied all five service migrations to isolated Testcontainers PostgreSQL databases and exercised advisory-lock exclusion, concurrent refresh replay, duplicate message persistence, and bounded deletion of expired messages.

Local runtime note:

- The workstation Docker daemon remains stopped, so the same distributed gates cannot currently be repeated locally. The remote Linux runner is the authoritative runtime evidence above.

Once Docker is available, run:

```bash
docker compose down --volumes # required once for databases created by the old EnsureCreated flow
docker compose up --build
docker compose --profile test run --rm k6
```

Then validate: register users A and B, wait for both `PublicId` projections, create a room as A, add B, exchange a SignalR message, refresh B, and confirm the message remains in history.
