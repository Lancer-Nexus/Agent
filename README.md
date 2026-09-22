# Lancer Nexus Agent

The .NET 10 Agent worker runs on a Linux host. Its initial implementation establishes private outbound QUIC/mTLS connectivity to the Coordinator and reports sequenced Agent heartbeats. Host lifecycle and game-instance management are not implemented yet.

## Responsibilities

- Register the host and its capabilities
- Keep an authenticated outbound QUIC control session to the Coordinator
- Report Agent identity, build version and capabilities with durable monotonic sequence numbers
- Reconnect with bounded exponential backoff after transport failures
- Future: start, stop and drain approved instances; report instance state and perform controlled upgrades

Host deployment files and systemd templates live in the `Scripts` repository.

## Shared Protocol

The shared contracts are checked out in the `Protocol` submodule. Update it before local builds with:

```bash
git submodule update --init --remote --merge Protocol
```

CI performs the same update before restoring and building the Agent.

## QUIC control worker

Configure `Agent__NodeId`, `Agent__AgentId`, `Agent__Coordinator__QuicEndpoint` as `IP:port`, `Agent__Coordinator__ServerName`, client PFX path/password, Coordinator CA path and `Agent__StateFile`. `Agent__Coordinator__HeartbeatIntervalSeconds` defaults to 5 and is limited to 1–10 seconds to fit the Coordinator's default freshness window. The client certificate must have Client Authentication EKU and exactly one DNS SAN matching `Agent__NodeId`; the Coordinator server name must match its certificate. Do not expose an inbound Agent port.

The worker sends a Hello stream, then one `AgentHeartbeat` request per bidirectional QUIC stream over the same TLS 1.3 connection. Heartbeat sequence state is atomically replaced on disk before each send so Agent restarts do not roll the registry sequence backward. The Coordinator acknowledges each heartbeat. Instance heartbeats and lifecycle commands await the corresponding Agent host-management implementation.

Build and test with `dotnet test tests/LancerNexus.Agent.Tests/LancerNexus.Agent.Tests.csproj --configuration Release` on a Linux host with .NET 10. End-to-end QUIC additionally requires `libmsquic` 2.2+ and provisioned certificates.
