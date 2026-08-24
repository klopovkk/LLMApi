# Quickstart: Per-User Message Ordering and Exclusivity

**Feature**: `001-per-user-message-ordering` | **Date**: 2026-08-24
**Spec**: [spec.md](./spec.md) | **Plan**: [plan.md](./plan.md) | **Contracts**: [contracts/](./contracts/)

How to run the system and convince yourself it behaves as specified. The automated suite proves the
seven required scenarios; the manual walkthrough covers Quality Gates 1–6, which the constitution
says must be demonstrable by running two instances rather than by inspection.

Every command below was run against the implementation on 2026-08-24. Where a result is quoted, it
is a measured one rather than an expectation.

## Prerequisites

- .NET SDK 10 (`dotnet --version` reports 10.x)
- Docker Desktop running — required for both the composed environment and the test suite, which
  starts its own PostgreSQL container
- Ports 5001, 5002, and 5432 free

## Automated verification

```bash
dotnet test
```

Runs everything. The concurrency suite starts one PostgreSQL container and hosts two middleware
instances in-process against it — separate application state, one shared store, which is the
property under test.

```bash
# The seven required scenarios individually
dotnet test --filter "FullyQualifiedName~SingleUserFifoTests"
dotnet test --filter "FullyQualifiedName~SingleUserExclusivityTests"
dotnet test --filter "FullyQualifiedName~MultiUserConcurrencyTests"
dotnet test --filter "FullyQualifiedName~TwoInstanceProcessingTests"
dotnet test --filter "FullyQualifiedName~CrossInstanceCallbackTests"
dotnet test --filter "FullyQualifiedName~ClaimExpiryTests"
dotnet test --filter "FullyQualifiedName~StoreUnavailableTests"
```

Expected: all green, whole suite under 60 seconds (R-008). No test sleeps waiting for a timeout —
expiry is driven by advancing a `FakeTimeProvider` (R-005), so a slow run means something is wrong
rather than merely loaded.

Measured on the implementation: **64 passed, 2 skipped, 32 seconds.** The two skips are the
performance smoke tests, which are gated behind an environment variable so a loaded machine cannot
fail the build on timing alone:

```bash
RUN_PERF_TESTS=1 dotnet test --filter "FullyQualifiedName~PerformanceSmokeTests"
```

| Scenario | Proves | Criteria |
|----------|--------|----------|
| `SingleUserFifoTests` | Processing starts in acceptance order for one user | FR-002, SC-002 |
| `SingleUserExclusivityTests` | Never two active for one user, including under induced races | FR-003, FR-012, SC-003 |
| `MultiUserConcurrencyTests` | A held message for one user delays nobody else | FR-004, SC-004 |
| `TwoInstanceProcessingTests` | Order and exclusivity hold when both instances accept and claim | FR-007, SC-005 |
| `CrossInstanceCallbackTests` | A callback delivered to the non-submitting instance completes the message and advances the queue | FR-006, SC-006 |
| `ClaimExpiryTests` | A never-answered message is failed and the queue released; a late callback does not revive it | FR-013, FR-013a, FR-013b, SC-011, SC-012 |
| `StoreUnavailableTests` | Submissions are refused as retryable during an outage, nothing is buffered, service resumes unattended | FR-022, FR-022a, FR-022b, SC-013 |

**How FIFO is asserted.** Not by watching wall-clock timing, which would be racy. Each message's
status exposes `sequence` and `claimedAt`, so the test asserts that `claimedAt` ordering agrees with
`sequence` ordering within a user (SC-002) — a claim about data, not about scheduling luck.

**How exclusivity is asserted.** The invariant is a partial unique index, so a broken design does
not merely fail an assertion — the losing claim raises a unique violation. The test drives at least
100 induced races and asserts exactly one winner each time (SC-005), and it queries for any moment
where two rows for one user were `Processing` (SC-003).

## Manual walkthrough (Quality Gates 1–6)

### Start the environment

```bash
docker compose up -d          # PostgreSQL plus two middleware instances
docker compose ps             # both instances healthy
curl http://localhost:5001/health
curl http://localhost:5002/health
```

Each instance is configured with its peer's callback address, so every completion callback crosses a
process boundary — the FR-006 path is exercised on every message rather than left to chance. A real
deployment would put a load balancer in front and let it distribute callbacks; peer-pointing is a
deliberate substitute that makes the cross-instance case certain (R-011).

To run the instances outside Docker instead:

```bash
docker compose up -d postgres

# The connection string is never in tracked configuration, so it comes from the environment.
export ConnectionStrings__Postgres="Host=localhost;Port=5432;Database=llmapi;Username=postgres;Password=postgres"

# Point each instance's provider at the other, so callbacks cross a process boundary.
FakeProvider__CallbackBaseUri=http://localhost:5002 Processing__InstanceId=local-1   dotnet run --project src/Middleware --urls=http://localhost:5001
FakeProvider__CallbackBaseUri=http://localhost:5001 Processing__InstanceId=local-2   dotnet run --project src/Middleware --urls=http://localhost:5002
```

### Gate 1 & 2 — concurrent acceptance, no early start

```bash
# Three submissions for one user, as fast as curl will go, split across both instances
curl -s -X POST http://localhost:5001/messages -H 'Content-Type: application/json' \
     -d '{"userId":"alice","content":"first"}'  &
curl -s -X POST http://localhost:5002/messages -H 'Content-Type: application/json' \
     -d '{"userId":"alice","content":"second"}' &
curl -s -X POST http://localhost:5001/messages -H 'Content-Type: application/json' \
     -d '{"userId":"alice","content":"third"}'  &
wait
```

Expected: three `202` responses with three distinct `messageId` values and ascending `sequence`
values. None waited on another.

Then read them back:

```bash
curl -s http://localhost:5001/messages/<id>   # repeat per id, or watch in a loop
```

Expected: at any instant at most one of alice's three is `Processing`. The second does not leave
`Pending` until the first is `Completed` or `Failed`, and `claimedAt` ordering follows `sequence`
ordering.

### Gate 3 — FIFO across different instances

The submissions above already went to different instances. Confirm that ascending `sequence`
matches ascending `claimedAt` regardless of which instance accepted each message.

Direct query, if you prefer to see it in the store:

```bash
docker compose exec postgres psql -U postgres -d llmapi -c \
  "SELECT sequence, state, claimed_by, claimed_at FROM messages
    WHERE user_id='alice' ORDER BY sequence;"
```

`claimed_by` will vary between instances while ordering stays monotone — the point being that order
comes from the store, not from who handled the request.

### Gate 4 — a callback on any instance

Submit, then immediately post the callback yourself to the instance that did *not* accept it —
faster than the provider's two-second delay, so your callback is the one that lands. (To take the
provider out of the picture entirely, set `FakeProvider__Mode: NeverRespond` on both services in
`docker-compose.yml` and recreate them.)

```bash
curl -s -X POST http://localhost:5001/messages -H 'Content-Type: application/json' \
     -d '{"userId":"bob","content":"hello"}'
# take the messageId, then complete it via the OTHER instance
curl -i -X POST http://localhost:5002/callbacks/llm -H 'Content-Type: application/json' \
     -d '{"messageId":"<id>","status":"completed","answer":"an answer"}'
```

Expected: `204`, the message reads `Completed` with that answer from either instance, and bob's next
waiting message (if any) starts. Post the same callback a second time: still `204`, still exactly
one completion, no second advance (FR-014, SC-010).

### Gate 5 — different users run concurrently

```bash
# carol is held unanswered; dave and erin should sail past her
curl -s -X POST http://localhost:5001/messages -H 'Content-Type: application/json' \
     -d '{"userId":"carol","content":"held"}'
for u in dave erin frank; do
  curl -s -X POST http://localhost:5002/messages -H 'Content-Type: application/json' \
       -d "{\"userId\":\"$u\",\"content\":\"quick\"}"
done
```

Expected: dave, erin, and frank all reach `Completed` while carol is still `Processing` (SC-004).

### Gate 6 — restart does not corrupt state

```bash
# with messages waiting and in progress
docker compose restart middleware-1
curl http://localhost:5001/health
```

Expected: every message still present exactly once with its original `sequence`; nothing stuck in
`Processing` beyond the claim timeout; waiting messages resume without client action. A message the
restarted instance had claimed but not completed is failed by expiry once the timeout passes, and
the user's next message starts (FR-013, FR-018).

### Bonus — store outage behaviour (FR-022)

Not one of the six gates, but quick to see and the reason `/health` exists:

```bash
docker compose stop postgres
curl -i http://localhost:5001/health                       # 503, storeReachable false
curl -i -X POST http://localhost:5001/messages -H 'Content-Type: application/json' \
        -d '{"userId":"alice","content":"during outage"}'   # 503, retryable, no id issued
docker compose start postgres
curl -i http://localhost:5001/health                       # 200 again, unattended
```

Expected: nothing was accepted during the outage and no phantom message appears afterwards; work
that was pending beforehand resumes on its own (FR-022b, SC-013).

## Verified run (2026-08-24)

The walkthrough above was executed against two containers. What was observed:

| Gate | Result |
|------|--------|
| 1 & 2 | Three submissions for `alice` accepted concurrently across both instances, sequences 1–3, exactly one `Processing` immediately afterwards |
| 3 | `claimed_at` ascended with `sequence` while `claimed_by` alternated `middleware-1`, `middleware-2`, `middleware-1` — ordering held while the work moved between instances |
| 4 | Submitted via instance 1, completed by a callback posted to instance 2 (`204`); a duplicate delivery also returned `204` and left the original answer intact |
| 5 | `dave`, `erin`, and `frank` all `Processing` at the same instant |
| 6 | `middleware-1` restarted with work in flight: five messages, five distinct sequences, none lost or duplicated |
| Bonus | With PostgreSQL stopped: `/health` 503 with `storeReachable: false`, submissions 503 with retryable problem details, zero rows written; recovered one second after the store returned |

The most informative moment was gate 6. The message `middleware-1` had claimed stayed `Processing`
after the restart, because the in-flight provider task died with the container and its callback was
never sent. Thirty seconds later the claim expired, the message was failed with
`No completion callback was received before the claim expired`, and the four messages behind it
completed in order. That is the whole recovery argument — at-most-once delivery plus expiry as the
single safety net — working end to end in the real environment rather than only under a fake clock.

One defect surfaced here that the automated suite had missed: an all-zeros message identifier
answered `400` where every other unknown identifier answered `404`. Fixed, with tests.

## Configuration

| Setting | Default | Purpose |
|---------|---------|---------|
| `ConnectionStrings:Postgres` | from compose | Shared store. Supplied by environment, never committed. |
| `Processing:ClaimTimeout` | `00:00:30` | How long a message may stay `Processing` before expiry (FR-013). Milliseconds in tests. |
| `Processing:SweepInterval` | `00:00:00.250` | How often the sweeper expires stale claims and picks up pending work. |
| `Processing:SweepBatchSize` | `100` | How many users one sweep considers for pickup. |
| `Processing:InstanceId` | machine/container name | Diagnostics only — nothing routes by it. |
| `FakeProvider:Mode` | `Respond` | `Respond`, `Fail`, or `NeverRespond`. |
| `FakeProvider:Delay` | `00:00:02` | Simulated answering latency. |
| `FakeProvider:CallbackBaseUri` | peer instance | Where completions are posted. Peer-pointing guarantees the cross-instance path. |

In containers these are supplied as environment variables with `__` in place of `:` — for example
`Processing__ClaimTimeout` and `FakeProvider__CallbackBaseUri`. See `docker-compose.yml`.

No secrets are involved — there is no provider key, and no authentication anywhere (FR-020). The
connection string arrives from the environment regardless, per the constitution's constraint on
tracked configuration.

## Teardown

```bash
docker compose down -v     # -v also drops the volume
```

The `-v` matters: finished messages are never removed by the application (FR-023), so a fresh start
means recreating the environment. Test containers clean themselves up.
