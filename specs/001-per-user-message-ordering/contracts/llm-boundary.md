# Contract: Language-Model Boundary

**Feature**: `001-per-user-message-ordering` | **Date**: 2026-08-24
**Plan**: [../plan.md](../plan.md) | **Research**: [../research.md](../research.md) (R-011)

This is the Principle IV boundary. It lives in `src/LLM.Abstraction`, which references no provider
and no ASP.NET Core types. `src/Middleware` references the abstraction; only the composition root
knows a concrete provider exists. The separation is enforced by assembly references rather than by
convention, so a future change cannot quietly reach past it.

## Interface

```csharp
namespace LLM.Abstraction;

/// Submits a message for answering. Returns as soon as the provider has accepted the
/// submission — never when the answer is ready.
public interface ILlmClient
{
    Task SubmitAsync(LlmSubmission submission, CancellationToken cancellationToken);
}

public sealed record LlmSubmission(
    Guid   MessageId,
    string UserId,
    string Content,
    Uri    CallbackUri);

public sealed record LlmCompletion(
    Guid    MessageId,
    bool    Succeeded,
    string? Answer,
    string? Error);
```

`LlmCompletion` describes the payload the provider posts back; it is defined here so the callback
shape belongs to the boundary rather than to the web layer.

## Obligations on the caller (middleware)

1. **Claim before submitting.** `SubmitAsync` is called only for a message already committed as
   `Processing`. The claim is the exclusivity guarantee; submitting first would allow two in flight
   for one user.
2. **Never hold a transaction or lock across the call.** The claim commits, then submission happens
   outside any transaction (Principle IV). This is stated as an obligation because the inline claim
   path runs inside a request handler, where wrapping both in one transaction is the natural mistake.
3. **Treat a throw as "no callback is coming."** No compensating update, no rollback to `Pending`.
   The message stays `Processing` and claim expiry resolves it (FR-013), which is the same path a
   silently-lost callback takes — one recovery mechanism, not two.
4. **Never log `Content`.** It crosses this boundary and must not appear in logs at any level
   (FR-021, R-009).
5. **Pass a callback URI the provider can actually reach.** It is configuration, not a constant: in
   the composed environment each instance points at its peer (R-011).

## Obligations on the implementation (provider)

1. **Return promptly.** Return once the submission is accepted. Answering takes as long as it takes;
   the caller is not waiting.
2. **Deliver completion by HTTP POST to `CallbackUri`** with an `LlmCompletion` body (FR-005). Not
   by an in-process call — the callback must genuinely cross a process boundary, which is the whole
   point of FR-006.
3. **At-most-once delivery.** Exactly one attempt, never a retry (FR-019a). A failed POST is logged
   without content and dropped.
4. **Any instance is a valid target.** The provider knows nothing about which instance submitted
   the message and must not try to find out.

## Fake provider behaviour

`src/LLM.FakeProvider` is the only implementation. It is in-process — not a separate service, per
the spec's non-functional constraints — and schedules one delayed POST.

| Option | Effect |
|--------|--------|
| `Mode = Respond` | After `Delay`, POST `Succeeded = true` with a synthetic answer. The default. |
| `Mode = Fail` | After `Delay`, POST `Succeeded = false` with an error string. Exercises the failed-state queue advance (FR-016). |
| `Mode = NeverRespond` | Accept the submission and never post. Sets up claim expiry (scenario 6) and the held-message case in SC-004, with no change to the code under test. |
| `Delay` | How long before posting. Milliseconds in tests, seconds in the composed demonstration. |
| `CallbackBaseUri` | Where to post. Each composed instance is given its peer's address. |

The synthetic answer is derived from the message identifier, not from the content, so nothing that
must stay out of logs can arrive in one by way of an echoed answer.

### Why no control endpoint

Tests drive the callback path by posting to `POST /callbacks/llm` themselves — the real endpoint,
with the real payload, aimed at whichever instance the scenario calls for. A test-only control API
would prove the control API works while bypassing the path under test, and it would add production
surface for a test's convenience.

## Mapping to requirements

| Requirement | Where it is discharged |
|-------------|------------------------|
| FR-005 asynchronous completion via HTTP callback | Provider obligation 2 |
| FR-006 any instance may receive the callback | Provider obligation 4; peer-pointing in R-011 |
| FR-019 substitutable boundary, no separate stub service | This interface; in-process fake |
| FR-019a at-most-once delivery | Provider obligation 3 |
| FR-013 expiry as the sole recovery path | Caller obligation 3 |
| FR-021 content never logged | Caller obligation 4; synthetic answers |
| Principle IV no lock or open request across the wait | Caller obligation 2 |
