# ADR-0008: Tool surface rules are shared and derived once, not repeated per tool

**Status:** Accepted
**Decided:** 2026-10-21 to 2026-10-24   **Recorded:** 2026-10-30
**Area:** process
**Current home:** [ARCHITECTURE: batch and network execution](../ARCHITECTURE.md#7-batch-and-network-execution); [ARCHITECTURE: write safety](../ARCHITECTURE.md#8-write-safety); [Network paging matrix](../SupportedOperations/NETWORK_OPERATIONS_SUMMARY.md#paging-matrix)

## Context
- After the structured tools landed, argument handling, batch limits, cursor codecs, guard definitions and worker method lists had each been copied per domain and had drifted.
- Removing `projectPath` from the content tools (the bound-project change, see [ADR-0007](0007-access-tiers-lifecycle-confirmation-and-binding.md)) would have left a stale root `projectPath` silently ignored on seven tools, so a call such as `close_project({projectPath: other})` would have closed the bound project.
- A JSON `null` on an optional boolean read as "omitted" would have made `dryRun: null` execute a write.
- The work was a set of small, separately merged items (Workstream C) whose order mattered because they touched the same files.

## Decision
- **Closed arguments first.** Allowed root keys are derived from each tool's input schema by one wrapper, `StrictArgumentsTool`, applied to all 19 tools. It rejects unknown or mistyped root keys, retired and unknown item members, and missing required arguments with the tool's own canonical `validation_error` document, `isError:true` and no audit record. This lands before the `projectPath` removal so retired members are rejected automatically.
- **Null is not "omitted".** JSON null is accepted only where the schema advertises it (today `bind_project.forceRebind`); everywhere else it is a shape error. No `contractVersion` bump.
- **One batch rule set.** `OperationBatchRules` owns the item-count and per-item ID checks for every batch catalog and the write pipeline; one `OperationValidation` result replaces four ad hoc shapes, and an oversized ID is never echoed.
- **One cursor plumbing.** Hardware-page, project-tree, HMI and network-object cursors seal and open through one `AuthenticatedCursorProtector` helper with one exception type and one binding-state rule: unbound cursors keep their epoch everywhere. The network-object cursor wrapper is host-sealed ([ADR-0003](0003-network-domain-identity-and-subnet-lifecycle.md), C-5).
- **Guard and method definitions are single-sourced.** `WriteGuardCatalog.NonInteractive` builds the PLC, Network, HMI and VCI guard sets so a misplaced acknowledge guard fails at type initialisation; a shared precondition-refusal set lives in `WorkerFailureCategories`; HMI worker method names live in Contracts lists that the policy, capability catalog and worker dispatch all read.
- **Read cancellation is item-boundary only.** The four batch read tools check the request token before each item; an in-flight worker call is not cancelled and the worker timeout bounds it.
- **Paging defaults.** Hardware cursor-only continuations and network-object pages default to 100, with explicit sizes and the maximum unchanged.
- Rejected: enforcing "no acknowledge guard" in the pipeline from `ConfirmsEveryCall=false` (the pipeline's domain interface carries no guard definitions and it would constrain test fakes); a `TransportLoss` category set (one consumer); putting the unbound gate in `ProjectSessionBinding.TryResolveWithSnapshot` (several callers must work unbound); cancelling in-flight worker calls.

## Consequences
- Argument and error shape are uniform across tools, and a shape error never reaches audit, the worker or elicitation.
- A new tool or write domain must register through the strict-argument table, call `OperationBatchRules`, build guards with `NonInteractive` and use the shared cursor helper; do not add a per-domain copy.
- Refactor items aimed to be behavior-preserving, so existing tests act as the regression net; the deliberate behavior changes were the null rule, the unbound-cursor epoch rule and the paging defaults.
- Follow-ups closed the remaining gaps afterward: explicit null for non-nullable arguments, bounded diagnostic names for operation and root keys, size-aware handling of oversized tree/HMI cursor issuance, the worker identity backstop for content reads, and rejection of unbound explicit-path resolution.
- Evidence limit: the items were qualified offline (builds, full suites, independent review). The only native evidence is the scoped network-object cursor smoke; no paging combination, later follow-up or merge candidate was separately re-qualified against a live Portal.

## Evidence
The plans, specs and live reports behind this decision belong to the project this repository started from and are not part of it.
