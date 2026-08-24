# LLMApi

Middleware that accepts messages for a user without asking the client to coordinate, then starts
them one at a time in acceptance order — never two at once for the same user, while different users
proceed in parallel — and holds those guarantees across at least two instances, with the language
model answering asynchronously through an HTTP callback that any instance can receive.

## How it works, briefly

Every coordination fact lives in PostgreSQL. A message row carries its own state and a
database-assigned sequence, so per-user FIFO is `ORDER BY sequence` and needs no clock — which is
what lets two instances agree on a user's order without talking to each other.

Exclusivity is a partial unique index, `UNIQUE (user_id) WHERE state = 'Processing'`: at most one
active message per user is something the database cannot violate, rather than something the code has
to remember. Claiming is a single conditional `UPDATE`, and the loser of a race stands down.

Nothing is held across the language-model call. The middleware claims, commits, submits, and
returns; the answer arrives later as a callback that resolves the message from whichever instance
receives it. A per-instance sweeper expires stale claims and picks up work nobody dispatched, which
is what turns an instance dying mid-flight into a little latency instead of a stuck user.

## Running it

```bash
docker compose up -d
curl http://localhost:5001/health
curl http://localhost:5002/health
```

Two instances on ports 5001 and 5002, each configured to post its callbacks to the other so the
cross-instance path is exercised on every message.

```bash
curl -X POST http://localhost:5001/messages \
     -H 'Content-Type: application/json' \
     -d '{"userId":"alice","content":"hello"}'

curl http://localhost:5001/messages/<messageId>
```

## Tests

```bash
dotnet test
```

Runs against a real PostgreSQL started by Testcontainers — deliberately, because the invariant under
test is a property of PostgreSQL's concurrency behaviour and a substitute would make the tests pass
while proving nothing.

## Documentation

Everything about this feature — requirements, the decisions behind the design, the SQL the
correctness argument rests on, and how to verify it by hand — lives in
[`specs/001-per-user-message-ordering/`](specs/001-per-user-message-ordering/):

| Document | What is in it |
|----------|---------------|
| [spec.md](specs/001-per-user-message-ordering/spec.md) | Requirements, acceptance scenarios, success criteria |
| [plan.md](specs/001-per-user-message-ordering/plan.md) | Architecture, constitution check, post-implementation review |
| [research.md](specs/001-per-user-message-ordering/research.md) | Twelve numbered decisions and the alternatives rejected |
| [data-model.md](specs/001-per-user-message-ordering/data-model.md) | Schema, states, and the five statements the design rests on |
| [contracts/](specs/001-per-user-message-ordering/contracts/) | HTTP surface and the language-model boundary |
| [quickstart.md](specs/001-per-user-message-ordering/quickstart.md) | How to run and verify, including a measured walkthrough |

Project conventions are in [CLAUDE.md](CLAUDE.md); the governing principles are in
[.specify/memory/constitution.md](.specify/memory/constitution.md).
