# Family Chat deployment guide

Family Chat is a distributed application. A production deployment consists of the Vite SPA on Vercel and the .NET gateway plus private services and stateful infrastructure on Render.

The repository now contains the platform configuration required for that topology:

- `src/Web/vercel.json` — Vite build output, SPA fallback, security headers, and immutable asset caching;
- `render.yaml` — Render Blueprint for the public gateway, six private .NET services, RabbitMQ, Render Key Value, and five isolated PostgreSQL databases;
- `deploy/rabbitmq/Dockerfile.render` — RabbitMQ image with the management and Prometheus plugins already used by the local stack;
- shared runtime normalization for Render's generated PostgreSQL, Key Value, and private-service connection values.

## Production topology

Use sibling custom domains under the same registrable domain:

```text
app.chat.example  -> Vercel / src/Web
api.chat.example  -> Render / gateway
                       |-- identity-svc
                       |-- family-svc
                       |-- room-svc
                       |-- message-svc
                       |-- realtime-hub
                       |-- notification-svc
                       |-- RabbitMQ
                       |-- Render Key Value
                       `-- five PostgreSQL databases
```

The sibling-domain requirement is important for authentication. The refresh token is intentionally stored in a `Secure`, `HttpOnly`, `SameSite=Strict` cookie. A default `*.vercel.app` frontend and a default `*.onrender.com` API are cross-site, so they are suitable for build/health verification but not for the final persistent-login topology. Use domains such as `app.example.com` and `api.example.com` before treating the deployment as production-ready.

## 1. Generate production JWT keys

Production intentionally refuses the development symmetric JWT secret. Generate an RSA pair locally and keep the private key out of Git:

```bash
openssl genpkey -algorithm RSA -pkeyopt rsa_keygen_bits:3072 -out jwt-private.pem
openssl rsa -pubout -in jwt-private.pem -out jwt-public.pem

# GNU/Linux
base64 -w0 jwt-private.pem; echo
base64 -w0 jwt-public.pem; echo
```

The first Base64 value is `JWT__PrivateKeyBase64`; the second is `JWT__PublicKeyBase64`.

Do not commit either production value to this repository.

## 2. Deploy the backend with Render Blueprint

Render supports repository-level Blueprints through `render.yaml`. Create a new Blueprint in Render and select this repository. The Blueprint creates:

| Resource | Type | Purpose |
| --- | --- | --- |
| `gateway` | public Docker web service | YARP HTTP/WebSocket ingress |
| `identity-svc` | private Docker service | authentication and refresh tokens |
| `family-svc` | private Docker service | user/family projection and gRPC |
| `room-svc` | private Docker service | rooms, roles, membership, gRPC |
| `message-svc` | private Docker service | persistent chat history |
| `realtime-hub` | private Docker service | SignalR and Redis backplane |
| `notification-svc` | private Docker service | notification consumers |
| `rabbitmq` | private Docker service + disk | MassTransit broker |
| `redis` | Render Key Value | SignalR backplane/presence |
| five `*-db` resources | Render Postgres | one database per bounded context |

During the first Blueprint creation Render prompts for the three values declared with `sync: false`:

```text
identity-svc / JWT__PrivateKeyBase64 = <private-key Base64>
identity-svc / JWT__PublicKeyBase64  = <public-key Base64>
gateway      / Cors__AllowedOrigins = https://app.chat.example
```

Use the final HTTPS frontend origin for `Cors__AllowedOrigins`. Multiple origins can be comma-separated if required.

The Blueprint generates RabbitMQ credentials and internal gRPC tokens automatically, references datastore connection strings without committing credentials, keeps PostgreSQL/Key Value off the public internet, and deploys Git-backed services only after repository checks pass.

### Render plans and region

`render.yaml` deliberately uses paid production-capable resources: `starter` service/Key Value instances and `basic-256mb` PostgreSQL instances. Render does not provide a free private-service instance type. Review the Blueprint cost before applying it.

All resources are pinned to `oregon` so they share the same private network. If another Render region is required, change every `region` entry in `render.yaml` **before the first Blueprint creation**; Render resource regions generally cannot be changed in place later.

### Render private networking

Do not replace the `Render__*Host` variables with Compose names. Render assigns stable private hostnames that include a generated suffix. The Blueprint injects those hostnames and the shared startup code maps them to:

```text
Gateway HTTP routes -> private service host:8080
Family gRPC         -> family private host:8081
Room gRPC           -> room private host:8081
```

Render's managed PostgreSQL and Key Value references are URL-shaped (`postgresql://...` and `redis://...`). The shared startup code converts those values into the Npgsql and StackExchange.Redis configuration formats before each service reads them. Existing Docker Compose connection strings are left unchanged.

### Notifications

The Blueprint starts `notification-svc` with notifications disabled so the initial production deployment does not depend on an SMTP vendor. To enable email later, set these Render environment variables on `notification-svc`:

```text
Notifications__Provider=Smtp
Notifications__Smtp__Host=<smtp-host>
Notifications__Smtp__Port=587
Notifications__Smtp__EnableSsl=true
Notifications__Smtp__From=<verified-from-address>
Notifications__Smtp__Username=<username-if-required>
Notifications__Smtp__Password=<secret-if-required>
Notifications__IncludeContent=false
```

Redeploy `notification-svc` after changing them.

## 3. Attach the API custom domain

When the Render gateway is healthy at `/health`, attach the production API domain to `gateway`, for example:

```text
api.chat.example
```

Keep the generated `onrender.com` URL available for platform diagnostics unless your Render plan/domain policy intentionally disables it.

## 4. Deploy the frontend to Vercel

Import this GitHub repository into Vercel and use:

| Vercel setting | Value |
| --- | --- |
| Root Directory | `src/Web` |
| Framework Preset | Vite |
| Build Command | `npm run build` |
| Output Directory | `dist` |
| Production env | `VITE_API_URL=https://api.chat.example` |

`src/Web/vercel.json` already supplies the Vite SPA rewrite required for direct/deep-link navigation, the build/output settings, security headers, and long-lived caching for hashed assets.

Attach the frontend custom domain, for example:

```text
app.chat.example
```

Because Vite embeds `VITE_API_URL` at build time, redeploy Vercel after changing the API URL.

If the final frontend origin differs from the value entered during initial Render Blueprint creation, update `Cors__AllowedOrigins` manually on the `gateway` Render service and redeploy it. Render only prompts for `sync: false` variables during the first Blueprint creation.

## 5. Production verification

Verify in this order:

```text
1. https://api.chat.example/health returns success.
2. https://app.chat.example loads without CSP/CORS errors.
3. Register a user and confirm a PublicId is projected.
4. Close/reopen the browser and verify refresh-cookie login restoration.
5. With two users, create/join a room and exchange SignalR messages.
6. Reload both clients and confirm message persistence.
7. Change/remove room membership and confirm realtime access revocation.
8. Delete a disposable account and confirm login fails afterward and projections are erased.
9. Check RabbitMQ queues and Render service logs for stalled consumers/outboxes.
```

The refresh-cookie restoration test is the key proof that the sibling-domain setup is correct. A successful login alone is not sufficient because the short-lived access token is held only in browser memory.

## Local development remains unchanged

The Render-specific adaptation is conditional. Local Docker Compose continues to use the existing service names and keyword-style datastore connection strings:

```bash
cp .env.example .env
docker compose config --quiet
docker compose up --detach --build --wait
```

Open `http://localhost:3000`; the gateway remains available on `http://localhost:5000`.

Before merging deployment changes, run the repository gates:

```bash
dotnet test FamilyChat.sln
cd src/Web
npm ci
npm run typecheck
npm test
npm run build
cd ../..
docker compose config --quiet
```

The GitHub Actions workflow also runs the distributed browser, notification, k6, backup, resilience, and migration checks.

## References

- Vercel Vite deployment: https://vercel.com/docs/frameworks/frontend/vite
- Vercel project configuration: https://vercel.com/docs/project-configuration/vercel-json
- Render Blueprint specification: https://render.com/docs/blueprint-spec
- Render private networking: https://render.com/docs/private-network
- Render Docker services: https://render.com/docs/docker
- Render Postgres: https://render.com/docs/postgresql-creating-connecting
- Render Key Value: https://render.com/docs/key-value
