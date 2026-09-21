# AGENTS.md – Lancer Nexus Agent

## Mission

Provide a narrowly scoped and auditable host-management service.

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

Test lost connections, duplicate commands, unauthorized paths, failed starts, partial upgrades and rollback behavior.
