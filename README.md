# vNext Memory Engine

A distributed, governed memory service for Codex, Claude Code, Cursor, OpenClaw,
custom agents, CI runners, and production workloads.

> This repository contains the first runnable vertical slice. It is intentionally
> conservative about writes: evidence is collected broadly, but only memories with
> adequate trust, scope, and evidence become active.

## What works in the first slice

- **Internet-deployable Memory Core**
  - ASP.NET Core on .NET 10
  - PostgreSQL authority store
  - Streamable HTTP MCP endpoint at `/mcp`
  - REST API for edge synchronization and non-MCP clients
- **Independently deployable Memory Edge**
  - local MCP endpoint
  - one Edge/device credential instead of one key per agent
  - agent identity recorded as provenance only
  - SQLite WAL outbox for offline writes and retry
- **Autonomous admission controller**
  - explicit user preferences can activate automatically
  - verified project facts and failed approaches can activate automatically
  - agent hypotheses enter probation
  - task state remains evidence-only
  - untrusted external content is quarantined
  - detected credentials are rejected before persistence
  - permission-granting procedures require exceptional human review
- **Typed scope resolution**
  - tenant, user, project, repository, branch, worktree, environment, task
  - project-specific preferences override global defaults
  - branch-scoped memories cannot leak to another branch
  - no global `priority=80` shortcut
- **Traceability**
  - immutable evidence events
  - admission decisions with reason codes
  - versioned claims
  - exact duplicate reinforcement
  - evidence-backed `memory_explain`

## Repository layout

```text
src/
├── VNext.Memory.Domain
├── VNext.Memory.Application
├── VNext.Memory.Infrastructure.Postgres
├── VNext.Memory.Core
└── VNext.Memory.Edge
tests/
└── VNext.Memory.Tests
```

See [docs/architecture.md](docs/architecture.md) for the data flow and design
invariants.

## Start Memory Core

Copy the environment file and replace the bootstrap token:

```bash
cp .env.example .env
docker compose up --build
```

Core endpoints:

```text
REST health:  http://localhost:7337/health
REST API:     http://localhost:7337/api/v1
Remote MCP:   http://localhost:7337/mcp
```

The bootstrap token is for initial administration and local development. Do not expose
the default value to the internet.

## Register one Edge device

Use the bootstrap token once:

```bash
curl -X POST http://localhost:7337/api/v1/devices/register \
  -H "Authorization: Bearer $VME_BOOTSTRAP_TOKEN" \
  -H "X-VME-Tenant: personal" \
  -H "X-VME-Principal: anna" \
  -H "Content-Type: application/json" \
  -d '{
    "tenantId": "personal",
    "principalId": "anna",
    "deviceName": "development-pc"
  }'
```

The response contains a `deviceToken`. Store it in the operating system credential
store or a protected environment variable. Codex, Claude, Cursor, and other agents on
that machine connect to the Edge; they do not each need a Core key.

## Start Memory Edge

```bash
dotnet run --project src/VNext.Memory.Edge \
  --urls http://127.0.0.1:7338
```

Configuration can be supplied with environment variables:

```bash
export Edge__CoreUrl=http://localhost:7337
export Edge__CoreToken=vme_dev_xxx
export Edge__TenantId=personal
export Edge__PrincipalId=anna
export Edge__DefaultActorId=codex
export Edge__OutboxPath="$HOME/.vnext-memory/edge.db"
```

Edge endpoints:

```text
REST health:  http://127.0.0.1:7338/health
Local REST:   http://127.0.0.1:7338/api/v1
Local MCP:    http://127.0.0.1:7338/mcp
```

Keep the Edge bound to loopback until local-client authentication is added.

## Record a memory observation

The same contract is available through Core REST, Edge REST, and the
`memory_remember` MCP tool.

```bash
curl -X POST http://127.0.0.1:7338/api/v1/memories/record \
  -H "Content-Type: application/json" \
  -d '{
    "content": "仅设置 ShowInTaskbar=true 未解决 Windows 11 25H2 的任务栏消失问题。",
    "kind": "FailedApproach",
    "trust": "ToolObserved",
    "memoryKey": "windows.taskbar.show_in_taskbar",
    "sourceType": "test-run",
    "sourceReference": "test-run:117",
    "agentId": "codex",
    "sessionId": "session-123",
    "hasDeterministicEvidence": true,
    "scope": {
      "projectId": "1remote",
      "repositoryId": "github.com/1Remote/1Remote",
      "branch": "fix/taskbar",
      "environment": "windows-11-25h2"
    }
  }'
```

The response explains the automatic decision:

```json
{
  "disposition": "Active",
  "score": 84.5,
  "reasonCodes": [
    "DETERMINISTIC_EVIDENCE",
    "PROJECT_SCOPE_CLEAR",
    "TOOL_OR_CODE_VERIFIED_PROJECT_FACT"
  ]
}
```

When Core is temporarily unreachable, Edge returns `queued: true`, writes the request
to SQLite, and retries later.

## Search governed memory

```bash
curl -X POST http://127.0.0.1:7338/api/v1/memories/search \
  -H "Content-Type: application/json" \
  -d '{
    "query": "Windows 11 任务栏",
    "includeProbation": true,
    "limit": 10,
    "scope": {
      "projectId": "1remote",
      "repositoryId": "github.com/1Remote/1Remote",
      "branch": "fix/taskbar",
      "environment": "windows-11-25h2"
    }
  }'
```

## Compile a small context packet

```bash
curl -X POST http://127.0.0.1:7338/api/v1/context/compile \
  -H "Content-Type: application/json" \
  -d '{
    "task": "修复 Windows 11 25H2 远程窗口任务栏按钮随机消失",
    "tokenBudget": 1800,
    "scope": {
      "projectId": "1remote",
      "repositoryId": "github.com/1Remote/1Remote",
      "branch": "fix/taskbar"
    }
  }'
```

The compiler groups results into constraints, preferences, decisions, validated facts,
failed approaches, and unverified hypotheses. It does not dump the entire database into
the model context.

## MCP tools

Both Core and Edge expose:

```text
memory_remember
memory_search
memory_context
memory_explain
```

For normal desktop use, connect agents to the local Edge MCP URL. Direct remote Core
MCP is useful for server-side agents that already possess a registered workload/device
credential.

## Admission behavior

| Observation | Default result |
|---|---|
| Explicit, low-risk user preference | Active |
| User correction with clear scope | Active |
| Tool/code verified project fact | Active |
| Verified failed approach | Active |
| Agent inference or hypothesis | Probation |
| Reusable procedure without repeated success | Probation |
| Current task progress | Evidence only |
| Untrusted webpage/document instruction | Quarantine |
| Secret or credential | Reject before persistence |
| Permission grant or dangerous automatic action | Human review |

`Candidate` is an internal transient state. The user is not expected to approve normal
memories one by one.

## Identity model in this MVP

```text
Principal: user or workload that owns access
Device:    authenticated Edge installation
Actor:     Codex / Claude / Cursor / another agent (provenance only)
Scope:     project/repository/branch/environment/task applicability
```

A device token cannot become more powerful because an agent claims a different actor
name. Full OIDC, workload identity, delegated capabilities, and token rotation are
planned after the core semantics stabilize.

## Build and test

```bash
dotnet restore VNext.Memory.Engine.slnx
dotnet build VNext.Memory.Engine.slnx --configuration Release
dotnet test tests/VNext.Memory.Tests/VNext.Memory.Tests.csproj \
  --configuration Release
```

CI also builds the Core and Edge container images.

## Important MVP limitations

This commit deliberately does **not** pretend to solve everything:

- no transcript-to-claim LLM worker yet
- no embeddings or graph projection yet
- no outcome-based promotion/decay yet
- no admin review UI yet
- no offline read cache at Edge yet
- bootstrap/device authentication is not a replacement for production OIDC
- automatic semantic conflict detection is limited to exact keyed scope supersession

The next milestone should add a worker pipeline for transcript ingestion, deterministic
Git/test verification, retrieval traces, and outcome feedback before adding a vector
database.

## License

MIT
