using LancerNexus.Agent;
using Microsoft.Extensions.Configuration;
using System.Runtime.Versioning;
using Xunit;

namespace LancerNexus.Agent.Tests;

[SupportedOSPlatform("linux")]
public sealed class AgentSettingsAndSequenceTests
{
    [Fact]
    public void SettingsRequirePrivateEndpointAndClientIdentityConfiguration()
    {
        var configuration = new ConfigurationBuilder().AddInMemoryCollection(new Dictionary<string, string?>
        {
            ["Agent:Coordinator:QuicEndpoint"] = "10.20.0.20:7443",
            ["Agent:Coordinator:ServerName"] = "coordinator.internal",
            ["Agent:NodeId"] = "node-01",
            ["Agent:AgentId"] = "agent-01",
            ["Agent:Coordinator:ClientCertificatePath"] = "agent.pfx",
            ["Agent:Coordinator:CaCertificatePath"] = "coordinator-ca.pem",
            ["Agent:StateFile"] = "agent-state/sequence.txt"
        }).Build();

        var settings = AgentQuicSettings.FromConfiguration(configuration);

        Assert.Equal(7443, settings.CoordinatorEndPoint.Port);
        Assert.Equal("node-01", settings.NodeId);
        Assert.Equal(TimeSpan.FromSeconds(5), settings.HeartbeatInterval);
    }

    [Fact]
    public void SequenceStorePersistsIncreasingValuesAcrossInstances()
    {
        var path = Path.Combine(Path.GetTempPath(), $"lancer-agent-sequence-{Guid.NewGuid():N}.txt");
        try
        {
            var first = new HeartbeatSequenceStore(path);
            Assert.Equal(1UL, first.Next());
            Assert.Equal(2UL, first.Next());

            var restarted = new HeartbeatSequenceStore(path);
            Assert.Equal(3UL, restarted.Next());
        }
        finally
        {
            if (File.Exists(path))
                File.Delete(path);
        }
    }
}
