# Lancer Nexus Agent

The Agent runs on a Linux host and manages the local Lancer Nexus game instances. It is the bridge between the Coordinator and the host service manager.

## Responsibilities

- Register the host and its capabilities
- Start, stop and drain approved instances
- Report process, health, capacity and resource state
- Maintain instance configuration and identity
- Perform controlled local upgrades and rollback hooks
- Keep private QUIC control connectivity to the Coordinator

Host deployment files and systemd templates live in the `Scripts` repository.

## Shared Protocol

The shared contracts are checked out in the `Protocol` submodule. Update it before local builds with:

```bash
git submodule update --init --remote --merge Protocol
```

CI performs the same update before restoring and building the Agent.
