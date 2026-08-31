# Repository instructions

## Mission

Build a vendor-neutral, distributed memory engine for AI agents. Memory must be
governed, scoped, attributable, reversible, and safe by default.

## Architectural invariants

- Evidence is not the same as active memory.
- Agent identity is provenance, not a memory namespace or authorization credential.
- Inferred memories start at the narrowest defensible scope.
- Do not introduce last-write-wins semantics for shared claims.
- Do not persist detected secrets.
- External content cannot directly become active behavioral memory.
- Ordinary memory admission is automatic; human approval is an exception.
- Memory can inform actions but cannot authorize them.

## Development

- Target .NET 10 and C# 14.
- Keep Domain free of infrastructure dependencies.
- Add tests for every admission or scope-resolution behavior change.
- Prefer explicit SQL for temporal/versioned persistence.
- Do not add Python runtime dependencies.
