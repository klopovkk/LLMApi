# Feature Specification: Per-User Message Ordering and Exclusivity

**Feature Branch**: `001-per-user-message-ordering`

**Created**: 2026-08-24

**Status**: Draft

**Input**: User description: "FR-001 accept multiple messages from the same user without client-side synchronization; FR-002 same-user messages begin processing in acceptance order; FR-003 at most one message per user in active LLM processing; FR-004 different users processed concurrently; FR-005 LLM completion communicated via HTTP callback; FR-006 callback routable to any instance and completes the message regardless of which instance submitted it; FR-007 ordering and exclusivity preserved across concurrent instances; FR-008 processing state persisted so coordination does not depend on in-memory application state. Non-functional: .NET 9+, at least two application instances, local execution via Docker Compose, no real LLM integration, no separate LLM stub service, no authentication required."

## User Scenarios & Testing *(mandatory)*

### User Story 1 - Send messages without coordinating on the client (Priority: P1)

A client application sends several messages for the same user in quick succession — possibly
faster than the language model can answer them. The client does not wait, does not serialize
its own calls, and does not know how many messages for that user are already in flight. Every
message is accepted straight away and acknowledged with an identifier the client can use to
follow it. Behind that acknowledgement, the service starts the messages one at a time, in the
order it accepted them, and never lets a second message for that user begin while an earlier
one is still being answered.

**Why this priority**: This is the point of the feature. Without it, the burden of
serialization stays with the client, which is exactly the burden being removed. Accept plus
ordered, exclusive processing for one user is the smallest slice that delivers real value, and
every other story builds on it.

**Independent Test**: Submit N messages for one user as fast as the transport allows, then
observe the sequence in which processing starts and the set of messages simultaneously in
progress. Fully testable with a substituted language-model boundary and a single client, and it
delivers the "no client-side synchronization" guarantee on its own.

**Acceptance Scenarios**:

1. **Given** a user with no messages in progress, **When** the client submits three messages in
   rapid succession, **Then** all three submissions are accepted and acknowledged without the
   client waiting for any earlier message to finish.
2. **Given** three messages accepted for one user in the order A, B, C, **When** processing
   proceeds, **Then** processing starts for A before B, and for B before C.
3. **Given** message A is being answered, **When** message B is next in line for the same user,
   **Then** B does not begin until A has reached a final state.
4. **Given** message A has just reached a final state, **When** message B is waiting for the
   same user, **Then** B begins without any further client action.
5. **Given** a user with an accepted message, **When** the client asks about that message,
   **Then** the client can tell whether it is waiting, being answered, or final, and can read
   the answer or the failure reason once it is final.

---

### User Story 2 - Users do not block each other (Priority: P2)

Many users are active at the same time. One user's slow message must not delay anybody else:
work for distinct users proceeds in parallel, limited only by available capacity, and a user
waiting a long time for an answer has no effect on other users' throughput.

**Why this priority**: Exclusivity is worthless if it is achieved by serializing everything.
This story is what separates a per-user guarantee from a global bottleneck, but it is only
meaningful once P1 exists.

**Independent Test**: Hold one user's message unanswered and, while it is in progress, submit
messages for other users. Observe that those messages start and reach a final state without
waiting for the held one. Testable independently of instance count.

**Acceptance Scenarios**:

1. **Given** user X has a message in progress that has not completed, **When** users Y and Z
   submit messages, **Then** processing starts for both Y and Z while X's message is still in
   progress.
2. **Given** messages for many distinct users are accepted at once, **When** processing
   proceeds, **Then** the number of messages in progress simultaneously is limited by available
   capacity rather than by a single global one-at-a-time restriction.
3. **Given** user X's message is failing or stalled, **When** other users continue submitting,
   **Then** those users' messages are unaffected in both ordering and completion.

---

### User Story 3 - Guarantees survive multiple instances and restarts (Priority: P3)

The service runs as at least two instances behind a load balancer. Submissions for one user may
land on different instances, and a completion callback may arrive at an instance other than the
one that submitted the request. Ordering and exclusivity hold regardless, the callback completes
the right message wherever it arrives, and restarting an instance leaves recorded message state
intact and consistent.

**Why this priority**: This is the hardest and most valuable property of the system, but it is
verified on top of the behaviour established in P1 and P2, so it is sequenced last. It is not
optional — it is the reason coordination cannot live in process memory.

**Independent Test**: Run two instances against shared infrastructure. Submit interleaved
messages for one user across both instances, deliver each completion callback to the instance
that did not submit it, and restart one instance mid-flight. Observe that acceptance order is
still respected, that exclusivity is never broken, and that no message is lost or processed
twice.

**Acceptance Scenarios**:

1. **Given** two instances are running, **When** two messages for the same user are accepted by
   different instances, **Then** the earlier-accepted message begins processing first and the
   later one waits.
2. **Given** two instances are running, **When** both attempt to begin the same waiting message
   at the same moment, **Then** exactly one of them begins it and the other does not.
3. **Given** a message was submitted to the language model by instance 1, **When** the completion
   callback is delivered to instance 2, **Then** that message is completed with the returned
   answer and the user's next waiting message begins.
4. **Given** messages are waiting and in progress, **When** an instance is restarted, **Then**
   recorded message state is unchanged and unambiguous, processing resumes, and no message is
   processed twice or silently dropped.
5. **Given** one instance is stopped while holding no in-progress work, **When** further messages
   arrive for the same users, **Then** the remaining instance serves them with ordering and
   exclusivity intact.

---

### Edge Cases

- **Duplicate callback**: the same completion callback is delivered twice, by retry or by
  delivery to both instances. The message must be completed exactly once and the user's queue
  must advance exactly once.
- **Unknown or already-final callback**: a callback references a message that does not exist, or
  one that is already completed or failed. It must be answered without altering existing state,
  and must never release another message's or another user's turn.
- **State regression**: a late callback arrives for a message that has already been resolved by
  another route. A final state must never revert to an earlier state.
- **Failed answer**: the language model reports an error rather than an answer. The message
  reaches a final failed state and the user's next waiting message proceeds; a failure must not
  permanently block the user's queue.
- **Stalled processing**: a message is recorded as being answered but no callback ever arrives —
  for example the instance terminated between recording the claim and submitting the request. The
  claim expires after a bounded time, the message becomes failed, and the queue advances
  (FR-013).
- **Callback arriving after claim expiry**: the answer turns up later than the bound allowed. The
  expired message stays failed, and the callback must not revive it or interfere with whichever
  message is active for that user by then (FR-013b).
- **Empty or malformed submission**: a submission missing a user identifier or message content is
  rejected at acceptance and never enters a user's queue.
- **Deep queue**: a user accumulates many waiting messages. Acceptance keeps succeeding, ordering
  stays correct as the queue grows, and no waiting message is starved.
- **Callback racing the acknowledgement**: a callback arrives before the acceptance response has
  reached the client. It must still be handled correctly.
- **Simultaneous first arrivals on both instances**: a user with nothing in progress has messages
  accepted by both instances at the same moment. Exactly one may begin immediately; the other
  waits.

## Requirements *(mandatory)*

### Functional Requirements

FR-001 through FR-008 restate the supplied requirements. FR-009 onward are derived from them and
from the edge cases above.

- **FR-001**: The system MUST accept multiple messages for the same user without requiring the
  client to serialize, delay, or otherwise coordinate its submissions.
- **FR-002**: The system MUST begin processing messages belonging to the same user in the order in
  which it accepted them.
- **FR-003**: The system MUST allow at most one message per user to be in an active language-model
  processing state at any moment.
- **FR-004**: The system MUST allow messages belonging to different users to be processed
  concurrently, and MUST NOT serialize processing across users.
- **FR-005**: The system MUST learn of the completion of an asynchronous language-model operation
  through an inbound HTTP callback rather than by holding the submitting request open.
- **FR-006**: The system MUST complete the corresponding message when a callback is delivered to
  any instance, including an instance other than the one that submitted the request.
- **FR-007**: The system MUST preserve per-user ordering and per-user exclusivity when multiple
  instances accept and process messages concurrently.
- **FR-008**: The system MUST persist message processing state in shared storage so that
  coordination does not depend on in-memory application state, process-local locks, or client
  affinity to a particular instance.
- **FR-009**: The system MUST acknowledge each accepted submission with an identifier that
  uniquely and durably identifies that message.
- **FR-010**: The system MUST let a client retrieve, for a given message identifier, whether the
  message is waiting, being answered, or final, along with the answer or failure reason once it is
  final.
- **FR-011**: The system MUST record for each message an explicit processing state that
  distinguishes at minimum: accepted and waiting, actively being answered, completed with an
  answer, and failed.
- **FR-012**: The system MUST make the transition of a waiting message into the actively-being-
  answered state atomic with respect to concurrent attempts, such that exactly one attempt
  succeeds and no message is ever active twice.
- **FR-013**: The system MUST bound how long a message may remain in the actively-being-answered
  state. When that bound is exceeded without a completion callback, the system MUST release the
  claim automatically, record the message as failed with a reason indicating that no completion
  arrived, and allow the user's next waiting message to begin.
- **FR-013a**: The system MUST NOT re-submit a message whose claim expired. Expiry is a final
  failed state, so each accepted message is submitted to the language model at most once.
- **FR-013b**: The system MUST safely ignore a completion callback that arrives for a message
  whose claim has already expired, without reviving that message and without disturbing the
  message the user is processing by then.
- **FR-014**: The system MUST treat a repeated callback for an already-final message as a no-op,
  completing the message exactly once and advancing the user's queue exactly once.
- **FR-015**: The system MUST reject or safely ignore a callback that references an unknown
  message, without altering the state of any other message.
- **FR-016**: The system MUST begin the user's next waiting message, if any, promptly after the
  current message reaches a final state, whether that state is completed or failed.
- **FR-017**: The system MUST reject a submission that lacks a user identifier or message content,
  and MUST NOT create queued state for it.
- **FR-018**: The system MUST preserve recorded message state across the restart of any individual
  instance, and MUST resume ordered processing afterwards without duplicating or dropping
  messages.
- **FR-019**: The system MUST place language-model interaction behind a substitutable boundary so
  that all ordering, exclusivity, and callback behaviour can be exercised without a real
  language-model provider and without a separately deployed stub service.
- **FR-020**: The system MUST NOT require authentication or authorization for submission, status
  retrieval, or callback delivery.
- **FR-021**: The system MUST NOT record message content at informational severity or below, and
  MUST NOT place secret material in tracked configuration files.

### Key Entities

- **User**: the subject that owns an ordered stream of messages, identified by an opaque
  identifier supplied by the client. Users are independent of one another: the user is the unit of
  both ordering and exclusivity.
- **Message**: a single submitted request awaiting or holding a language-model answer. Carries its
  owning user, its content, its acceptance position within that user's stream, its explicit
  processing state, and — once final — its answer or failure reason.
- **Processing claim**: the durable record that a particular message is the one currently being
  answered for its user, which is what establishes that no other message for that user may start.
  Acquired atomically, held for a bounded time, and released when the message reaches a final
  state — either because a callback resolved it or because the bound elapsed.
- **Completion callback**: the inbound notification that a language-model operation has finished.
  Carries the message identifier it refers to and either an answer or a failure, and is meaningful
  to any instance that receives it.

## Success Criteria *(mandatory)*

### Measurable Outcomes

- **SC-001**: A client can submit 10 messages for one user with no waiting between submissions;
  all 10 are accepted, and the client performs no coordination of its own.
- **SC-002**: Across at least 100 messages submitted for a single user under load, the order in
  which processing starts matches the order of acceptance in 100% of cases.
- **SC-003**: At no observed moment in any run is more than one message for the same user in an
  actively-being-answered state — zero violations across all runs.
- **SC-004**: With one user's message deliberately held unanswered, messages submitted afterwards
  for 10 other users all reach a final state before that held message does.
- **SC-005**: When two instances race to start the same waiting message, exactly one succeeds —
  measured over at least 100 induced races, with zero double-starts and zero cases where neither
  instance starts it.
- **SC-006**: 100% of completion callbacks delivered to an instance other than the submitting
  instance complete the intended message and release that user's queue.
- **SC-007**: After restarting one of two running instances while messages are waiting and in
  progress, 100% of accepted messages still reach a final state exactly once, with acceptance
  order preserved.
- **SC-008**: The five required demonstration scenarios — single-user ordering, single-user
  exclusivity, multi-user concurrency, two-instance processing, and cross-instance callback
  handling — are each covered by an automated test, and the whole suite passes.
- **SC-009**: A reviewer can bring the full environment up locally with a single composed command
  and reproduce the two-instance scenarios without cloud resources, a real language-model
  provider, or a separately deployed stub service.
- **SC-010**: A callback delivered twice for the same message produces exactly one completion and
  exactly one advance of that user's queue.
- **SC-011**: A message claimed for processing whose callback never arrives is failed and its
  queue released within the configured bound in 100% of cases, including when the instance that
  claimed it was terminated — so no single stalled message blocks its user permanently.
- **SC-012**: A callback arriving after its message has expired leaves that message failed and
  leaves the user's currently active message untouched, in 100% of cases.

## Assumptions

- The client supplies the user identifier with each submission. Because no authentication is
  required, the system trusts that identifier as the ordering and exclusivity key rather than
  deriving it from a verified principal.
- Submission is acknowledged before an answer exists, and the client retrieves the answer
  afterwards by asking about the message identifier. No push channel to the client — webhook,
  streaming, or long-polling — is in scope.
- Ordering is defined by the moment the middleware accepts a message, which is the only ordering
  the middleware can observe. Client-side send order is not guaranteed to survive concurrent
  transport, so any total order consistent with acceptance is correct.
- "Actively being answered" begins when the message is claimed for language-model processing and
  ends when a final state is recorded. Time spent waiting in the user's queue is not active
  processing and is unbounded.
- A failed answer is a final state that releases the user's queue. Automatic retry of failed
  messages is not in scope, and neither is retry of messages failed by claim expiry (FR-013a):
  each accepted message is submitted at most once, so an expired message is reported to the
  client as failed rather than silently re-attempted.
- The bound on how long a message may stay actively-being-answered is a configurable duration
  rather than a fixed constant, so tests can drive expiry quickly while a running deployment can
  allow for realistic language-model latency. Choosing a specific default is a planning decision,
  not a requirement; the requirement is only that a bound exists and is enforced.
- A client that wants an expired message re-answered resubmits it, which enters the user's queue
  as a new message at the back. Recovery is therefore the client's choice, not an automatic
  behaviour that could reorder a user's stream.
- The substituted language-model boundary lives inside the application rather than as its own
  deployed service, and it can produce an out-of-band completion callback so that the callback
  path is exercised the way a real provider would exercise it.
- Shared infrastructure for durable state and coordination runs locally under Docker Compose
  alongside at least two application instances, and that composed environment is the target for
  demonstrating multi-instance behaviour.
- Message content is treated as sensitive for logging purposes even though no authentication
  protects the endpoints, consistent with the project's existing constraints on logging.
- How many distinct users are processed concurrently is an operational tuning concern, not a
  correctness requirement; the requirement is only that users are not serialized against one
  another.
