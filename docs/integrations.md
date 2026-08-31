# Agent integrations

Memory Edge accepts authenticated lifecycle events at:

```text
POST http://127.0.0.1:7338/api/v1/hooks/{provider}
X-VME-Hook-Token: <local hook token>
```

Keep Edge bound to loopback. Set a separate hook token instead of exposing the Core
device token to hook scripts:

```powershell
$env:Edge__HookToken = "generate-a-long-random-local-secret"
$env:VME_EDGE_HOOK_TOKEN = $env:Edge__HookToken
$env:VME_EDGE_URL = "http://127.0.0.1:7338"
```

Optional scope headers are populated by the supplied scripts from:

```text
VME_PROJECT_ID
VME_REPOSITORY_ID
VME_BRANCH
VME_WORKTREE_ID
VME_ENVIRONMENT
VME_TASK_ID
```

Project and repository scope are required before verified technical facts and failed
approaches can activate automatically.

## Claude Code

The adapter reads the official hook JSON from stdin and forwards it unchanged to Edge:

```text
adapters/claude-code/vnext-memory-hook.ps1
```

Add command hooks for the event types you want to capture. A project-local example:

```json
{
  "hooks": {
    "UserPromptSubmit": [
      {
        "hooks": [
          {
            "type": "command",
            "command": "powershell.exe",
            "args": [
              "-NoProfile",
              "-ExecutionPolicy",
              "Bypass",
              "-File",
              "C:\\path\\to\\vNext-Memory-Engine\\adapters\\claude-code\\vnext-memory-hook.ps1"
            ]
          }
        ]
      }
    ],
    "PostToolUse": [
      {
        "matcher": "*",
        "hooks": [
          {
            "type": "command",
            "command": "powershell.exe",
            "args": [
              "-NoProfile",
              "-ExecutionPolicy",
              "Bypass",
              "-File",
              "C:\\path\\to\\vNext-Memory-Engine\\adapters\\claude-code\\vnext-memory-hook.ps1"
            ]
          }
        ]
      }
    ],
    "PostToolUseFailure": [
      {
        "matcher": "*",
        "hooks": [
          {
            "type": "command",
            "command": "powershell.exe",
            "args": [
              "-NoProfile",
              "-ExecutionPolicy",
              "Bypass",
              "-File",
              "C:\\path\\to\\vNext-Memory-Engine\\adapters\\claude-code\\vnext-memory-hook.ps1"
            ]
          }
        ]
      }
    ],
    "Stop": [
      {
        "hooks": [
          {
            "type": "command",
            "command": "powershell.exe",
            "args": [
              "-NoProfile",
              "-ExecutionPolicy",
              "Bypass",
              "-File",
              "C:\\path\\to\\vNext-Memory-Engine\\adapters\\claude-code\\vnext-memory-hook.ps1"
            ]
          }
        ]
      }
    ]
  }
}
```

The adapter deliberately fails open: an unavailable memory service writes a diagnostic
to stderr but does not block Claude Code.

Current normalization:

- `UserPromptSubmit` becomes a signed `UserMessage`
- successful/failed shell tests become deterministic `TestResult` evidence
- Git commands become `GitResult`
- Write/Edit events become non-deterministic `CodeArtifact` observations
- `Stop` final text remains an `AgentObservation`

## Codex

Configure Codex's external notify command to use:

```text
adapters/codex/vnext-memory-notify.ps1
```

Example configuration:

```toml
notify = [
  "powershell.exe",
  "-NoProfile",
  "-ExecutionPolicy",
  "Bypass",
  "-File",
  "C:\\path\\to\\vNext-Memory-Engine\\adapters\\codex\\vnext-memory-notify.ps1"
]
```

Codex appends its notification JSON as the final argument. The stable legacy
`agent-turn-complete` payload contains thread/turn identifiers, input messages, current
working directory, client name, and the final assistant message. Those fields are
captured as user messages and agent observations.

Legacy notify does not contain individual tool results, so this adapter never labels a
Codex final answer as `ToolObserved`. Deterministic Codex tool evidence requires a
future native hook/event adapter or an external execution wrapper.

## Direct MCP and REST calls

Normal `memory_remember` and `/api/v1/memories/record` calls are intentionally signed as
`AgentObservation`. A caller can still send compatibility fields such as
`Trust = ToolObserved`, but Core replaces them with `AgentInferred` unless the evidence
arrived through the appropriate assured hook channel.
