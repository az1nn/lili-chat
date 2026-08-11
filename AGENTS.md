# Repository Guidelines

## Project Structure & Module Organization

Backend code lives in `src/`: `Gateway/` is the YARP edge, `Services/` contains bounded contexts, and `Shared/` holds contracts and telemetry defaults. The React/TypeScript client is in `src/Web/`. Infrastructure belongs in `deploy/`; `docker-compose.yml` defines local orchestration. Keep persistence inside its owning service.

## Build, Test, and Development Commands

- `cp .env.example .env` creates local configuration; never commit real secrets.
- `docker compose up --build` builds and starts the complete stack.
- `docker compose down` stops local containers without deleting volumes.
- `docker compose --profile test run --rm k6` runs the end-to-end smoke test.
- `./scripts/check-structure.sh` verifies required project and Docker files.
- `dotnet build FamilyChat.sln && dotnet test FamilyChat.sln` validates the backend and contract tests.
- `RUN_INTEGRATION_TESTS=1 dotnet test tests/Persistence.IntegrationTests` validates service migrations against disposable PostgreSQL databases; Docker is required.
- `cd src/Web && npm ci && npm test && npm run build` installs locked dependencies, runs component tests, and builds the web client; use `npm run dev` for Vite development.

## Coding Style & Naming Conventions

Use four-space indentation in C# and two spaces in TypeScript, JSON, and YAML. Use `PascalCase` for types/public members, `camelCase` for locals/parameters, and `Async` for asynchronous methods. Keep nullable references enabled. React components and types use `PascalCase`; hooks use `useName`. Run relevant builds before submitting.

## Testing Guidelines

Tests live under `tests/`; update contract tests whenever protobuf fields or shared events change. New projects use `<Service>.Tests` and behavior names such as `CreateRoom_RejectsNonMember`. Frontend tests live beside features as `*.test.ts(x)`. Run the structure check, solution tests, affected builds, and k6 smoke test for cross-service changes.

## Architecture & Security

Each service owns its database; never query another bounded context directly. Identity owns credentials, FamilyGraph owns `PublicId` and family links, Room owns membership and exposes authorization through gRPC, Message owns persistent history, and RealtimeHub must not persist messages. Put shared events and protobuf contracts in `FamilyChat.Contracts`; keep contract changes backward-compatible or version them. Derive user identity from JWT claims—never trust a frontend-supplied `userId`.

## Commits & Pull Requests

Use concise, imperative, scoped commits (for example, `fix(room): validate member role`). Pull requests should explain motivation, affected services/contracts, validation performed, configuration or migration impact, and linked issues. Include screenshots for visible web changes and call out compatibility or security implications.
