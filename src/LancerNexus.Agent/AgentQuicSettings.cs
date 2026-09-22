using System.Net;

namespace LancerNexus.Agent;

public sealed record AgentQuicSettings(
    IPEndPoint CoordinatorEndPoint,
    string CoordinatorServerName,
    string NodeId,
    string AgentId,
    string BuildVersion,
    string ClientCertificatePath,
    string? ClientCertificatePassword,
    string CoordinatorCaCertificatePath,
    string SequenceFilePath,
    TimeSpan HeartbeatInterval,
    string[] Capabilities)
{
    public const string Alpn = "lancer-nexus-control/1";

    public static AgentQuicSettings FromConfiguration(IConfiguration configuration)
    {
        var endpointValue = configuration["Agent:Coordinator:QuicEndpoint"];
        var serverName = configuration["Agent:Coordinator:ServerName"];
        var nodeId = configuration["Agent:NodeId"];
        var agentId = configuration["Agent:AgentId"];
        var certificatePath = configuration["Agent:Coordinator:ClientCertificatePath"];
        var caPath = configuration["Agent:Coordinator:CaCertificatePath"];
        var sequencePath = configuration["Agent:StateFile"];
        if (endpointValue is null || !IPEndPoint.TryParse(endpointValue, out var endpoint) ||
            string.IsNullOrWhiteSpace(serverName) || string.IsNullOrWhiteSpace(nodeId) ||
            string.IsNullOrWhiteSpace(agentId) || string.IsNullOrWhiteSpace(certificatePath) ||
            string.IsNullOrWhiteSpace(caPath) || string.IsNullOrWhiteSpace(sequencePath))
            throw new InvalidOperationException(
                "Agent QUIC requires Coordinator.QuicEndpoint, Coordinator.ServerName, NodeId, AgentId, client certificate, Coordinator CA and StateFile settings.");

        var intervalSeconds = configuration.GetValue<int?>("Agent:Coordinator:HeartbeatIntervalSeconds") ?? 5;
        if (intervalSeconds is < 1 or > 60)
            throw new InvalidOperationException("Agent heartbeat interval must be between 1 and 60 seconds.");

        var capabilities = configuration.GetSection("Agent:Capabilities").Get<string[]>() ??
                           ["cluster_handshake_v1", "agent_heartbeat_v1"];
        if (capabilities.Length == 0 || capabilities.Any(string.IsNullOrWhiteSpace) ||
            capabilities.Distinct(StringComparer.Ordinal).Count() != capabilities.Length)
            throw new InvalidOperationException("Agent capabilities must be non-empty and unique.");

        return new AgentQuicSettings(
            endpoint!,
            serverName,
            nodeId,
            agentId,
            configuration["Agent:BuildVersion"] ?? typeof(AgentQuicSettings).Assembly.GetName().Version?.ToString() ?? "unknown",
            Path.GetFullPath(certificatePath),
            configuration["Agent:Coordinator:ClientCertificatePassword"],
            Path.GetFullPath(caPath),
            Path.GetFullPath(sequencePath),
            TimeSpan.FromSeconds(intervalSeconds),
            capabilities);
    }
}
