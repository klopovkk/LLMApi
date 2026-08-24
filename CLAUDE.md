# CLAUDE.md — LLMApi

## Source of truth

- Constitution: `.specify/memory/constitution.md` — read before any architectural decision
- Active spec: `specs/<current-feature>/spec.md`
- Plan: `specs/<current-feature>/plan.md`
- Tasks: `specs/<current-feature>/tasks.md`

Do not copy principle text here — path only. Otherwise the files drift apart when
the constitution is amended.

## Repo layout

- `src/Middleware` — API + message processing
- `src/LLM.Abstraction` — LLM boundary (Principle IV)
- `src/LLM.FakeProvider` — test double for the LLM (used per Principle V)
- `tests/Concurrency` — the seven required scenario tests: the five in Principle V, plus
  claim expiry and store-unavailable (see the Principle V gate note in the active plan)

## Commands

- Build: `dotnet build`
- Run tests: `dotnet test`
- Run single test: `dotnet test --filter "FullyQualifiedName~ClassName.MethodName"`
- Start infra: `docker compose up -d`
- Run 2 instances locally:
  `dotnet run --project src/Middleware --urls=http://localhost:5001`
  `dotnet run --project src/Middleware --urls=http://localhost:5002`

## Workflow (TDD)

1. Never write implementation before a failing test exists and has been run and
   observed red, then green
2. After any change touching claim/state logic, run the full `tests/Concurrency`
   suite, not just unit tests
3. If implementation diverges from `spec.md`/`plan.md`/`tasks.md`, update the
   artifact in the same commit as the code change

## Commit strategy

- Branch per feature: `NNN-feature-name` (matches `specs/NNN-feature-name/`)
- One commit = one logical change. Do NOT bundle:
  - a test-writing commit together with its implementation commit (Principle V
    requires a visible red-green-refactor cycle in history - commit the failing
    test first)
  - constitution/spec/plan changes together with unrelated code changes
- Commit message format: `<type>(<scope>): <summary>`
  - types: `feat`, `fix`, `test`, `docs`, `refactor`, `chore`
  - scope: `claim-store` | `llm-boundary` | `callback-handler` | `infra` | `tests`
  - example: `test(claim-store): add failing test for concurrent claim by two instances`
  - example: `feat(claim-store): implement atomic TryClaim via Postgres advisory lock`
- Never commit directly to `master` - always via feature branch + PR, per Governance
- If implementation diverges from spec.md/plan.md/tasks.md, the artifact update goes
  in the SAME commit as the code change, never a follow-up commit
- CI must be green before merge - do not merge on red
- Do not squash commits that separate "red" and "green" test states - the history
  itself is part of the evidence for Principle V compliance

## Out of scope - do not add without explicit request

- Kubernetes manifests, Helm charts
- Auth/authz middleware
- Real LLM provider SDKs (use `LLM.Abstraction` + `FakeProvider`)
- Any caching/perf optimization not required by a Quality Gate
