using LancerNexus.Agent;
using System.Runtime.Versioning;

[assembly: SupportedOSPlatform("linux")]

var builder = Host.CreateApplicationBuilder(args);
var settings = AgentQuicSettings.FromConfiguration(builder.Configuration);
builder.Services.AddSingleton(settings);
builder.Services.AddHostedService<AgentCoordinatorControlService>();
await builder.Build().RunAsync();
