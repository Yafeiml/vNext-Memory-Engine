# Architecture

vNext Memory Engine is a distributed, governed memory platform for AI agents.

## Deployment topology

```text
Codex / Claude / Cursor / OpenClaw / custom agents
                         |
                   local MCP/REST
                         |
                  VNext.Memory.Edge
          (identity, outbox, future hooks/cache)
                         |
                     HTTPS
                         |
                  VNext.Memory.Core
       (MCP gateway, governance, scope, retrieval)
                         |
                    PostgreSQL
```

The Core and Edge are independently deployable. A user or workload registers one
Edge device credential. Individual agents do **not** receive separate Core API keys;
their names are recorded only as provenance.

## Write path

```text
observation
  -> secret and trust gates
  -> evidence event
  -> memory candidate
  -> autonomous admission controller
  -> evidence-only / quarantine / probation / active
  -> versioned claim
```

`Candidate` is an internal processing state, not an approval notification. Low-risk,
well-supported memories are activated automatically. Human review is reserved for
permission grants, dangerous procedures, sensitive information, and unresolved
high-impact conflicts.

## Scope model

A memory can be scoped by:

- tenant
- user
- project
- repository
- branch
- worktree
- environment
- task

The actor that observed a fact is provenance, not scope. A Codex observation and a
Claude observation about the same repository contribute evidence to the same logical
project memory space.

The resolver starts inferred memories at the narrowest defensible scope. For
override-style memories such as user preferences, the most specific applicable scope
wins. Additive memories such as failed approaches remain as a set.

## Initial consistency rules

- Evidence is append-only.
- Exact duplicate claims reinforce one claim and link new evidence.
- Exact-scope keyed technical facts and decisions use temporal supersession.
- Hypotheses can coexist until evidence validates or contradicts them.
- Secrets are rejected before raw content is persisted.
- External untrusted content is quarantined and never returned by normal search.
- Memory supplies context; it never grants operational permission.

## Deliberate MVP limits

The first slice does not yet include:

- LLM-based automatic extraction from full transcripts
- semantic embedding retrieval
- object storage for large transcripts
- OIDC login and delegated workload identity
- admin review UI
- outcome-based promotion/decay
- code/Git verification workers
- cached offline reads at the Edge

Those capabilities can be added without changing the evidence/claim/admission model.
