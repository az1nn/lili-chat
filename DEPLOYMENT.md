# Family Chat: Changes and Deployment Guide

This guide summarizes the current implementation and explains how to run it locally or deploy it with Vercel and Render. Production deployment is a distributed topology, not a single-container upload.

## What changed

The repository now provides an executable MVP with:

- isolated PostgreSQL ownership for Identity, Family Graph, Room, Message, and Notification;
- EF Core migrations, drift checks, transactional outboxes, idempotent consumers, and verified backup/restore drills;
- short-lived JWT access tokens in memory and rotating refresh tokens in `HttpOnly`, `Secure` cookies;
- room roles, member removal, persistent cursor-based history, optimistic message reconciliation, SignalR reconnect/access revocation, and visible `PublicId` projection polling;
- authenticated internal gRPC, RSA JWT support, endpoint-specific rate limits, request limits, CSP/security headers, and configurable production CORS origins;
- SMTP notifications with per-recipient delivery state, bounded retries, privacy-safe previews, metrics, and alerts;
- password-confirmed account deletion propagated through an outbox event, with local tombstones preventing delayed events from restoring personal data; rooms are revoked, authored message content is erased, and notification addresses are removed while idempotency ledgers remain;
- unit, contract, Testcontainers integration, Playwright E2E, k6, migration, Compose, Prometheus, and backup gates in CI.

## Local setup: WSL2 and Docker Desktop

Microsoft documents WSL installation with `wsl --install`; Docker documents enabling its WSL 2 engine and distribution integration. See the official [WSL installation guide](https://learn.microsoft.com/windows/wsl/install) and [Docker Desktop WSL guide](https://docs.docker.com/desktop/features/wsl/).

1. In an elevated PowerShell terminal, install WSL and reboot if requested:

   ```powershell
   wsl --install -d Ubuntu
   ```

2. Install Docker Desktop, enable **Use the WSL 2 based engine**, then enable integration for Ubuntu. Inside Ubuntu, verify:

   ```bash
   docker info
   docker compose version
   ```

3. Clone and start the application from the Linux filesystem, not `/mnt/c`, for better I/O:

   ```bash
   git clone https://github.com/az1nn/lili-chat.git
   cd lili-chat
   cp .env.example .env
   docker compose config --quiet
   docker compose up --detach --build --wait
   ```

4. Open `http://localhost:3000`. Gateway is on `:5000`, RabbitMQ on `:15672`, Jaeger on `:16686`, Prometheus on `:9090`, and Grafana on `:3001`.

5. Run the live gates:

   ```bash
   cd src/Web && npm ci && npm run test:e2e && cd ../..
   docker compose --profile test run --rm k6
   ```

To inspect email locally, set the following values in `.env`, start with `COMPOSE_PROFILES=notification-test docker compose up --detach --build --wait`, and open `http://localhost:8025`:

```dotenv
NOTIFICATION_PROVIDER=Smtp
SMTP_HOST=mailpit
SMTP_PORT=1025
SMTP_ENABLE_SSL=false
SMTP_FROM=notifications@familychat.test
```

Stop with `docker compose down`. Add `--volumes` only when intentionally deleting all local databases.

## Recommended cloud topology

Use two HTTPS custom domains under the same registrable domain:

```text
app.chat.example  → Vercel frontend
api.chat.example  → Render Gateway
                       ├─ private .NET services
                       ├─ five owned PostgreSQL databases
                       ├─ RabbitMQ
                       └─ Redis-compatible Key Value
```

Using sibling domains keeps the refresh-cookie flow same-site. Default `*.vercel.app` and `*.onrender.com` hosts are cross-site; do not rely on third-party-cookie behavior for authentication.

## Frontend on Vercel

Vercel supports Vite projects and build-time environment variables; consult its official [Vite deployment](https://vercel.com/docs/frameworks/frontend/vite) and [environment variable](https://vercel.com/docs/projects/environment-variables) documentation.

Create a Vercel project with:

| Setting | Value |
| --- | --- |
| Root Directory | `src/Web` |
| Framework Preset | Vite |
| Build Command | `npm run build` |
| Output Directory | `dist` |
| Environment | `VITE_API_URL=https://api.chat.example` |

Attach `app.chat.example` to the project. Configure Vercel response headers with the same policy as `src/Web/nginx.conf`; `connect-src` must allow `https://api.chat.example` and `wss://api.chat.example`. Redeploy after changing `VITE_API_URL`, because Vite embeds it at build time.

## Backend on Render

Render deploys Dockerfiles and supports private-network service discovery and infrastructure-as-code Blueprints. Use the official [Docker](https://render.com/docs/docker), [private network](https://render.com/docs/private-network), and [Blueprint specification](https://render.com/docs/blueprint-spec) references.

Create one public Docker web service for `src/Gateway/Dockerfile` and private Docker services for Identity, Family Graph, Room, Message, Realtime Hub, and Notification. Use each service's existing Dockerfile. Keep RabbitMQ private with a persistent disk and one replica; use Render Key Value or another Redis-compatible managed service. Render documents managed [PostgreSQL](https://render.com/docs/postgresql-creating-connecting) and [Key Value](https://render.com/docs/key-value).

For repository-backed services, set Render's root directory to `src` so each Dockerfile keeps the same build context used by Compose:

| Render service | Visibility | Dockerfile | Stateful dependencies |
| --- | --- | --- | --- |
| `gateway` | Public | `Gateway/Dockerfile` | All HTTP services |
| `identity-svc` | Private | `Services/Identity/Dockerfile` | Identity DB, RabbitMQ |
| `family-svc` | Private | `Services/FamilyGraph/Dockerfile` | Family DB, RabbitMQ |
| `room-svc` | Private | `Services/Room/Dockerfile` | Room DB, RabbitMQ, Family gRPC |
| `message-svc` | Private | `Services/Message/Dockerfile` | Message DB, RabbitMQ, Room gRPC |
| `realtime-hub` | Private | `Services/RealtimeHub/Dockerfile` | RabbitMQ, Redis, Room gRPC |
| `notification-svc` | Private | `Services/Notification/Dockerfile` | Notification DB, RabbitMQ, Room gRPC, SMTP |

Deploy RabbitMQ from its official container image as a private service, persist `/var/lib/rabbitmq`, enable `rabbitmq_prometheus`, and do not publish ports `5672`, `15672`, or metrics port `15692` publicly. Allow Prometheus to scrape `15692` only over the private network. Set every HTTP health check to `/health`. Identity, Message, Realtime, Notification, and Gateway use HTTP port `8080`; Family Graph and Room additionally bind private gRPC port `8081`.

Preserve five independent databases. They may share a managed PostgreSQL server only if it provides five separate logical databases and credentials; never point two bounded contexts at the same schema.

Set the Gateway destinations to Render private hostnames, for example:

```dotenv
ReverseProxy__Clusters__identity__Destinations__d1__Address=http://identity-svc:8080
ReverseProxy__Clusters__family__Destinations__d1__Address=http://family-svc:8080
ReverseProxy__Clusters__room__Destinations__d1__Address=http://room-svc:8080
ReverseProxy__Clusters__message__Destinations__d1__Address=http://message-svc:8080
ReverseProxy__Clusters__realtime__Destinations__d1__Address=http://realtime-hub:8080
CORS__AllowedOrigins=https://app.chat.example
```

Replace hostnames and ports with the exact private-network values shown by Render. Room and Family Graph also expose internal h2c gRPC on port `8081`; do not expose that port publicly.

## Production secrets and configuration

Set `ASPNETCORE_ENVIRONMENT=Production` everywhere. Generate RSA keys as described in `README.md`: only Identity receives `JWT__PrivateKeyBase64`; validators receive `JWT__PublicKeyBase64`. Set the same issuer/audience everywhere and distinct random `InternalAuth__FamilyToken` and `InternalAuth__RoomToken` values of at least 32 bytes.

Also configure non-guest `RabbitMQ__User` and `RabbitMQ__Pass` credentials, five `ConnectionStrings__Default` values, Redis authentication/TLS, OTLP destination, and SMTP secrets. Every service now refuses missing or `guest` RabbitMQ credentials outside Development. Use Render secret environment variables; never copy `.env` to production. Set health checks to `/health`, enable automatic deploy only after CI passes, and run at least one instance of every required service.

## Release verification

Before directing users to production:

```bash
dotnet test FamilyChat.sln
cd src/Web && npm ci && npm run typecheck && npm test && npm run build
docker compose config --quiet
```

Then verify both custom domains, registration and refresh after reopening the browser, two-user SignalR delivery, persistence after reload, member revocation, SMTP delivery, metrics, and a backup restored into isolated databases. A Vercel deployment alone is not a functioning application; the complete Render backend and its stateful dependencies must already be healthy.

Also delete a disposable test account from the sidebar and verify that it cannot log in again, its room access is revoked, and its authored message disappears after history reload. Account erasure is eventually consistent across services; monitor RabbitMQ queues and the per-service tombstones before treating the workflow as complete.
