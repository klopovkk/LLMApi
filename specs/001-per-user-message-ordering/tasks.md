---

description: "Task list for per-user message ordering and exclusivity"
---

# Tasks: Per-User Message Ordering and Exclusivity

**Input**: Design documents from `/specs/001-per-user-message-ordering/`

**Prerequisites**: [plan.md](./plan.md), [spec.md](./spec.md), [research.md](./research.md),
[data-model.md](./data-model.md), [contracts/](./contracts/)

**Tests**: MANDATORY, not optional. Constitution Principle V is the only NON-NEGOTIABLE principle:
every test is written first, run, observed failing, and **committed** before the implementation that
makes it pass. `CLAUDE.md` forbids bundling a test commit with its implementation commit, because
the red-green history is itself the evidence of compliance.

**Organization**: Twelve phases following the architectural seams of the design. Setup, type
definitions, and configuration are grouped where they touch different files, carry no independent
architectural significance, and need no separate validation. Everything with architectural weight —
the claim, the boundary, the callback, the sweeper, multi-instance behaviour, outage behaviour — is
kept as its own task.

## Format: `[ID] [P?] [Story] Description`

- **[P]**: Can run in parallel (different files, no dependency on an incomplete task)
- **[Story]**: Which user story the task belongs to (US1, US2, US3)
- Every task names its exact file paths

## Path Conventions

Per `plan.md`: `src/Middleware`, `src/LLM.Abstraction`, `src/LLM.FakeProvider`,
`tests/Concurrency`, all from the repository root.

## Red-green discipline

Each phase after Foundation is a **failing-test task followed by the implementation task that makes
it pass**, and those are always two commits: `test(<scope>): ...` then `feat(<scope>): ...`. A test
task is not complete until the test has been run and observed failing for the right reason — a test
that passes when first written is a bug in the test, not a task finished early.

---

## Phase 1: Setup

- [ ] T001 Establish the four-project layout: `git mv LLMApi.Api src/Middleware` renaming the project file to `src/Middleware/Middleware.csproj` with `<RootNamespace>Middleware</RootNamespace>`; create `src/LLM.Abstraction/LLM.Abstraction.csproj` (no package references — the boundary assembly stays dependency-free per Principle IV), `src/LLM.FakeProvider/LLM.FakeProvider.csproj` (references `src/LLM.Abstraction` only), and the xUnit project `tests/Concurrency/Concurrency.csproj`; update `LLMApi.slnx` to reference all four and add a reference from `src/Middleware` to `src/LLM.Abstraction` **only** — never to `src/LLM.FakeProvider`, so the compiler enforces the boundary rather than review catching it. All projects target `net10.0`
- [ ] T002 Add dependencies and build configuration: `Npgsql` to `src/Middleware/Middleware.csproj`; `xunit`, `Microsoft.NET.Test.Sdk`, `Testcontainers.PostgreSql`, `Microsoft.AspNetCore.Mvc.Testing`, and `Microsoft.Extensions.TimeProvider.Testing` to `tests/Concurrency/Concurrency.csproj`; `Directory.Build.props` at the repository root enabling `Nullable`, `ImplicitUsings`, and `TreatWarningsAsErrors`. Confirm `dotnet build` and `dotnet test` both succeed on the empty solution

**Checkpoint**: Four projects, one solution, green build, `dotnet run --project src/Middleware` works.

---

## Phase 2: Foundation (Blocking Prerequisites)

**⚠️ CRITICAL**: No story work begins until this phase is complete.

- [ ] T003 Build the test harness: `PostgresFixture` in `tests/Concurrency/Fixtures/PostgresFixture.cs` starting a `Testcontainers.PostgreSql` container shared per test collection, and `InstanceFactory` in `tests/Concurrency/Fixtures/InstanceFactory.cs` building a `WebApplicationFactory` with per-instance overrides for connection string, instance id, claim timeout, `TimeProvider`, fake provider mode, and callback base URI. Test infrastructure, so no preceding test of its own
- [ ] T004 Write the failing tests in `tests/Concurrency/FoundationTests.cs`: applying the schema from two callers concurrently yields exactly one `messages` table with all three indexes and no exception (R-007), and `GET /health` returns 200 with `storeReachable: true` against a live container and 503 with `storeReachable: false` when the connection string points nowhere
- [ ] T005 Make T004 pass: add `src/Middleware/Schema/schema.sql` as an embedded resource carrying the DDL from `data-model.md` (table, `CHECK` constraints, `ux_messages_active_per_user`, `ix_messages_pending`, `ix_messages_processing_claimed_at`); implement `SchemaInitializer` in `src/Middleware/Schema/SchemaInitializer.cs` applying it inside `pg_advisory_lock(4919283746501)`; define `MessageState` in `src/Middleware/Messages/MessageState.cs`, the `Message` row projection in `src/Middleware/Messages/Message.cs`, and `ProcessingOptions` in `src/Middleware/Configuration/ProcessingOptions.cs` (`ClaimTimeout` 30s, `SweepInterval` 250ms, `InstanceId`); implement `GET /health` in `src/Middleware/Api/HealthEndpoints.cs`; register `NpgsqlDataSource`, `ProcessingOptions`, `TimeProvider.System`, and `SchemaInitializer` in `src/Middleware/Program.cs`

**Checkpoint**: Schema applies safely from two instances; the harness can start configured instances against a real PostgreSQL.

---

## Phase 3: Message Acceptance and Read (US1, Priority: P1) 🎯 MVP

**Goal**: Accept a submission without client-side coordination and let the client read it back.

**Independent Test**: Submit repeatedly for one user with no waiting; every submission is accepted
with a distinct identifier and an ascending sequence.

- [ ] T006 [US1] Write the failing tests in `tests/Concurrency/Contracts/SubmitMessageTests.cs` and `tests/Concurrency/Contracts/GetMessageTests.cs`: 202 carrying `messageId`, `sequence`, `state`, and `acceptedAt`; ascending `sequence` across rapid submissions; 400 on blank `userId` or `content` with no queued state created (FR-017); two identical submissions producing two distinct messages (FR-009a); 200 on read with the fields in `contracts/openapi.yaml`; 404 for an unknown identifier; and the read response never echoing submitted content
- [ ] T007 [US1] Make T006 pass: implement `MessageStore.InsertAsync` (statement 1 from `data-model.md`, returning the assigned `sequence`) and `MessageStore.GetByIdAsync` (statement 5, selecting no `content` column) in `src/Middleware/Messages/MessageStore.cs`, and `POST /messages` plus `GET /messages/{messageId}` with request validation in `src/Middleware/Api/MessageEndpoints.cs`

**Checkpoint**: Messages are accepted and durably recorded in acceptance order; nothing processes them yet.

---

## Phase 4: Atomic Claim (US1, Priority: P1)

**Goal**: The per-user FIFO and exclusivity invariant. **This is the load-bearing phase — if it is
wrong, everything built on it is wrong.**

**Independent Test**: Drive concurrent claim attempts for one user directly against the store;
exactly one wins each round and the winner is always the lowest-sequence pending message.

- [ ] T008 [US1] Write the failing tests in `tests/Concurrency/SingleUserFifoTests.cs`, `tests/Concurrency/SingleUserExclusivityTests.cs`, and `tests/Concurrency/ClaimRaceTests.cs`: for 100 messages submitted for one user, `claimedAt` ordering matches `sequence` ordering in every case (FR-002, SC-002); no two messages for one user are ever `Processing` at the same instant (FR-003, SC-003); and 100 rounds of concurrent `TryClaimNextAsync` calls for one user each produce exactly one winner and never zero (FR-012, SC-005)
- [ ] T009 [US1] Make T008 pass: implement `MessageStore.TryClaimNextAsync` in `src/Middleware/Messages/MessageStore.cs` using statement 2 from `data-model.md` verbatim — the `ORDER BY sequence LIMIT 1` subselect, the `NOT EXISTS` guard, and `RETURNING` — catching `PostgresException` with `SqlState` 23505 on `ux_messages_active_per_user` and returning "lost the race" rather than rethrowing. The unique index is the guarantee; the `NOT EXISTS` is only an optimization on top of it

**Checkpoint**: FIFO and exclusivity hold at the store level, proven against real PostgreSQL.

---

## Phase 5: Language-Model Boundary (US1, Priority: P1)

**Goal**: Submission is asynchronous and substitutable, with nothing held across the wait.

**Independent Test**: Submit through the fake provider and observe that the call returns before any
answer exists, that exactly one callback POST is attempted, and that `NeverRespond` posts nothing.

- [ ] T010 [P] [US1] Define the boundary types exactly as in `contracts/llm-boundary.md`: `ILlmClient`, `LlmSubmission`, and `LlmCompletion` in `src/LLM.Abstraction/`, and `FakeProviderOptions` in `src/LLM.FakeProvider/FakeProviderOptions.cs` with `Mode` (`Respond`/`Fail`/`NeverRespond`), `Delay`, and `CallbackBaseUri`
- [ ] T011 [US1] Write the failing tests in `tests/Concurrency/FakeLlmClientTests.cs`: `SubmitAsync` returns before the delay elapses; exactly one POST is made per submission and never a retry when the target refuses the connection (FR-019a); `NeverRespond` posts nothing; and the synthetic answer is derived from the message identifier, never from content
- [ ] T012 [US1] Make T011 pass and wire the hot path: implement `FakeLlmClient` in `src/LLM.FakeProvider/FakeLlmClient.cs` scheduling a single delayed HTTP POST of `LlmCompletion`; implement `MessageDispatcher.TryStartNextAsync` in `src/Middleware/Messages/MessageDispatcher.cs` claiming and then submitting **outside any transaction**, treating a submission throw as "no callback is coming" with no compensating update (caller obligations 2 and 3 in `contracts/llm-boundary.md`); register `FakeLlmClient` as the `ILlmClient` in `src/Middleware/Program.cs` and call `TryStartNextAsync` from the accept path in `src/Middleware/Api/MessageEndpoints.cs` after the insert commits

**Checkpoint**: Accepted messages are claimed and submitted; nothing completes them yet.

---

## Phase 6: Callback Handling (US1, Priority: P1)

**Goal**: Completion arrives asynchronously over HTTP, is idempotent by final state, and releases
the user's queue.

**Independent Test**: Post a callback for an in-flight message; it completes and the successor
starts. Post the same callback again; nothing changes and the queue does not advance twice.

- [ ] T013 [US1] Write the failing tests in `tests/Concurrency/Contracts/CallbackTests.cs`: 204 on completion with the answer readable afterwards; 204 again on a duplicate with exactly one completion and exactly one queue advance (FR-014, SC-010); 404 for an unknown identifier with no other message altered (FR-015); 400 on a malformed body; a `failed` callback moving the message to `Failed` and still advancing the queue (FR-016); and no caller verification of any kind being required (FR-020a)
- [ ] T014 [US1] Make T013 pass: implement `MessageStore.TryCompleteAsync` in `src/Middleware/Messages/MessageStore.cs` using statement 3 with its `AND state = 'Processing'` guard, returning `user_id` only when a row actually changed; implement `POST /callbacks/llm` in `src/Middleware/Api/CallbackEndpoints.cs` calling `TryStartNextAsync` for that user in a **separate** statement after the completion commits, so a failure to start the successor cannot roll back a completion already reported; and add structured transition logging in `src/Middleware/Messages/MessageStore.cs` and `src/Middleware/Messages/MessageDispatcher.cs` carrying `messageId`, `userId`, `fromState`, `toState`, `sequence`, and instance identity — never `content`, at any level (FR-021, R-009)

**Checkpoint**: **User Story 1 is complete and demonstrable.** A client fires messages without coordinating; they run one at a time, in order, answered asynchronously.

---

## Phase 7: Multi-User Processing (US2, Priority: P2)

**Goal**: Distinct users proceed in parallel; one user's stuck message delays nobody else.

**Independent Test**: Hold one user's message with `Mode = NeverRespond`, submit for ten other
users, and watch all ten finish while the held one is still `Processing`.

- [ ] T015 [US2] Write the failing tests in `tests/Concurrency/MultiUserConcurrencyTests.cs` and `tests/Concurrency/SweeperPickupTests.cs`: with user X held unanswered, ten other users' messages all reach `Completed` before X leaves `Processing` (FR-004, SC-004); the observed count of simultaneously `Processing` messages across distinct users exceeds one at some instant, proving parallelism rather than merely fast serialization; and a message inserted directly into the store with no accept-path dispatch is picked up and claimed within a few sweep intervals, for several users in one sweep
- [ ] T016 [US2] Make T015 pass: implement `MessageStore.FindUsersWithPendingWorkAsync` in `src/Middleware/Messages/MessageStore.cs` using the sweeper support statement from `data-model.md` with a batch limit, implement `ProcessingSweeper` as a `BackgroundService` in `src/Middleware/Processing/ProcessingSweeper.cs` iterating pending users each `SweepInterval` and calling `TryStartNextAsync` per user — no global lock and no leader election, since the atomic claim decides winners (R-004) — and register it as a hosted service in `src/Middleware/Program.cs`

**Checkpoint**: Per-user exclusivity holds without becoming a global bottleneck.

---

## Phase 8: Multi-Instance Processing (US3, Priority: P3)

**Goal**: Ordering, exclusivity, and callback routing hold across two instances, and a restart
leaves state intact.

**Independent Test**: Two instances on one store; interleaved submissions for one user, callbacks
delivered to the non-submitting instance, one instance restarted mid-flight.

- [ ] T017 [US3] Write the failing tests in `tests/Concurrency/TwoInstanceProcessingTests.cs`, `tests/Concurrency/ClaimRaceAcrossInstancesTests.cs`, and `tests/Concurrency/CrossInstanceCallbackTests.cs`: two `InstanceFactory` instances on one container accept for the same user and the earlier-accepted message still starts first with exclusivity never broken (FR-007); at least 100 induced races where both instances claim the same waiting message produce exactly one winner and never zero (SC-005); and a message submitted via instance 1 is completed by a callback posted to instance 2, with the successor starting (FR-006, SC-006)
- [ ] T018 [US3] Make T017 pass: resolve `InstanceId` in `src/Middleware/Configuration/ProcessingOptions.cs` from machine or container name and record it as `claimed_by` for diagnostics only, then audit `src/Middleware/Api/CallbackEndpoints.cs` and `src/Middleware/Messages/MessageDispatcher.cs` for any in-memory map from message to submitting instance and remove it — no correctness may depend on which instance handled anything (FR-008, Principle I)
- [ ] T019 [US3] Write the failing test in `tests/Concurrency/RestartDurabilityTests.cs`: with messages waiting and in progress, dispose and recreate one instance, then assert every message is still present exactly once with its original `sequence`, that none was processed twice or dropped, and that processing resumes without client action (FR-018, Quality Gate 6)
- [ ] T020 [US3] Make T019 pass: correct any path in `src/Middleware/Program.cs` or `src/Middleware/Schema/SchemaInitializer.cs` that assumes an empty store, re-initializes state, or reconstructs anything from process memory at startup

**Checkpoint**: All three user stories work. Quality Gates 1 through 6 are demonstrable.

---

## Phase 9: Claim Expiry (Required Scenario 6)

**Goal**: A message that never receives a callback cannot block its user forever. With at-most-once
delivery (FR-019a) this is the **only** recovery path in the system.

**Independent Test**: Hold a message with `Mode = NeverRespond`, advance `FakeTimeProvider` past
`ClaimTimeout`, and watch it fail and the queue release.

- [ ] T021 Write the failing tests in `tests/Concurrency/ClaimExpiryTests.cs` using `FakeTimeProvider`: a never-answered message becomes `Failed` with a reason identifying expiry and the user's next message starts (FR-013, SC-011); the expired message is never re-submitted (FR-013a); and a callback arriving after expiry leaves it `Failed` and does not disturb the user's currently active message (FR-013b, SC-012)
- [ ] T022 Make T021 pass: implement `MessageStore.ExpireStaleClaimsAsync` in `src/Middleware/Messages/MessageStore.cs` using statement 4, with `@cutoff` computed in application code as `TimeProvider.GetUtcNow() - ClaimTimeout` so tests advance time instead of sleeping (R-005), and call it from `ProcessingSweeper` in `src/Middleware/Processing/ProcessingSweeper.cs` before the pending-pickup pass, feeding returned user identifiers straight into `TryStartNextAsync`. Confirm no test in the suite sleeps waiting for a timeout

**Checkpoint**: Stalled work self-heals; no user can be permanently blocked.

---

## Phase 10: Store Outage Behaviour (Required Scenario 7)

**Goal**: With the shared store unreachable, operations fail as retryable, nothing is buffered
locally, and service resumes unattended.

**Independent Test**: Stop the store, exercise every endpoint, restart the store, and confirm
nothing was accepted during the outage and nothing phantom appears afterwards.

- [ ] T023 Write the failing tests in `tests/Concurrency/StoreUnavailableTests.cs`: with the store unreachable, `POST /messages` returns 503 with retryable problem details and issues no identifier, `GET /messages/{id}` returns 503, and `/health` reports `storeReachable: false` (FR-022, SC-013); nothing is buffered in process memory during the outage and no phantom message appears after the store returns (FR-022a); pending work resumes unattended once it does (FR-022b); and no 503 body ever contains message content
- [ ] T024 Make T023 pass: map `NpgsqlException` connection failures to 503 problem details in `src/Middleware/Api/MessageEndpoints.cs`, `src/Middleware/Api/CallbackEndpoints.cs`, and `src/Middleware/Api/HealthEndpoints.cs`, and make `ProcessingSweeper` in `src/Middleware/Processing/ProcessingSweeper.cs` survive an outage — log at Error, never crash the host, resume on the next interval

**Checkpoint**: All seven required scenarios pass. `tests/Concurrency` matches what `CLAUDE.md` claims it contains.

---

## Phase 11: Compose and Integration Validation

- [ ] T025 Add `docker-compose.yml` at the repository root with PostgreSQL plus two middleware instances on ports 5001 and 5002, each configured with its **peer's** callback base URI so every callback crosses a process boundary (R-011); add `src/Middleware/appsettings.Development.json` defaults and document every environment variable the compose file uses, keeping the connection string out of tracked configuration per the constitution
- [ ] T026 Walk `specs/001-per-user-message-ordering/quickstart.md` end to end against `docker compose up -d`, confirming Quality Gates 1 through 6 by hand with two real instances, and correct any command in `quickstart.md` that does not work exactly as written

**Checkpoint**: The gates that must be demonstrated by running the system, not by inspection, have been.

---

## Phase 12: Final Validation

- [ ] T027 Add `tests/Concurrency/LoggingPolicyTests.cs` capturing log output across a full message lifecycle and asserting the submitted content string never appears at any level (FR-021). If it fails, fix the offending call sites in `src/Middleware/` and `src/LLM.FakeProvider/` in a separate follow-up commit
- [ ] T028 [P] Add `tests/Concurrency/PerformanceSmokeTests.cs` checking submission acknowledgement p95 under 50 ms and successor claim within 100 ms of a predecessor finishing (R-008), gated behind an environment variable so a loaded CI machine cannot fail the suite on timing alone
- [ ] T029 Run the full `tests/Concurrency` suite and confirm all seven required scenarios pass, the suite completes in under 60 seconds, and no test sleeps on a timeout (R-005, R-008)
- [ ] T030 Reconcile artifacts with the finished code: re-read `specs/001-per-user-message-ordering/spec.md`, `plan.md`, and this file, update anything that diverged in the same commit as the divergence (Principle VII), and confirm no secret material is present in tracked files or captured log output and that `dotnet build` is warning-free under `TreatWarningsAsErrors` (Quality Gate 8)

---

## Dependencies & Execution Order

### Phase dependencies

- **Phase 1 (Setup)**: no dependencies
- **Phase 2 (Foundation)**: needs Phase 1 — **blocks everything else**
- **Phase 3 (Acceptance)**: needs Phase 2
- **Phase 4 (Claim)**: needs Phase 3 for rows to claim
- **Phase 5 (Boundary)**: needs Phase 4 — a claim exists before anything is submitted
- **Phase 6 (Callback)**: needs Phase 5
- **Phase 7 (Multi-user)**: needs Phase 6
- **Phase 8 (Multi-instance)**: needs Phase 6; T019/T020 additionally benefit from Phase 7's sweeper
- **Phase 9 (Expiry)**: needs Phase 7's sweeper as its host
- **Phase 10 (Outage)**: needs only Phase 3, and may be pulled forward if convenient
- **Phase 11 (Compose)**: needs Phases 8 and 9 — a demonstration without the sweeper looks stuck
- **Phase 12 (Final)**: needs everything

### Story dependencies

- **US1 (P1)** — Phases 3 through 6: independent, and deliverable alone as the MVP
- **US2 (P2)** — Phase 7: builds on US1's claim but independently testable, since its test asserts
  cross-user parallelism that US1 never exercises
- **US3 (P3)** — Phase 8: builds on US1 and US2 but independently testable, asserting cross-instance
  ordering, callback routing, and restart durability that neither earlier story touches

### Within each phase

- The failing test is written, run, observed red, and **committed** before its implementation
- Store methods before dispatcher before endpoints
- Never commit a test together with the implementation that makes it pass

### Parallel opportunities

- T010 — boundary type definitions, different projects, can be written alongside Phase 4
- T028 — performance smoke test, independent of the other Phase 12 work

**Not parallelizable despite appearances**: every task touching
`src/Middleware/Messages/MessageStore.cs` (T007, T009, T014, T016, T022) — same file, and the claim
statement in T009 is the one place where a merge conflict resolved carelessly would silently break
the feature's central invariant. The phases are otherwise a single vertical slice; splitting them
across people costs more in coordination than it returns.

---

## Implementation Strategy

### MVP first

1. Phase 1 Setup
2. Phase 2 Foundation — blocks everything
3. Phases 3 through 6 — User Story 1
4. **STOP and VALIDATE**: single-user FIFO and exclusivity pass against real PostgreSQL
5. Demonstrable: a client fires messages without coordinating and they are answered one at a time, in order

### Incremental delivery

1. Setup + Foundation → schema applies safely from two instances
2. + Phases 3–6 → **MVP**: ordering and exclusivity for one user, end to end
3. + Phase 7 → users no longer share a bottleneck
4. + Phase 8 → the guarantees hold across two instances and restarts
5. + Phases 9–10 → the system recovers from stalls and store outages
6. + Phases 11–12 → demonstrated by hand and validated against `quickstart.md`

### Sequencing advice

Phase 4 is where this feature is either right or wrong. It sits as early as the schema and an
insert path allow, so a wrong claim design surfaces before the boundary, the callback, the sweeper,
and the multi-instance work are built on top of it. Resist deferring it behind more comfortable
work — if T008 cannot be made to pass, everything after it changes.

---

## Notes

- `[P]` means different files and no dependency on an incomplete task
- Every test task ends with the test **failing for the right reason**
- Commit format: `test(<scope>): ...` then `feat(<scope>): ...`, scopes from `CLAUDE.md`
  (`claim-store`, `llm-boundary`, `callback-handler`, `infra`, `tests`)
- Never squash a red commit into its green commit — the history is the Principle V evidence
- After any change touching claim or state logic, run the whole `tests/Concurrency` suite, not just
  the affected test
