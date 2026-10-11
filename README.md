# Lancer Nexus Agent

The runtime status reader validates the optional `NpcTransferEndpoint`
(`quic://host:port`) and forwards it in instance heartbeats. NPC transport ports
are independent of the game endpoint and may differ for instances on one host.

The .NET 10 Agent worker runs on a Linux host. It establishes private outbound QUIC/mTLS connectivity to the Coordinator and reports sequenced Agent heartbeats. When configured with an LLServer runtime-status file, it also reports sequenced Instance heartbeats. Host lifecycle management is not implemented yet.

## Responsibilities

- Register the host and its capabilities
- Keep an authenticated outbound QUIC control session to the Coordinator
- Report Agent identity, build version and capabilities with durable monotonic sequence numbers
- Reconnect with bounded exponential backoff after transport failures
- Report live instance readiness and player counts from a fresh local LLServer status snapshot
- Future: start, stop and drain approved instances; perform controlled upgrades

Host deployment files and systemd templates live in the `Scripts` repository.

## Shared Protocol

The shared contracts are checked out in the `Protocol` submodule. Update it before local builds with:

```bash
git submodule update --init --remote --merge Protocol
```

CI recursively checks out the `Protocol` revision pinned by the Agent repository before restoring and building.

## QUIC control worker

Configure `Agent__NodeId`, `Agent__AgentId`, `Agent__Coordinator__QuicEndpoint` as `IP:port`, `Agent__Coordinator__ServerName`, client PFX path/password, Coordinator CA path and `Agent__StateFile`. `Agent__Coordinator__HeartbeatIntervalSeconds` defaults to 5 and is limited to 1–10 seconds to fit the Coordinator's default freshness window. The client certificate must have Client Authentication EKU and exactly one DNS SAN matching `Agent__NodeId`; the Coordinator server name must match its certificate. Do not expose an inbound Agent port.

For market-event transport, an instance Agent may additionally configure `dynamic_market_v1` in `Agent__Capabilities` and set `Agent__MarketTrade__SocketPath` to a per-instance Unix-domain socket under its private runtime directory. The Agent creates this socket only when Coordinator negotiates the capability, restricts it to the Agent service user, validates each event against the configured instance and owned systems, and relays it unchanged over the existing mTLS QUIC session. LLServer must keep its event and cursor in a durable outbox and remove an event only after `Applied` or idempotent `Duplicate`; the Agent does not own that outbox. Run LLServer with the same service identity to connect to the mode-0600 socket.

The worker sends a Hello stream, then one `AgentHeartbeat` request per bidirectional QUIC stream over the same TLS 1.3 connection. Heartbeat sequence state is atomically replaced on disk before each send so Agent restarts do not roll the registry sequence backward. If `Agent__Instance__StatusFile`, `InstanceId`, `SystemId`, `Endpoint`, and positive `MaxPlayers` are configured, the Agent negotiates `instance_heartbeat_v1` and sends an `InstanceHeartbeat` after each Agent heartbeat. Missing, stale (older than 10 seconds), malformed, identity-mismatched, or permission-unsynchronized snapshots are reported as not ready with zero players; `PermissionsReady` must be true and `PermissionRevision` nonnegative. The Agent never infers readiness from configuration. LLServer writes this snapshot atomically when its own `ServerConfig.RuntimeStatusFile`, `InstanceId`, `SystemId` and `InstanceEndpoint` are configured. LLServer includes `npc_transfer_v1` in that status only while its private NPC mTLS/QUIC receiver is running; the Agent forwards these runtime capabilities in the instance heartbeat, so the Coordinator will not reserve NPC transfers for an unconfigured target. Set the server's optional `DrainFlagFile` to a host-local path and create that file to mark the instance draining; the Agent forwards the flag to the Coordinator, which excludes it from new placements. Removing the file permits placement again. Existing player connections remain untouched. The status file should be on a host-local path readable by the Agent and writable by LLServer. Agent lifecycle operations and controlled transfer orchestration remain unimplemented.

Local live verification on 2026-10-10 used two .NET Agent processes over authenticated QUIC/mTLS, one for LI01 and one for LI02, with each instance's current LLServer status file. The single Coordinator registry reported both instances ready; LI01 remained draining and LI02 was available. Gateway placed the client on LI02, and the disconnect completed the Gateway final snapshot and lease release. The Coordinator MySQL event store acknowledged `PlayerConnected`, `CharacterConnected`, `CharacterDisconnected`, and `PlayerDisconnected` through sequence 38. This was a local test using short-lived certificates; it does not implement remote process lifecycle commands.

Build and test with `dotnet test tests/LancerNexus.Agent.Tests/LancerNexus.Agent.Tests.csproj --configuration Release` on a Linux host with .NET 10. End-to-end QUIC additionally requires `libmsquic` 2.2+ and provisioned certificates.

For the eight-group Nexus baseline, run one Agent process/configuration per group. `Agent:Instance:SystemIds` must match LLServer runtime status. Omitted lists preserve single-system deployments.
