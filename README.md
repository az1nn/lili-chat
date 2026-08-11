# Family Chat Distributed

Chat familiar distribuído baseado em React + .NET 8, com autenticação JWT, salas compartilhadas, convite por `PublicId`, mensagens em tempo real com SignalR, RabbitMQ, Redis, bancos PostgreSQL isolados por bounded context, YARP e telemetria OpenTelemetry.

## O que está implementado

- **Identity Service**: registro, login, refresh token rotativo e logout.
- **Family Graph Service**: projeção de usuários vindos do Identity, geração de `PublicId`, famílias e membros; gRPC para resolução de usuários.
- **Room Service**: salas, membros, papéis Admin/Member/Muted; adição por `PublicId`; gRPC para autorização de acesso à sala.
- **Realtime Hub**: SignalR autenticado, presença via Redis, validação de membro via gRPC, publicação de `MessageCreatedEvent`, confirmação de persistência, sincronização de roles e expulsão imediata de membros removidos.
- **Message Service**: consumidor idempotente do evento, histórico REST e retenção configurável em lotes.
- **Notification Service**: consumidor do evento e registro de auditoria de notificação (o provedor real de push/email fica como extensão).
- **Gateway**: YARP com CORS, rate limit e proxy de HTTP/WebSocket.
- **Web**: React + TypeScript + Vite com registro/login, criação/listagem de salas, chat, convite por `PublicId`, controles coerentes com Admin/Member/Muted e histórico incremental por cursor ao rolar para cima.
- **Observabilidade**: OpenTelemetry Collector, Jaeger, Prometheus e Grafana.
- **Smoke test**: k6 para registro, login, criação de sala e leitura autorizada do histórico, com latências separadas.

## Decisões corrigidas em relação ao documento de arquitetura

O Realtime Hub não possui banco; por isso ele **não usa Transactional Outbox**. Ele publica diretamente no RabbitMQ. Identity e Room gravam eventos de domínio em outbox; o Message Service grava `MessagePersistedEvent` junto do histórico para confirmar persistência ao frontend. Um advisory lock do PostgreSQL permite apenas um publisher por banco. Falhas usam backoff exponencial de até cinco minutos e continuam sendo tentadas; após 20 erros, uma métrica de evento stalled alerta a operação. Uma falha depois do publish ainda pode repetir o evento, portanto os consumidores continuam obrigatoriamente idempotentes. `MessageId` é a chave primária do histórico.

Cada serviço com PostgreSQL possui migrations próprias e as aplica no startup com `Database.MigrateAsync()`. Ao atualizar um ambiente local criado por uma versão antiga que usava `EnsureCreated`, recrie os volumes uma vez com `docker compose down --volumes` antes de subir o stack.

O repositório fixa o CLI do EF Core em `.config/dotnet-tools.json`. Para criar uma alteração de schema, restaure a ferramenta e gere a migration no serviço que é dono do banco:

```bash
dotnet tool restore
dotnet ef migrations add AddExample --project src/Services/Room/Room.API.csproj
./scripts/check-migrations.sh
```

Revise o SQL gerado e mantenha a migration e o `ModelSnapshot` no mesmo commit da mudança de modelo.

## Quick start

Pré-requisitos: Docker Engine com Docker Compose v2.

```bash
cp .env.example .env
docker compose up --build
```

Acesse:

- Web: http://localhost:3000
- Gateway/API: http://localhost:5000
- RabbitMQ Management: http://localhost:15672
- Jaeger: http://localhost:16686
- Prometheus: http://localhost:9090
- Grafana: http://localhost:3001

Credenciais RabbitMQ de desenvolvimento vêm do `.env`. Troque todos os segredos antes de qualquer deploy real.

O dashboard provisionado **Family Chat Overview** compara `message_publish`, `message_persisted` e falhas. Uma diferença crescente entre publicação e persistência indica lag ou falha no consumidor. O mesmo dashboard acompanha falhas de login, lockouts, reutilização de refresh token, conexões SignalR ativas e falhas Redis/gRPC do realtime.

O Prometheus carrega alertas versionados em `deploy/prometheus/rules/`: gap persistente entre mensagens publicadas/persistidas, outbox stalled, erros HTTP elevados, falhas Redis/gRPC, pipeline de telemetria indisponível, replay de refresh token e pico de lockouts. A CI valida configuração e PromQL com o `promtool` da mesma versão usada no Compose.

O Message Service retém mensagens por 365 dias por padrão e remove conteúdo expirado em lotes limitados. Ajuste `MESSAGE_RETENTION_DAYS` por ambiente; valores aceitos ficam entre 1 e 3650 dias. Tamanho, máximo de lotes e intervalo também são configuráveis no Compose por `MESSAGE_RETENTION_BATCH_SIZE`, `MESSAGE_RETENTION_MAX_BATCHES` e `MESSAGE_RETENTION_INTERVAL_MINUTES`. Falhas de limpeza geram métrica e alerta, sem impedir leitura ou persistência do chat.

### Backup e restore

Cada bounded context gera um dump PostgreSQL próprio e consistente. O diretório de destino deve estar vazio; o script grava dumps em formato custom e um manifesto `SHA256SUMS`:

```bash
./scripts/backup-databases.sh ./backups/$(date +%Y%m%d-%H%M%S)
```

O restore nunca aceita os nomes dos bancos ativos. Ele exige um sufixo `_restore_*`, valida todos os checksums, restaura em bancos isolados e confirma que há tabelas públicas:

```bash
./scripts/restore-databases.sh ./backups/20260811-120000 _restore_drill
```

Defina `CLEANUP_RESTORED_DATABASES=1` para remover os bancos temporários depois do drill. A CI executa backup e restore dos cinco bancos após o E2E; armazene dumps reais fora da máquina de origem, criptografados e com controle de acesso.

O access token permanece somente em memória no navegador. O refresh token usa cookie `HttpOnly`; em produção ele também exige HTTPS para que o atributo `Secure` funcione.

Handlers validam os mesmos limites definidos no schema: username até 100, email até 255, senha até 128, nomes de sala/família até 100, descrições até 1000 e mensagens até 2000 caracteres. O frontend replica esses limites apenas para feedback imediato; o backend continua sendo a autoridade.

Enquanto uploads não são suportados, o Gateway limita corpos HTTP a 64 KiB. Ao implementar anexos, use upload direto para armazenamento com URL assinada em vez de aumentar globalmente esse limite.

No container, o Web encaminha `/api` e `/hubs` ao Gateway pelo Nginx, mantendo cookies e WebSocket same-origin. `http://localhost:5000` permanece exposto para desenvolvimento e diagnóstico.

O access log do Nginx usa apenas `$uri`, sem query string. Isso é obrigatório porque o transporte WebSocket/SSE do SignalR pode enviar o JWT como `access_token` na query; nunca troque o formato seguro por `$request`, `$request_uri`, `$args` ou `$query_string`.

### JWT em produção

Produção exige RSA: somente Identity recebe `JWT_PRIVATE_KEY_BASE64`; os demais serviços recebem `JWT_PUBLIC_KEY_BASE64`. Gere e armazene as chaves no secret manager, por exemplo:

```bash
openssl genpkey -algorithm RSA -pkeyopt rsa_keygen_bits:3072 -out jwt-private.pem
openssl pkey -in jwt-private.pem -pubout -out jwt-public.pem
base64 -w0 jwt-private.pem # JWT_PRIVATE_KEY_BASE64
base64 -w0 jwt-public.pem  # JWT_PUBLIC_KEY_BASE64
```

Defina também `ASPNETCORE_ENVIRONMENT=Production`. O fallback por `JWT_SECRET` é recusado fora de `Development`. Não salve os arquivos PEM ou valores base64 no repositório.

Os containers .NET executam como o usuário não privilegiado `app`. Os health checks aguardam migrations e inicialização HTTP antes de liberar dependências como Room, Message, Realtime e Gateway.

Chamadas gRPC internas têm deadline de três segundos. Falhas de autorização retornam `503` ou um resultado SignalR controlado; a listagem de membros degrada para identificadores genéricos quando o Family Graph está temporariamente indisponível.

### Autenticação interna

As APIs gRPC internas exigem credenciais distintas por destino: `INTERNAL_FAMILY_TOKEN` protege o Family Graph e `INTERNAL_ROOM_TOKEN` protege o Room Service. Use valores aleatórios com pelo menos 32 caracteres no desenvolvimento e identidades de workload ou um secret manager em produção. Esses tokens nunca devem ser enviados pelo navegador.

## Fluxo sugerido para testar

1. Abra `http://localhost:3000`.
2. Crie duas contas em abas ou navegadores separados.
3. Em cada conta, copie o `PublicId` exibido.
4. Crie uma sala com a primeira conta.
5. Abra a sala e adicione a segunda conta pelo `PublicId`.
6. Entre na mesma sala com os dois usuários e envie mensagens.
7. Verifique o RabbitMQ e o Jaeger.

A criação do `PublicId` é assíncrona: o Identity publica `UserRegisteredEvent` e o Family Graph cria a projeção. Em uma máquina local isso costuma ocorrer rapidamente. Se `/users/me` retornar 404 logo após o registro, atualize alguns instantes depois.

## Testes

```bash
dotnet test FamilyChat.sln
RUN_INTEGRATION_TESTS=1 dotnet test tests/Persistence.IntegrationTests # requer Docker
cd src/Web && npm ci && npm run typecheck && npm test && npm run build
npm run test:e2e # requer o Compose ativo e Chromium do Playwright
docker compose --profile test run --rm k6
# carga ajustável, por exemplo:
VUS=10 ITERATIONS=50 docker compose --profile test run --rm k6
```

O E2E usa dois contextos isolados: registra A/B, convida por `PublicId`, troca mensagem via SignalR, aguarda a confirmação `persisted` emitida pela outbox do Message Service, comprova o histórico após reload, verifica mute/unmute em tempo real e valida a revogação depois de remover B.

Os testes de persistência usam Testcontainers para aplicar as migrations de cada serviço em um database PostgreSQL isolado. Sem `RUN_INTEGRATION_TESTS=1`, esse projeto é reportado como skipped; a CI habilita o gate obrigatoriamente.

## Endpoints

### Auth
- `POST /api/v1/auth/register`
- `POST /api/v1/auth/login`
- `POST /api/v1/auth/refresh`
- `POST /api/v1/auth/logout`

### Users / Family
- `GET /api/v1/users/me`
- `GET /api/v1/users/by-public-id/{publicId}`
- `GET /api/v1/families`
- `POST /api/v1/families`
- `POST /api/v1/families/{id}/members`

### Rooms
- `GET /api/v1/rooms`
- `POST /api/v1/rooms`
- `GET /api/v1/rooms/{id}`
- `PATCH /api/v1/rooms/{id}`
- `DELETE /api/v1/rooms/{id}` (arquiva; somente owner)
- `GET /api/v1/rooms/{id}/audit` (somente owner)
- `POST /api/v1/rooms/{id}/leave`
- `GET /api/v1/rooms/{id}/members`
- `POST /api/v1/rooms/{id}/members/by-public-id`
- `PATCH /api/v1/rooms/{id}/members/{userId}/role`
- `DELETE /api/v1/rooms/{id}/members/{userId}`

### Messages
- `GET /api/v1/messages/room/{roomId}?take=50&beforeSentAt=...&beforeId=...`
- SignalR: `/hubs/chat`
  - `JoinRoom(roomId)`
  - `LeaveRoom(roomId)`
  - `SendMessage(roomId, content)`
  - eventos: `MessageReceived`, `PresenceUpdated`

O cursor do histórico é o par `SentAt + MessageId` da mensagem mais antiga carregada. O cliente usa esse par para buscar páginas anteriores sem perder mensagens com o mesmo timestamp.

## Estrutura

```text
src/
  Gateway/
  Shared/
    FamilyChat.Contracts/
    FamilyChat.ServiceDefaults/
  Services/
    Identity/
    FamilyGraph/
    Room/
    Message/
    RealtimeHub/
    Notification/
  Web/
deploy/
  otel-collector/
  prometheus/
  grafana/
  k6/
```

## Limites deste pacote

- O Notification Service registra o evento, mas não chama FCM/APNs/email.
- Não há upload de imagens/anexos.
- Não há E2EE; mensagens são texto persistido no Message Service.
- Não há Kubernetes/manifests; Docker Compose é o alvo desta entrega.
