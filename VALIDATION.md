# Validation notes

Checks executed in the development environment on 2026-08-10:

- `./scripts/check-structure.sh`: PASS
- `docker compose config --quiet`: PASS
- Compose services define readiness checks; CI uses `docker compose up --wait` before Playwright and k6
- All .NET runtime Dockerfiles select the built-in non-root `app` user; the structure gate enforces this
- `dotnet restore FamilyChat.sln`: PASS (NuGet audit disabled locally due feed latency)
- `dotnet tool restore`: PASS; repository-pinned `dotnet-ef` 8.0.4 restored
- `dotnet ef migrations has-pending-model-changes`: PASS for Identity, Family Graph, Room, Message, and Notification
- `dotnet build FamilyChat.sln --no-restore`: PASS, zero warnings
- `dotnet test FamilyChat.sln --no-build --no-restore`: PASS, 68 contract/authorization/security/input/retry tests; Docker-backed migration test skipped locally by design
- `npm ci`: PASS
- `npm run typecheck`: PASS
- `npm test`: PASS, 3 React/API authentication tests across 2 files
- `npm run build`: PASS
- `npx playwright test --list`: PASS, two-user E2E discovered and transpiled
- The E2E requires a live `accepted` → `persisted` acknowledgement before validating persistence after reload
- The E2E requires an actively connected removed member to be evicted from the SignalR room without reloading
- `node --check deploy/k6/smoke.js`: PASS; the smoke gate measures register, login, room creation, and authorized history independently
- Grafana dashboard JSON parse: PASS; message consistency, authentication-security, SignalR connection, and realtime dependency panels are provisioned
- Prometheus rules are mounted by Compose; CI validates configuration and PromQL with `promtool` 2.54.1
- Internal gRPC calls use three-second deadlines and controlled unavailable responses

Runtime checks still required:

- `docker compose build/up`: not executed because the host Docker daemon is stopped and requires administrator credentials to start.
- Full execution of the Playwright two-user E2E and k6 smoke tests depends on the running Compose stack.
- Migration application during full service startup depends on the running Compose stack.
- `RUN_INTEGRATION_TESTS=1 dotnet test tests/Persistence.IntegrationTests` depends on Docker; CI executes this gate against five isolated PostgreSQL databases, verifies advisory-lock exclusion, concurrent refresh replay, and duplicate message persistence with one history/outbox row.

Once Docker is available, run:

```bash
docker compose down --volumes # required once for databases created by the old EnsureCreated flow
docker compose up --build
docker compose --profile test run --rm k6
```

Then validate: register users A and B, wait for both `PublicId` projections, create a room as A, add B, exchange a SignalR message, refresh B, and confirm the message remains in history.
