using System.Buffers.Binary;
using System.Net.Sockets;
using System.Text;
using LancerNexus.Protocol;
using MessagePack;

namespace LancerNexus.Agent;

/// <summary>Local-only transport relay for LLServer market events.</summary>
public sealed class AgentMarketTradeIpcServer : IDisposable
{
    private const int MaximumPayloadLength = 16 * 1024;
    private const int MaximumUnixPathLength = 100;
    private static readonly MessagePackSerializerOptions UntrustedMessagePack =
        MessagePackSerializerOptions.Standard.WithSecurity(MessagePackSecurity.UntrustedData);

    private readonly string socketPath;
    private readonly string instanceId;
    private readonly HashSet<string> systemIds;
    private readonly Socket listener;
    private readonly ILogger logger;
    private bool disposed;

    public AgentMarketTradeIpcServer(
        string socketPath,
        string instanceId,
        string systemId,
        IReadOnlyCollection<string> systemIds,
        ILogger logger)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(socketPath);
        ArgumentException.ThrowIfNullOrWhiteSpace(instanceId);
        ArgumentException.ThrowIfNullOrWhiteSpace(systemId);
        ArgumentNullException.ThrowIfNull(systemIds);
        ArgumentNullException.ThrowIfNull(logger);

        this.socketPath = Path.GetFullPath(socketPath);
        if (Encoding.UTF8.GetByteCount(this.socketPath) > MaximumUnixPathLength)
            throw new ArgumentException("Market trade socket path exceeds the Unix domain socket path limit.", nameof(socketPath));
        this.instanceId = instanceId;
        this.logger = logger;
        this.systemIds = new HashSet<string>(systemIds, StringComparer.OrdinalIgnoreCase) { systemId };

        Directory.CreateDirectory(Path.GetDirectoryName(this.socketPath)!);
        if (File.Exists(this.socketPath))
            throw new IOException("Market trade socket path already exists; verify the Agent instance before removing it.");

        listener = new Socket(AddressFamily.Unix, SocketType.Stream, ProtocolType.Unspecified);
        try
        {
            listener.Bind(new UnixDomainSocketEndPoint(this.socketPath));
            File.SetUnixFileMode(this.socketPath, UnixFileMode.UserRead | UnixFileMode.UserWrite);
            listener.Listen(16);
        }
        catch
        {
            listener.Dispose();
            if (File.Exists(this.socketPath))
                File.Delete(this.socketPath);
            throw;
        }
    }

    public async Task RunAsync(
        Func<MarketTradeEventV1, CancellationToken, Task<MarketTradeEventAcknowledgementV1>> forward,
        CancellationToken cancellationToken)
    {
        ArgumentNullException.ThrowIfNull(forward);
        ObjectDisposedException.ThrowIf(disposed, this);
        try
        {
            while (!cancellationToken.IsCancellationRequested)
            {
                using var client = await listener.AcceptAsync(cancellationToken).ConfigureAwait(false);
                try
                {
                    await HandleAsync(client, forward, cancellationToken).ConfigureAwait(false);
                }
                catch (OperationCanceledException) when (cancellationToken.IsCancellationRequested)
                {
                    throw;
                }
                catch (Exception error) when (error is IOException or SocketException or
                                              MessagePackSerializationException or ProtocolViolationException)
                {
                    logger.LogWarning(error, "Rejected a malformed or interrupted local market event request.");
                }
            }
        }
        catch (OperationCanceledException) when (cancellationToken.IsCancellationRequested)
        {
        }
        catch (ObjectDisposedException) when (cancellationToken.IsCancellationRequested || disposed)
        {
        }
    }

    public void Dispose()
    {
        if (disposed)
            return;
        disposed = true;
        listener.Dispose();
        if (File.Exists(socketPath))
            File.Delete(socketPath);
    }

    private async Task HandleAsync(
        Socket client,
        Func<MarketTradeEventV1, CancellationToken, Task<MarketTradeEventAcknowledgementV1>> forward,
        CancellationToken cancellationToken)
    {
        using var stream = new NetworkStream(client, ownsSocket: false);
        var prefix = new byte[sizeof(uint)];
        await stream.ReadExactlyAsync(prefix, cancellationToken).ConfigureAwait(false);
        var payloadLength = BinaryPrimitives.ReadUInt32BigEndian(prefix);
        if (payloadLength is 0 or > MaximumPayloadLength)
            throw new ProtocolViolationException("Invalid market trade IPC payload length.");

        var payload = new byte[(int)payloadLength];
        await stream.ReadExactlyAsync(payload, cancellationToken).ConfigureAwait(false);
        var trade = MessagePackSerializer.Deserialize<MarketTradeEventV1>(payload, UntrustedMessagePack);
        if (!trade.IsValid())
            throw new ProtocolViolationException("Invalid market trade IPC payload.");

        var acknowledgement = trade.InstanceId == instanceId && systemIds.Contains(trade.SystemId)
            ? await ForwardAsync(trade, forward, cancellationToken).ConfigureAwait(false)
            : CreateAcknowledgement(trade, MarketTradeIngestStatusV1.Rejected);
        if (!acknowledgement.Matches(trade))
            throw new ProtocolViolationException("Market trade relay returned an acknowledgement for another event.");

        var responsePayload = MessagePackSerializer.Serialize(acknowledgement);
        BinaryPrimitives.WriteUInt32BigEndian(prefix, checked((uint)responsePayload.Length));
        await stream.WriteAsync(prefix, cancellationToken).ConfigureAwait(false);
        await stream.WriteAsync(responsePayload, cancellationToken).ConfigureAwait(false);
        await stream.FlushAsync(cancellationToken).ConfigureAwait(false);
    }

    private async Task<MarketTradeEventAcknowledgementV1> ForwardAsync(
        MarketTradeEventV1 trade,
        Func<MarketTradeEventV1, CancellationToken, Task<MarketTradeEventAcknowledgementV1>> forward,
        CancellationToken cancellationToken)
    {
        try
        {
            return await forward(trade, cancellationToken).ConfigureAwait(false);
        }
        catch (IOException error) when (!cancellationToken.IsCancellationRequested)
        {
            logger.LogWarning(error, "Coordinator market event delivery is unavailable for {InstanceId}/{Sequence}.",
                trade.InstanceId, trade.Sequence);
            return CreateAcknowledgement(trade, MarketTradeIngestStatusV1.Unavailable);
        }
    }

    private static MarketTradeEventAcknowledgementV1 CreateAcknowledgement(
        MarketTradeEventV1 trade,
        MarketTradeIngestStatusV1 status) => new()
        {
            EventId = trade.EventId,
            InstanceId = trade.InstanceId,
            ProducerEpoch = trade.ProducerEpoch,
            Sequence = trade.Sequence,
            Status = status
        };
}
