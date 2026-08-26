# PayFlow

Cloud-native, event-driven payment gateway (portfolio project). Simulates
payment processing end-to-end; no real money movement. The goal is to
demonstrate production-grade distributed-systems patterns.

## Architecture

Three .NET 10 services communicating via Azure Service Bus (monorepo):

- **src/PayFlow.PaymentApi** — minimal API. Receives `POST /payments`,
  validates, persists to PostgreSQL with an idempotency key, publishes
  `PaymentCreated` via transactional outbox, responds `202 Accepted`.
- **src/PayFlow.PaymentProcessor** — worker service, no HTTP endpoints.
  Subscribes to `PaymentCreated`, simulates the acquirer call (retries +
  circuit breaker via Polly), publishes `PaymentAuthorized` or
  `PaymentFailed`.
- **src/PayFlow.WebhookDispatcher** — worker service. Subscribes to result
  events, notifies merchants via HMAC-signed webhooks with exponential
  retries and DLQ.

Shared event contracts live in a shared project referenced by all
services (never duplicate contract types).

## Key decisions (do not revisit without asking)

- .NET 10 (current LTS), minimal APIs, PostgreSQL.
- Azure Service Bus (topics + subscriptions) for messaging.
- EF Core on the write path; Dapper on the read path (CQRS-style split).
- Transactional outbox for atomic write+publish.
- Idempotency keys on payment creation (client-supplied header).
- At-least-once delivery + idempotent consumers.
- Blob Storage + Azure Functions for settlement file processing.
- Local dev via Docker Compose; AKS + Terraform (Azure provider) later
  (infra/).
- CI/CD via GitHub Actions.

## Conventions

- Conventional Commits (feat:, fix:, chore:, docs:, test:, refactor:).
- English for all code, comments, commits, and docs.
- Tests accompany every feature (xUnit). No feature merges untested.
- Explain non-obvious choices in code comments sparingly; deeper
  rationale goes in docs/adr/.

## Environment

- Runs on WSL2 (Ubuntu), 8 GB RAM limit — keep Docker Compose services
  memory-constrained.
- Build: `dotnet build` at repo root. Test: `dotnet test`.
- Local infra: `docker compose up -d` at repo root (Postgres on
  localhost:5432, db/user/password all `payflow`); `docker compose down`
  to stop, `docker compose down -v` to also drop the data volume.
  Credentials default from `docker-compose.yml`; override via `.env`
  (see `.env.example`) and keep the `PayFlowDb` connection string in sync.

## Working style

- Propose a short plan before multi-file changes; wait for approval.
- Small, reviewable diffs. One logical change per commit.
- The human reviews and must understand every line; when asked,
  explain generated code rather than just producing it.