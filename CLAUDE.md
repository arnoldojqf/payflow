# PayFlow

Cloud-native, event-driven payment gateway (portfolio project). Simulates
payment processing end-to-end; no real money movement. The goal is to
demonstrate production-grade distributed-systems patterns.

## Architecture

Three .NET 10 services communicating via Kafka (monorepo):

- **src/PayFlow.PaymentApi** — minimal API. Receives `POST /payments`,
  validates, persists to PostgreSQL with an idempotency key, publishes
  `PaymentCreated` via transactional outbox, responds `202 Accepted`.
- **src/PayFlow.PaymentProcessor** — worker service, no HTTP endpoints.
  Consumes `PaymentCreated`, simulates the acquirer call (retries +
  circuit breaker via Polly), publishes `PaymentAuthorized` or
  `PaymentFailed`.
- **src/PayFlow.WebhookDispatcher** — worker service. Consumes result
  events, notifies merchants via HMAC-signed webhooks with exponential
  retries and DLQ.

Shared event contracts live in a shared project referenced by all
services (never duplicate contract types).

## Key decisions (do not revisit without asking)

- .NET 10 (current LTS), minimal APIs, PostgreSQL, Kafka.
- Transactional outbox for atomic write+publish.
- Idempotency keys on payment creation (client-supplied header).
- At-least-once delivery + idempotent consumers.
- Local dev via Docker Compose; Kubernetes + Terraform later (infra/).

## Conventions

- Conventional Commits (feat:, fix:, chore:, docs:, test:, refactor:).
- English for all code, comments, commits, and docs.
- Tests accompany every feature (xUnit). No feature merges untested.
- Explain non-obvious choices in code comments sparingly; deeper
  rationale goes in docs/adr/.

## Environment

- Runs on WSL2 (Ubuntu), 4 GB RAM limit — keep Docker Compose services
  memory-constrained.
- Build: `dotnet build` at repo root. Test: `dotnet test`.

## Working style

- Propose a short plan before multi-file changes; wait for approval.
- Small, reviewable diffs. One logical change per commit.
- The human reviews and must understand every line; when asked,
  explain generated code rather than just producing it.
