# ADR-0004: Writes run as one guarded call; safety tokens are retired

**Status:** Accepted
**Decided:** 2026-09-01 to 2026-10-21   **Recorded:** 2026-10-30
**Area:** write-safety
**Current home:** [ARCHITECTURE §8: guarded write pipeline](../ARCHITECTURE.md#guarded-write-pipeline-foundation-and-lifecycle); [PLC writes](../ARCHITECTURE.md#plc-writes-plc_write); [Lifecycle continuity](../ARCHITECTURE.md#lifecycle-continuity); [PLC reference](../SupportedOperations/PLC_OPERATIONS_SUMMARY.md)

## Context
- The first write-safety design was a generic `preview_write_batch` that returned an apply token bound to a hash of a typed state snapshot. PRs 1-6 hardened it with preview diffs and per-operation snapshot scopes.
- A token gives no consent: the agent previews and applies with `confirm=true` in the same turn, so it confirms itself. MCP places consent in the client.
- The state hash was fragile by construction (volatile export fields had to be stripped), dependent items could not be batched (#79), preview quality was uneven (#78), and snapshot readers grew with every operation.
- A probe of the installed V21 `OnlineProvider` found no public PLC Start/Stop and no authoritative RUN/STOP read, so the reflected control calls could not be trusted.

## Decision
- Every write is one call through a shared pipeline: validate, verified binding under a pinned lease, plan, guards, stop here on `dryRun:true`, mutate, verify, audit. A `dryRun` flag replaces the preview tool; the consequence report becomes the `effects` of both dry runs and applied writes.
- Stale-state protection is a required, format-tagged `expectedContentHash` (If-Match style) on content replacement only, checked at plan time and again by the worker.
- Asking the user uses MCP elicitation, the only mechanism that reached the user in every client tested (Claude Code, Codex CLI, MCP Inspector; Codex in unrestricted mode declines without a dialog). The `anthropic/requiresUserInteraction` marker was dropped because only Claude Code honoured it. Anything other than an explicit accept denies, and the server never claims a human saw the prompt. Which tools elicit follows the access mode ([ADR-0007](0007-access-tiers-lifecycle-confirmation-and-binding.md)).
- Safety tokens, the token core, `SafetyRead` snapshot readers, the generic batch tools, the legacy write audit stream and PLC start/stop are removed. Generic batches split into `plc_read`, `read_cross_references` and `plc_write`.
- PLC header metadata is read through typed `PlcBlock` properties only, never `GetAttribute`, which can throw over remoting and stall.
- Follow-up contracts on the same pipeline:
  - Rebind never discards unsaved work: a modified worker-owned source is refused at preview and re-checked immediately before close; nothing is auto-saved; unreadable state fails closed; a modified UI-owned source may stay open.
  - Response budgeting is one shared `GuardedWriteResponseBudget` with typed per-domain adapters that keep their own omission priorities and guard reserves.
  - A retired request field is rejected by the generic unknown-member rule, not by a retired-member list.
- Rejected alternatives:
  - A per-tool forced-approval `_meta` marker: honoured by one client only.
  - A separate preview tool per surface: naming churn.
  - A per-domain budget copy in each new domain: four copies of one algorithm already carried the same defect (B-10).

## Consequences
- Safety rests on validation, transparent effects, content hashes and client-side consent, not on a server-enforced token. Stale-state protection outside content hashes is limited, because the agent and the maintainer share the open project.
- New write domains build on `Safety/Pipeline`; tokens, snapshot readers and a `SafetyRead` catalog must not return. Network, PLC, HMI and VCI all use the pipeline.
- Clients that prompt on destructive tools also prompt on dry runs. PLC start/stop was removed.
- Later narrowed by [ADR-0012](0012-portal-as-rejection-authority.md): guard severities reduced to block/info outside lifecycle, and collision guards, replanning and read-back removed. The per-domain budget priorities and reserves are in [ARCHITECTURE](../ARCHITECTURE.md).

## Evidence
The plans, specs and live reports behind this decision belong to the project this repository started from and are not part of it.
