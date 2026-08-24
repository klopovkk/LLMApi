<!--
Sync Impact Report
- Version change: none (unfilled template) → 1.0.0
- Bump rationale: MAJOR — initial ratification. The previous file was the unpopulated
  `constitution-template` scaffold with every `[PLACEHOLDER]` intact, so this is the first
  governing version rather than an amendment.
- Principles defined (all new; template slots I–V expanded to seven):
  - I. Distributed Correctness First
  - II. Explicit State and Ownership
  - III. Atomic Coordination
  - IV. Asynchronous LLM Boundary
  - V. Test-First and Testable Architecture (NON-NEGOTIABLE)
  - VI. Simplicity and Scope
  - VII. Traceability
- Added sections:
  - Technical Constraints (template slot [SECTION_2_NAME])
  - Quality Gates (template slot [SECTION_3_NAME])
  - Governance (amendment procedure, versioning policy, compliance review)
- Removed sections: none (no prior populated content existed)
- Notes:
  - Principle V merges the supplied "Testable Architecture" principle with the strict
    test-first gate selected during ratification; it is the only NON-NEGOTIABLE principle.
  - Secrets and key hygiene was selected as a hard gate and is recorded under
    Technical Constraints, with enforcement in Quality Gates.
  - Process rigor was chosen as "lightweight solo-friendly"; Governance encodes feature
    branches, self-review, and green CI rather than multi-reviewer PR approval.
  - Repository currently targets `net10.0`, which satisfies the ".NET 9 or newer" constraint.
- Follow-up TODOs: none. No placeholders were deferred.
-->

# LLMApi Constitution

## Core Principles

### I. Distributed Correctness First

The system MUST preserve per-user exclusivity and FIFO processing order across multiple
middleware instances. No correctness guarantee may depend on in-memory state, process-local
locks, or client-side synchronization.

Rationale: the service is defined by its behaviour under horizontal scale-out. A guarantee
that holds only in a single process is not a guarantee, because the deployment target is at
least two concurrently running instances.

### II. Explicit State and Ownership

The state of each message MUST be persisted and MUST represent its current processing stage
explicitly. The system MUST make it possible to determine which message is currently active
for a user and whether the next message may be processed.

Rationale: implicit or derived state cannot be inspected, recovered after a restart, or
reasoned about across instances. Explicit persisted state is what makes ordering and
exclusivity observable and auditable.

### III. Atomic Coordination

Operations that claim a message for processing MUST be atomic and safe under concurrent
execution by multiple middleware instances. A message MUST NOT be claimed simultaneously by
two instances.

Rationale: claiming is the single point where two instances can race. If the claim is not
atomic in shared persistent state, every downstream ordering and exclusivity guarantee fails.

### IV. Asynchronous LLM Boundary

The LLM integration MUST be isolated behind an abstraction. Submitting a message to the LLM
and receiving its completion callback MUST be treated as separate operations. The middleware
MUST NOT hold an application-level lock or keep a request open while waiting for the
asynchronous LLM callback.

Rationale: LLM latency is unbounded and provider-specific. Holding a lock or an HTTP request
across that wait converts provider slowness into service-wide unavailability, and it prevents
a callback from being handled by an instance other than the submitting one.

### V. Test-First and Testable Architecture (NON-NEGOTIABLE)

The core processing logic MUST be testable independently of the real LLM API. Tests MUST be
written before the implementation they cover, MUST be observed failing first, and the
Red-Green-Refactor cycle MUST be followed. Implementation code that arrives without a
preceding failing test is a constitution violation, not a style preference.

The solution MUST include tests demonstrating:

- FIFO processing for a single user;
- exclusivity for a single user;
- concurrent processing for different users;
- correct processing across two middleware instances;
- callback handling on an instance different from the submitting instance.

Rationale: the guarantees in Principles I–IV are concurrency properties that cannot be
confirmed by inspection or manual trial. They are only credible when a test can reproduce the
race, and only reproducible when the LLM boundary is substitutable.

### VI. Simplicity and Scope

The implementation MUST prefer the simplest architecture that satisfies the requirements.
Production-scale infrastructure, authentication, Kubernetes, cloud deployment, and real LLM
integration are out of scope unless required by the specification.

Rationale: the hard problem here is distributed coordination. Infrastructure added beyond the
specification consumes attention without strengthening any guarantee this constitution makes.

### VII. Traceability

Every significant implementation decision MUST be traceable to the specification or design.
The repository MUST maintain `spec.md`, `plan.md`, and `tasks.md`, and the implementation MUST
remain consistent with these artifacts. When implementation diverges from an artifact, the
artifact MUST be updated in the same change rather than left stale.

Rationale: the correctness argument for this system lives in its design documents. Code that
has drifted from them silently invalidates the reasoning that justified it.

## Technical Constraints

- Target framework MUST be .NET 9 or newer.
- The application MUST support at least two simultaneously running instances.
- External infrastructure MUST be runnable locally through Docker Compose.
- Shared persistent state MUST be used for distributed coordination.
- Client-side synchronization MUST NOT be required for correctness.
- Provider API keys and other secrets MUST NOT appear in source control, including
  `appsettings*.json`. They MUST be supplied through configuration providers, user secrets, or
  environment variables, and MUST NOT be written to logs, traces, or error responses.
- Message content sent to or returned from the LLM MUST NOT be logged at `Information` level
  or below.

## Quality Gates

Before considering the implementation complete, verify that:

1. Two messages from the same user can be accepted concurrently.
2. The second message cannot start before the first completes.
3. FIFO order is preserved when messages are handled by different middleware instances.
4. A callback can be received by any middleware instance.
5. Different users can be processed concurrently.
6. Restarting an instance does not corrupt persisted message state.
7. The automated test suite passes, and each of the five scenarios in Principle V is covered by
   a test that was written before its implementation.
8. No secret material is present in tracked files or in captured log output.

Gates 1–6 MUST be demonstrable by running the solution with at least two instances against
locally composed infrastructure, not by inspection alone.

## Governance

This constitution supersedes other practices and ad-hoc conventions in this repository. Where
a `spec.md`, `plan.md`, or `tasks.md` artifact conflicts with a principle here, the
constitution wins and the artifact MUST be corrected.

Amendment procedure: amendments are made by editing this file in a dedicated change that
states what is changing and why, records the resulting version, and updates any affected
Quality Gate. An amendment that relaxes or removes a principle MUST also state how already
merged work is brought back into compliance.

Versioning policy: this document uses semantic versioning. MAJOR for backward-incompatible
governance changes, including removing or redefining a principle. MINOR for a newly added
principle or materially expanded guidance. PATCH for clarifications, wording, and non-semantic
refinements.

Compliance review: development proceeds on feature branches; the author self-reviews the
change against these principles before merging, and CI MUST be green before merge to `master`.
Multi-reviewer approval is deliberately not required while this is a single-maintainer
repository. Any complexity that appears to conflict with Principle VI MUST be justified in the
change description or the relevant design artifact.

**Version**: 1.0.0 | **Ratified**: 2026-08-24 | **Last Amended**: 2026-08-24
