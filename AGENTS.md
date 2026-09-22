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
