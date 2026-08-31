# Source Assurance

Source Assurance prevents a client from promoting its own statement merely by sending
`Trust = ToolObserved`, `IsCorrection = true`, or
`HasDeterministicEvidence = true`.

## Trust boundary

The public compatibility fields in `MemoryRecordRequest` are now treated as claims.
Memory Core derives effective values from the authenticated capture channel:

| Capture channel | Default effective trust |
|---|---|
| normal MCP or Edge REST observation | `AgentInferred` |
| signed user-message hook | `UserExplicit` / `UserCorrection` |
| signed tool or test result | `ToolObserved` |
| signed Git or code event | `CodeDerived` |
| external content capture | `ExternalUntrusted` |
| authenticated administrator | `HumanApproved` / `PolicyManaged` |

A signed `AgentObservation` remains an agent observation. A valid signature proves
which Edge sent the event and that the payload was not modified; it does not make the
agent's conclusion true.

## Envelope format

Edge signs an immutable envelope with HMAC-SHA256 using its device token. The signature
covers:

- envelope version
- event identifier
- capture channel
- capture timestamp
- nonce
- hash of adapter metadata, typed proof, and observation payload

The raw device token exists only in the request scope inside Core. PostgreSQL stores the
device-token hash and the resulting assurance record, never the raw credential.

## Offline and replay behavior

The exact signed envelope is stored in the SQLite outbox. A retry preserves the same
event identifier and signature. Core serializes ingestion by event identifier and
returns the original evidence/claim on replay instead of increasing the reinforcement
count.

The current offline replay window is 30 days with five minutes of accepted future clock
skew.

## Deterministic proof

A signed event only receives `AssuranceLevel.Deterministic` when the channel and proof
agree. Examples:

- a test/tool result has an exit code plus a tool name or command
- a Git result has an exit code plus a command or commit SHA
- a code artifact has an artifact digest or commit SHA

The admission controller still requires clear project scope and a source reference
before a deterministic technical fact or failed approach becomes active.

## Retrieval feedback

Every governed search records a trace containing the query, scope, ranking, and claim
identifiers. Context packets expose their trace id.

Feedback is stored with its own signed assurance. Agent-only feedback is useful
telemetry but cannot promote, stale, or revoke a claim. Authoritative mutation requires
one of:

- a user correction
- administrator/human approval
- deterministic signed tool/test/Git/code feedback

Confirmed authoritative feedback can promote probation memory. Contradicted feedback
marks a claim stale. Harmful feedback revokes it. Every mutation creates a new claim
version.

## Current threat model

HMAC device signatures protect Edge-to-Core transport integrity and stop ordinary MCP
clients from selecting a more trusted channel. They do not provide hardware-backed
attestation and cannot protect a fully compromised Edge host or stolen device token.
Production deployments should add token rotation, revocation, OS credential storage,
and later workload/OIDC identity.
