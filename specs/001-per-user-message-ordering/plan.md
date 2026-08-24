# Implementation Plan: Per-User Message Ordering and Exclusivity

**Branch**: `001-per-user-message-ordering` | **Date**: 2026-08-24 | **Spec**: [spec.md](./spec.md)

**Input**: Feature specification from `/specs/001-per-user-message-ordering/spec.md`

## Summary

Accept messages for a user without asking the client to coordinate, then start them one at a time
in acceptance order, never two at once for the same user, while distinct users proceed in parallel
— and hold all of that across at least two instances, with the language model answering
asynchronously through an HTTP callback that any instance can receive.

The approach puts every coordination fact in PostgreSQL. A message row carries its own state and a
database-assigned sequence, so per-user FIFO is `ORDER BY sequence` and needs no clock. Exclusivity
is a partial unique index — `UNIQUE (user_id) WHERE state = 'Processing'` — which makes "at most one
active message per user" something the database cannot violate rather than something the code must
remember. Claiming is one conditional `UPDATE`; the loser of a race sees a unique violation and
stands down. Nothing is held across the language-model call: the middleware submits, returns, and
waits for a callback that resolves the message from whichever instance receives it. A per-instance
background sweeper expires stale claims and picks up any user with pending work and no active
message, which is what makes an instance dying mid-flight cost a little latency instead of
correctness.

See [research.md](./research.md) for the decisions and rejected alternatives behind this.

## Technical Context

**Language/Version**: C# 14 on .NET 10 (`net10.0`; existing project already targets it, installed
SDK 10.0.302 — satisfies the constitution's ".NET 9 or newer")

**Primary Dependencies**: ASP.NET Core minimal APIs; `Npgsql` for data access with explicit SQL
(no ORM — see R-006); `Microsoft.Extensions.Hosting` for the sweeper; `TimeProvider` for the
expiry clock

**Storage**: PostgreSQL 17, single table plus two indexes. Sole shared store — no Redis (R-001)

**Testing**: xUnit; `Testcontainers.PostgreSql` for a real PostgreSQL in tests;
`Microsoft.AspNetCore.Mvc.Testing` hosting two instances in-process against one container;
`Microsoft.Extensions.TimeProvider.Testing` (`FakeTimeProvider`) to drive expiry deterministically

**Target Platform**: Linux containers via Docker Compose for the demonstration; the test suite runs
anywhere Docker is available

**Project Type**: Web service (HTTP API) plus two supporting libraries and one test project

**Performance Goals**: submission acknowledgement p95 under 50 ms; successor claimed within 100 ms
of its predecessor reaching a final state; at least 200 accepted submissions per second across two
instances; full required suite under 60 s. Full table in R-008

**Constraints**: no correctness may depend on in-memory state, process-local locks, or instance
affinity (Principle I, FR-008); no lock or open request held across the language-model call
(Principle IV); message content never written to logs at any level (FR-021, R-009); no
authentication anywhere, and the callback endpoint verifies nothing about its caller (FR-020,
FR-020a); callback delivery is at-most-once (FR-019a), so claim expiry is the only recovery path

**Scale/Scope**: demonstration scale — tens of users, hundreds of messages per run, exactly two
instances. Storage grows monotonically for an environment's life (FR-023); reset by recreating the
environment

## Constitution Check

*GATE: Must pass before Phase 0 research. Re-check after Phase 1 design.*

Constitution version 1.0.0, ratified 2026-08-24.

### Pre-Phase 0 evaluation

| Principle | Verdict | How this plan satisfies it |
|-----------|---------|----------------------------|
| I. Distributed Correctness First | PASS | Ordering comes from a database sequence and exclusivity from a database index. No guarantee is derived from process memory, a `lock` statement, or which instance a request reached. |
| II. Explicit State and Ownership | PASS | One `state` column with four explicit values, plus `claimed_at`, `sequence`, and timestamps. "Which message is active for this user" and "may the next one start" are both single queries. |
| III. Atomic Coordination | PASS | Claiming is one conditional `UPDATE` guarded by a partial unique index. Two instances cannot both win; the loser gets a unique violation, not a silent success. Expiry uses the same shape. |
| IV. Asynchronous LLM Boundary | PASS | `ILlmClient` in a separate dependency-free assembly; only the composition root names a provider, guarded by `BoundaryTests`. Submission returns immediately; no transaction, lock, or request is held across the wait. Completion arrives as an inbound HTTP callback handled independently of the submitting request. |
| V. Test-First and Testable Architecture | PASS (with obligations) | Tests run against real PostgreSQL, so the mechanism itself is under test. All five required scenarios are covered, plus two more the clarifications made necessary — see the gate note below. Every task in `tasks.md` must land its failing test in a separate commit before the implementation commit. |
| VI. Simplicity and Scope | PASS | One store, one table, no ORM, no migration tool, no Redis, no load balancer, no metrics backend, no auth. Three source projects, and the third exists only because Principle IV requires the language-model integration to sit behind a substitutable boundary. |
| VII. Traceability | PASS (with obligations) | Every decision in `research.md` is numbered and referenced from this plan. Two documentation corrections fall out of this plan and must ride with the code that causes them — see Required artifact corrections. |

**Principle V gate note**: the constitution enumerates five required scenarios. The clarification
session added two more that are not optional, because they cover behaviour the clarifications made
load-bearing rather than incidental:

1. FIFO for a single user
2. Exclusivity for a single user
3. Concurrent processing for different users
4. Correct processing across two instances
5. Callback handled on an instance other than the submitting one
6. **Claim expiry** — with at-most-once callback delivery (FR-019a), expiry is the *only* recovery
   path in the system, so it needs a deterministic test driven by `FakeTimeProvider`
7. **Shared store unavailable** — FR-022/022a/022b describe required behaviour during an outage,
   which no other scenario exercises

**Result: PASS.** No violations, so Complexity Tracking is omitted.

### Post-Phase 1 re-evaluation

Re-checked against `data-model.md`, `contracts/`, and `quickstart.md`. Still PASS. Three points
worth recording because the design phase is where they could have gone wrong:

- **Principle IV held under pressure.** The inline claim attempt after acceptance runs *before* the
  response is written, which invites keeping a transaction open across the submission to the
  language model. The contract in `contracts/llm-boundary.md` forbids it explicitly: the state
  transition to `Processing` commits first, then submission happens outside any transaction. A
  submission that throws leaves the message claimed and lets expiry reclaim it, which is correct
  and needs no compensating logic.
- **Principle II survived the addition of expiry.** `Failed` carries a `failure_reason`
  distinguishing a provider-reported failure from an expiry, so an operator can tell "the model
  said no" from "nobody ever answered" — a distinction Principle II's inspectability requirement
  would otherwise lose.
- **Principle VI was tested by the health endpoint.** `GET /health` is the only endpoint not
  implied by a functional requirement. It is kept because FR-022 describes behaviour during a store
  outage and scenario 7 needs an observable signal that the instance is alive while the store is
  not (R-009). One endpoint, no dependencies added.

## Project Structure

### Documentation (this feature)

```text
specs/001-per-user-message-ordering/
├── plan.md              # This file
├── spec.md              # Feature specification (with Clarifications session)
├── research.md          # Phase 0 output — 12 numbered decisions
├── data-model.md        # Phase 1 output — schema, states, claim statements
├── quickstart.md        # Phase 1 output — how to run and verify
├── contracts/
│   ├── openapi.yaml     # HTTP surface: submission, status, callback, health
│   └── llm-boundary.md  # ILlmClient contract and fake provider behaviour
├── checklists/
│   └── requirements.md  # Spec quality checklist (16/16)
└── tasks.md             # Phase 2 output (/speckit-tasks — NOT created here)
```

### Source Code (repository root)

```text
src/
├── Middleware/                     # ASP.NET Core minimal API (relocated from LLMApi.Api)
│   ├── Program.cs                  # Composition root, endpoint registration
│   ├── Api/
│   │   ├── MessageEndpoints.cs     # POST /messages, GET /messages/{id}
│   │   ├── CallbackEndpoints.cs    # POST /callbacks/llm
│   │   └── HealthEndpoints.cs      # GET /health
│   ├── Messages/
│   │   ├── MessageStore.cs         # All SQL: insert, claim, complete, expire, read
│   │   ├── MessageState.cs         # The four explicit states
│   │   ├── Message.cs              # Row projection
│   │   └── MessageDispatcher.cs    # Claim-then-submit, shared by inline and sweeper paths
│   ├── Processing/
│   │   └── ProcessingSweeper.cs    # BackgroundService: expire stale claims, pick up pending work
│   ├── Schema/
│   │   ├── schema.sql              # Idempotent DDL (embedded resource)
│   │   └── SchemaInitializer.cs    # Applies schema under pg_advisory_lock at startup
│   └── Configuration/
│       └── ProcessingOptions.cs    # ClaimTimeout, SweepInterval, callback base URL
├── LLM.Abstraction/                # No provider dependencies — the Principle IV boundary
│   ├── ILlmClient.cs
│   ├── LlmSubmission.cs
│   └── LlmCompletion.cs
└── LLM.FakeProvider/               # In-process fake; single delayed HTTP POST, no retry
    ├── FakeLlmClient.cs
    └── FakeProviderOptions.cs      # Delay, mode (Respond | Fail | NeverRespond)

tests/
└── Concurrency/                    # The required scenarios (5 constitutional + 2 added)
    ├── Fixtures/
    │   ├── PostgresFixture.cs      # Testcontainers PostgreSQL, shared per class
    │   └── InstanceFactory.cs      # WebApplicationFactory with per-instance config
    ├── SingleUserFifoTests.cs      # Scenario 1 — FR-002, SC-002
    ├── SingleUserExclusivityTests.cs # Scenario 2 — FR-003, FR-012, SC-003
    ├── MultiUserConcurrencyTests.cs  # Scenario 3 — FR-004, SC-004
    ├── TwoInstanceProcessingTests.cs # Scenario 4 — FR-007, SC-005
    ├── CrossInstanceCallbackTests.cs # Scenario 5 — FR-006, SC-006
    ├── ClaimExpiryTests.cs           # Scenario 6 — FR-013/013a/013b, SC-011, SC-012
    └── StoreUnavailableTests.cs      # Scenario 7 — FR-022/022a/022b, SC-013

docker-compose.yml                  # PostgreSQL + two middleware instances (peer callbacks)
LLMApi.slnx                         # Updated to reference all four projects
```

**Structure Decision**: The layout `CLAUDE.md` already prescribes, adopted as written (R-012). The
existing `LLMApi.Api` scaffold is relocated to `src/Middleware` and `LLMApi.slnx` updated, so the
documented commands (`dotnet run --project src/Middleware --urls=...`) are correct from the first
commit rather than aspirational. `LLM.Abstraction` is its own dependency-free assembly, and no file
under `src/Middleware` except `Program.cs` may name a concrete provider — the composition root has
to, since an executable must compose something. That narrower rule is enforced by `BoundaryTests`
rather than by the compiler, which cannot express "this one file may".

## Post-implementation review

Implemented 2026-08-24. Constitution check re-run against the finished code: still **PASS**. The
suite is 64 passing plus 2 gated performance tests, in 32 seconds; all seven required scenarios pass
and Quality Gates 1–6 were demonstrated against two containers (see `quickstart.md`).

Four things the design got wrong, each found by a test or by the manual walkthrough rather than by
review. They are recorded here because the corrections are the part worth keeping:

1. **The claim was not actually atomic** (`data-model.md` statement 2). Under `READ COMMITTED`,
   EvalPlanQual re-reads the target row's own columns but does *not* re-evaluate subqueries in the
   qualification. The `NOT EXISTS` guard therefore stayed true for every blocked caller, and 99 of
   100 contended rounds produced two or more winners. The partial unique index could not catch it,
   because updating one row repeatedly never creates a second `Processing` row. Fixed by adding
   `AND state = 'Pending'` on the target row.
2. **Completion destroyed the evidence FIFO is asserted from.** Statement 3 cleared `claimed_at`, so
   by the time a message was `Completed` there was no record of when it had started — and SC-002 is
   defined in terms of exactly that. `claimed_at` is now retained.
3. **The two contract documents disagreed** about the callback body: `openapi.yaml` specified a
   status string, `llm-boundary.md` implied `LlmCompletion`'s boolean. The wire contract won and the
   provider maps at the edge.
4. **The compiler could not enforce the Principle IV boundary** (R-012). A composition root has to
   name a concrete implementation to register it, so `Program.cs` cannot compile without the
   reference. `BoundaryTests` enforces the narrower true rule instead — no file under
   `src/Middleware` except `Program.cs` may name a provider — and also asserts the abstraction
   assembly still has no dependencies of its own.

The first two are the ones that justify R-010's insistence on a real PostgreSQL in the test suite. A
substitute would have reported both designs as correct.

## Required artifact corrections

Both raised by this plan under Principle VII, both now resolved:

1. **`CLAUDE.md` repo layout** — RESOLVED. The line "docker-compose.yml — Postgres/Redis for shared
   state" was removed rather than narrowed, since R-001 rejects Redis outright and the compose file
   is described in `quickstart.md` instead. Lands in the same commit as this plan.
2. **`CLAUDE.md` commit scopes** — RESOLVED. `infra` covers repository-level documents, including
   the constitution, `CLAUDE.md`, and the artifacts under `specs/`. No `spec` scope is added; the
   scope list stays as written and the existing `docs(infra)` commits on this branch stay
   consistent with it.

3. **`CLAUDE.md` scenario count** — RESOLVED. The line describing `tests/Concurrency` as "the five
   required scenario tests" now reads seven: the five in Principle V, plus claim expiry and
   store-unavailable from the gate note above. Corrected ahead of the test project existing so the
   working agreement states the real obligation from the outset rather than after the fact.

## Implementation sequence

`/speckit-tasks` will expand this; recorded here so the ordering rationale is not lost. Every step
is a failing test committed first, then the implementation (Principle V, and `CLAUDE.md`'s
prohibition on bundling the two).

1. **Foundation** — project layout, `slnx`, schema plus `SchemaInitializer`, `PostgresFixture`.
   Verified by a test that applies the schema twice concurrently and finds one table.
2. **Accept and read** — insert with sequence, `POST /messages`, `GET /messages/{id}`, validation
   (FR-017), store-unavailable behaviour (FR-022). Scenario 7 lands here.
3. **Claim** — the partial unique index and the conditional `UPDATE`. Scenarios 1, 2, and 4 land
   here; this is the step where the whole feature is either right or wrong.
4. **Boundary and submission** — `ILlmClient`, `FakeLlmClient`, claim-then-submit-outside-a-
   transaction.
5. **Callback** — `POST /callbacks/llm`, final-state transition, duplicate and unknown and
   post-expiry handling (FR-014, FR-015, FR-013b), successor claim. Scenarios 3 and 5 land here.
6. **Sweeper** — expiry against `FakeTimeProvider`, pending-work pickup. Scenario 6 lands here.
7. **Compose and quickstart** — two instances with peer callbacks; walk Quality Gates 1–6 by hand.

The ordering is deliberate: step 3 is the load-bearing one, and it comes as early as the schema
allows so that a wrong claim design is found before four other components are built on top of it.

## Risks and how the design answers them

| Risk | Answer |
|------|--------|
| A claim race passes tests by luck rather than by design | The invariant is a database constraint, so a broken design surfaces as a unique violation in the losing path — an assertion, not a coin flip. Scenario 4 induces at least 100 races (SC-005) rather than one. |
| Expiry tests become slow or flaky | The cutoff is computed from an injected `TimeProvider` (R-005), so tests advance time instead of waiting. No test sleeps for a timeout. |
| Instance dies between insert and claim, stalling a user forever | The sweeper picks up any user with pending work and no active message, so the cost is latency, not a stuck queue. |
| Instance dies between claim and submission | The message stays `Processing` with no callback coming, and expiry resolves it — the same path FR-013 already requires, with no compensating transaction. |
| The two-instance test topology is not really two instances | Two `WebApplicationFactory` hosts have genuinely separate application state and share only PostgreSQL, which is exactly the property under test. The compose walkthrough covers the OS-process case for the gates that demand it. |
| Cross-instance callbacks pass by accident under a load balancer | Each instance is configured to call its *peer*, so every message in the composed run crosses a process boundary (R-011). |
| Content leaks into logs | Content is never logged at any level, and logging code paths take message identifiers rather than message bodies (R-009). |
