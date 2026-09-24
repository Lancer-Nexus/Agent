using LancerNexus.Agent;
using System.Runtime.Versioning;

[assembly: SupportedOSPlatform("linux")]

var builder = Host.CreateApplicationBuilder(args);
var settings = AgentQuicSettings.FromConfiguration(builder.Configuration);
builder.Services.AddSingleton(settings);
builder.Services.AddHostedService<AgentCoordinatorControlService>();
var host = builder.Build();
var logger = host.Services.GetRequiredService<ILoggerFactory>().CreateLogger("LancerNexus.Agent.Startup");
logger.LogInformation(
    "Agent starting for node {NodeId}; Coordinator QUIC is configured; instance reporting {InstanceReportingStatus}.",
    settings.NodeId,
    settings.InstanceStatusFilePath is null ? "disabled" : "enabled");
await host.RunAsync();
