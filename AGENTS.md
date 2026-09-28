# AGENTS.md – Lancer Nexus Agent

## Mission

Provide a narrowly scoped and auditable host-management service.

## MVP architecture baseline

- Agent owns host and process lifecycle only; it does not own accounts, placement, character persistence or game simulation.
- It reports readiness, capacity and lifecycle state to the Coordinator, which makes placement decisions.
- It must preserve the transfer handoff lifecycle: `Requested -> Reserved -> Prepared -> SourceFrozen -> TargetAccepted -> Committed -> SourceReleased`; draining must allow active transfers to finish safely.
- Character authority is protected by MySQL `lease_version` fencing outside the Agent. Redis is not authoritative storage.
- Agent control messages use the versioned `Protocol` contracts and capability negotiation.
- Agent initiates and maintains outbound QUIC/mTLS sessions; it must never open a public inbound control port.
- Agent heartbeat sequence state must survive process restart so Coordinator replay/freshness checks remain monotonic.

## Rules

- Accept only authenticated and authorized control commands.
- Allow only configured service, release and instance paths.
- Never execute arbitrary shell text received over the network.
- Do not expose host credentials or unrestricted root access to game instances.
- Make start, stop, drain and upgrade operations idempotent.
- Wait for readiness and draining state instead of using uncontrolled process kills.
- Report explicit capability, version and health information.
- Keep filesystem and systemd behavior in `Scripts` where possible.

## Verification

Test lost connections, reconnect backoff, duplicate heartbeats, monotonic sequence recovery, missing/stale/mismatched instance runtime snapshots, unauthorized paths, failed starts, partial upgrades and rollback behavior.

For the initial control worker, run:

```bash
git submodule update --init --remote --merge Protocol
dotnet restore tests/LancerNexus.Agent.Tests/LancerNexus.Agent.Tests.csproj
dotnet format src/LancerNexus.Agent/LancerNexus.Agent.csproj --verify-no-changes --no-restore
dotnet format tests/LancerNexus.Agent.Tests/LancerNexus.Agent.Tests.csproj --verify-no-changes --no-restore
dotnet build tests/LancerNexus.Agent.Tests/LancerNexus.Agent.Tests.csproj --configuration Release --no-restore --warnaserror
dotnet test tests/LancerNexus.Agent.Tests/LancerNexus.Agent.Tests.csproj --configuration Release --no-build
```

## Working-model escalation

- If a task requires complex reasoning beyond the current model's reliable scope, ask the user whether switching to a stronger model is desired before continuing.
- Do not switch models silently or broaden the task because a stronger model may be useful.

## Nexus baseline system groups

The base Nexus topology uses eight game instances, one per group: BR01-BR06 (`br-01`), BW01-BW10 (`bw-01`), EW01-EW05 (`ew-01`), IW01-IW06 (`iw-01`), KU01-KU06 (`ku-01`), LI01-LI05 (`li-01`), RH01-RH05 (`rh-01`), and `mixed-01` for all remaining registered systems. System nicknames are compared case insensitively and emitted lowercase. Folder names are not always world nicknames: `fp7` contains `fp7_system`; `intro` and `miners` are asset directories, not registered worlds.
The current worker manages one instance report per process; prepare one Agent configuration/process per group, with separate AgentId and sequence files. All configured SystemIds must match the fresh LLServer status set before reporting ready. Each group shares one player limit and endpoint across its systems. Never report readiness from static topology alone.
