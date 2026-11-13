# Coding Standards

How code in this package is designed and written. The standards apply to every change, whether a person or an AI assistant writes it. Optimize for correctness, safety, explicit requirements, project constraints, simplicity, clarity, measured performance, brevity, then consistency.

## 1. Before A Change

Start from observable behavior and acceptance criteria. Existing code and tests are evidence; do not normalize toward conventions from other codebases. Preserve local architecture unless it causes the requested problem. Keep minor reversible assumptions explicit; ask only when uncertainty materially changes behavior or risk.

Before proposing structure, write down a concise decision record: current ownership, impossible states, dependency direction, boundary failures, and proof.

## 2. Overengineering

New structure must earn its exact shape, not merely the change around it. A real boundary can justify one concrete module without justifying an interface, factory, adapter hierarchy, or dependency-injection system. State can justify an owner without a service object.

Default moves:

- keep clear linear code together;
- modify the owner instead of wrapping it;
- use functions and concrete values before classes or interfaces;
- keep helpers local until semantic reuse exists;
- pass concrete dependencies until multiple implementations need a contract;
- remove unused private extension points after checking hidden consumers.

Repeated syntax alone neither requires nor forbids extraction. Extract a shared invariant, concept, boundary, or independently changing behavior. Inlining is better when it removes a shallow rename without exposing complexity.

## 3. Rewrites

A rewrite is broad when it replaces ownership, public behavior, persistence meaning, dependency direction, or most of an existing unit. New requested behavior is not permission to rewrite unrelated structure. When a rewrite is approved, keep its boundary and compatibility plan explicit; otherwise use the smallest safe owner-local change.

## 4. Design Evidence

Name the system responsibility, state owner, trust boundary, expensive decisions, failure semantics, and proof. Make invalid states hard to represent. Complexity belongs at the lowest owner with enough policy context to resolve it correctly.

## 5. Architecture

Architecture is ownership, dependency direction, persistence, failures, public APIs, and integration boundaries—not folder shape.

### Dependency Direction

Keep stable policy independent of volatile UI, storage, network, platform, framework, and generated details when that separation protects a real decision. A simple workflow may call one concrete mechanism directly. Composition may know many modules but should own little behavior. Avoid cycles between independently changing owners.

### Structure

Use feature slices when behavior changes feature-by-feature; layers for cohesive engines, compilers, protocols, or small apps; another shape when local forces justify it. Keep common changes local and mechanisms pointing toward stable policy.

### Ownership

One owner defines each important state's type, invariants, mutations, and persistence meaning. Other modules request behavior rather than copy rules. Transport, storage, and view shapes match domain types only when their meaning truly matches.

## 6. Modules And APIs

A good module hides complexity or protects ownership behind a smaller meaningful API. A wrapper that forwards the same operation is not a layer.

Public APIs expose domain meaning and caller-relevant semantics such as freshness, idempotency, deadlines, cancellation, consistency, and typed failure. They hide storage, transport, retry, cache, parsing, and platform details. Preserve public compatibility or deprecate intentionally.

## 7. Patterns

Patterns are conditional tools. Use MVVM, MVC, Strategy, Observer, Adapter, Factory, Repository, Command, actors, state machines, or dependency injection only for a present force they solve: state/lifecycle ownership, framework discovery, multiple current variants, a hard boundary, or proven duplication. Do not add them for folder symmetry or hypothetical change.

## 8. Data, Types, And Failure

Design data first. Prefer meaningful types over strings, flags, generic bags, and unowned globals. Distinguish:

- normal absence with the language's explicit convention;
- expected failure with an idiomatic typed/result form callers need not parse;
- programmer-invalid states by stopping the current operation;
- corrupt external state with a boundary error and preserved evidence.

Do not silently repair parser, protocol, persistence, indexing, or security input when callers need to know it was invalid. Forgiving presentation helpers may normalize only when invalidity is irrelevant.

## 9. Implementation Discipline

Validate at the trust boundary or invariant owner. Retry only where idempotency, cancellation, transaction state and the prior outcome are known; never stack retries or retry unsafe effects. Map failures where callers need a different meaning. Keep platform branches narrow.

Prefer immutability and local mutation. Use async for I/O; offload CPU work only for a measured or structurally obvious constraint. Optimize measured bottlenecks, not imagined ones. Dependencies for difficult standards require a named local risk and review.

Use precise names; generic `Manager`, `Service`, `Helper`, `Util`, `Handler`, `Processor`, `Data`, or `Info` must state a real responsibility. Comments explain reasons, invariants, and constraints—not syntax.

## 10. Tests

Cover domain rules, application behavior, important failures, and boundary mapping. Assertions must distinguish plausible wrong outcomes; order tests require unsorted data, and failure tests must prove the correct owner/signal.

Use real temporary storage at filesystem boundaries and small fakes at network, time, randomness, process, platform, or third-party boundaries. Do not mock internal choreography or add interfaces for mocks. Test observable behavior around generated or third-party code, not trivial holders.

Tests must be deterministic and fast. Serialize only inherently process-wide or shared resources. A flaky test is broken. TDD is optional; proportionate repeatable evidence is mandatory.

## 11. Starting Or Evolving A Codebase

For a new codebase: define boundary, concepts, state, invariants, dependency direction, real mechanisms, then implement one tested end-to-end behavior. Add a contract only when the first real boundary or current variant earns it.

For an existing codebase: understand its structure, make the smallest owner-local change, update affected failures and proof, and defer unrelated architecture, cleanup, data-shape, and performance work.

## 12. Failure Modes To Avoid

Reject nearby behavior, speculative extension points, shallow wrappers, decorative DTOs/mappers/factories, one-implementation interfaces, generic coordinators, internal mocks, trivial dependencies, paraphrasing comments, split linear logic, defensive handling of impossible states, tutorial architecture, unapproved rewrites, and unchecked deletion of extension points.

A ready change satisfies requested behavior and scope, has clear ownership and intentional failures, keeps mechanisms from leaking into policy without reason, and records proportionate final-state verification.
