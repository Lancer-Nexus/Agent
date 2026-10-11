using System.Buffers.Binary;
using System.Net.Sockets;
using System.Runtime.Versioning;
using LancerNexus.Agent;
using LancerNexus.Protocol;
using MessagePack;
using Microsoft.Extensions.Logging.Abstractions;
using Xunit;

namespace LancerNexus.Agent.Tests;

[SupportedOSPlatform("linux")]
public sealed class AgentMarketTradeIpcServerTests
{
    private static readonly MessagePackSerializerOptions UntrustedMessagePack =
        MessagePackSerializerOptions.Standard.WithSecurity(MessagePackSecurity.UntrustedData);

    [Fact]
    public async Task RelayForwardsTheEventAndReturnsTheDurableCoordinatorAcknowledgement()
    {
        var directory = Path.Combine(Path.GetTempPath(), $"ln-agent-market-{Guid.NewGuid():N}");
        var socketPath = Path.Combine(directory, "market.sock");
        using var server = new AgentMarketTradeIpcServer(socketPath, "li01", "li01", [], NullLogger.Instance);
        using var cancellation = new CancellationTokenSource(TimeSpan.FromSeconds(5));
        var trade = Trade();
        MarketTradeEventV1? forwarded = null;
        Assert.Equal(UnixFileMode.UserRead | UnixFileMode.UserWrite, File.GetUnixFileMode(socketPath));
        var relayTask = server.RunAsync((value, _) =>
        {
            forwarded = value;
            return Task.FromResult(Acknowledgement(value, MarketTradeIngestStatusV1.Applied, 7));
        }, cancellation.Token);

        var acknowledgement = await SendAsync(socketPath, trade, cancellation.Token);

        Assert.Equal(trade, forwarded);
        Assert.True(acknowledgement.Matches(trade));
        Assert.Equal(MarketTradeIngestStatusV1.Applied, acknowledgement.Status);
        Assert.Equal(7, acknowledgement.MarketRevision);
        cancellation.Cancel();
        await relayTask.WaitAsync(TimeSpan.FromSeconds(2));
    }

    [Fact]
    public async Task RelayRejectsEventsOutsideItsConfiguredInstanceAndWorlds()
    {
        var directory = Path.Combine(Path.GetTempPath(), $"ln-agent-market-{Guid.NewGuid():N}");
        var socketPath = Path.Combine(directory, "market.sock");
        using var server = new AgentMarketTradeIpcServer(socketPath, "li01", "li01", ["li02"], NullLogger.Instance);
        using var cancellation = new CancellationTokenSource(TimeSpan.FromSeconds(5));
        var forwardCount = 0;
        var relayTask = server.RunAsync((value, _) =>
        {
            Interlocked.Increment(ref forwardCount);
            return Task.FromResult(Acknowledgement(value, MarketTradeIngestStatusV1.Applied, 7));
        }, cancellation.Token);

        var acknowledgement = await SendAsync(socketPath, Trade() with { SystemId = "li03" }, cancellation.Token);

        Assert.Equal(0, forwardCount);
        Assert.Equal(MarketTradeIngestStatusV1.Rejected, acknowledgement.Status);
        cancellation.Cancel();
        await relayTask.WaitAsync(TimeSpan.FromSeconds(2));
    }

    private static async Task<MarketTradeEventAcknowledgementV1> SendAsync(
        string socketPath,
        MarketTradeEventV1 trade,
        CancellationToken cancellationToken)
    {
        using var socket = new Socket(AddressFamily.Unix, SocketType.Stream, ProtocolType.Unspecified);
        await socket.ConnectAsync(new UnixDomainSocketEndPoint(socketPath), cancellationToken);
        using var stream = new NetworkStream(socket, ownsSocket: false);
        var payload = MessagePackSerializer.Serialize(trade);
        var prefix = new byte[sizeof(uint)];
        BinaryPrimitives.WriteUInt32BigEndian(prefix, checked((uint)payload.Length));
        await stream.WriteAsync(prefix, cancellationToken);
        await stream.WriteAsync(payload, cancellationToken);
        await stream.FlushAsync(cancellationToken);

        await stream.ReadExactlyAsync(prefix, cancellationToken);
        var responseLength = BinaryPrimitives.ReadUInt32BigEndian(prefix);
        Assert.InRange(responseLength, 1u, 16u * 1024u);
        var response = new byte[(int)responseLength];
        await stream.ReadExactlyAsync(response, cancellationToken);
        return MessagePackSerializer.Deserialize<MarketTradeEventAcknowledgementV1>(response, UntrustedMessagePack);
    }

    private static MarketTradeEventV1 Trade() => new()
    {
        EventId = Guid.NewGuid(),
        InstanceId = "li01",
        ProducerEpoch = Guid.NewGuid(),
        Sequence = 1,
        OccurredUtc = DateTimeOffset.UtcNow,
        MarketId = "li01_01_base",
        SystemId = "li01",
        GoodId = "commodity_food",
        Side = MarketTradeSideV1.Buy,
        Quantity = 1
    };

    private static MarketTradeEventAcknowledgementV1 Acknowledgement(
        MarketTradeEventV1 trade,
        MarketTradeIngestStatusV1 status,
        long marketRevision) => new()
        {
            EventId = trade.EventId,
            InstanceId = trade.InstanceId,
            ProducerEpoch = trade.ProducerEpoch,
            Sequence = trade.Sequence,
            Status = status,
            MarketRevision = marketRevision
        };
}
