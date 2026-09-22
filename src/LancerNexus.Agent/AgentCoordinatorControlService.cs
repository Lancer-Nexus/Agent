using System.Buffers;
using System.Net.Quic;
using System.Net.Security;
using System.Security.Authentication;
using System.Security.Cryptography;
using System.Security.Cryptography.X509Certificates;
using System.Runtime.Versioning;
using LancerNexus.Protocol;
using MessagePack;

namespace LancerNexus.Agent;

[SupportedOSPlatform("linux")]
public sealed class AgentCoordinatorControlService : BackgroundService
{
    private const int MaxSerializedEnvelopeLength = checked((int)ClusterEnvelopeValidator.MaxPayloadLength + 16 * 1024);
    private static readonly SslApplicationProtocol Alpn = new(AgentQuicSettings.Alpn);
    private static readonly MessagePackSerializerOptions UntrustedMessagePack =
        MessagePackSerializerOptions.Standard.WithSecurity(MessagePackSecurity.UntrustedData);

    private readonly AgentQuicSettings settings;
    private readonly ILogger<AgentCoordinatorControlService> logger;
    private readonly X509Certificate2 clientCertificate;
    private readonly X509Certificate2 coordinatorCaCertificate;
    private readonly HeartbeatSequenceStore sequenceStore;

    public AgentCoordinatorControlService(
        AgentQuicSettings settings,
        ILogger<AgentCoordinatorControlService> logger)
    {
        this.settings = settings;
        this.logger = logger;
        clientCertificate = X509CertificateLoader.LoadPkcs12FromFile(
            settings.ClientCertificatePath,
            settings.ClientCertificatePassword,
            X509KeyStorageFlags.EphemeralKeySet);
        coordinatorCaCertificate = X509CertificateLoader.LoadCertificateFromFile(settings.CoordinatorCaCertificatePath);
        ValidateClientCertificate(clientCertificate, settings.NodeId);
        sequenceStore = new HeartbeatSequenceStore(settings.SequenceFilePath);
    }

    protected override async Task ExecuteAsync(CancellationToken stoppingToken)
    {
        if (!QuicConnection.IsSupported)
            throw new PlatformNotSupportedException("Agent QUIC requires System.Net.Quic, TLS 1.3 and libmsquic.");

        var retryDelay = TimeSpan.FromSeconds(1);
        while (!stoppingToken.IsCancellationRequested)
        {
            var sessionStartedAt = DateTimeOffset.UtcNow;
            try
            {
                await RunSessionAsync(stoppingToken);
                retryDelay = TimeSpan.FromSeconds(1);
            }
            catch (OperationCanceledException) when (stoppingToken.IsCancellationRequested)
            {
                break;
            }
            catch (Exception exception)
            {
                if (DateTimeOffset.UtcNow - sessionStartedAt >= TimeSpan.FromMinutes(1))
                    retryDelay = TimeSpan.FromSeconds(1);
                logger.LogWarning(exception, "Agent lost its Coordinator QUIC control session; reconnecting in {Delay}", retryDelay);
                await Task.Delay(retryDelay, stoppingToken);
                retryDelay = TimeSpan.FromSeconds(Math.Min(retryDelay.TotalSeconds * 2, 30));
            }
        }
    }

    public override void Dispose()
    {
        clientCertificate.Dispose();
        coordinatorCaCertificate.Dispose();
        base.Dispose();
    }

    private async Task RunSessionAsync(CancellationToken cancellationToken)
    {
        var options = new QuicClientConnectionOptions
        {
            RemoteEndPoint = settings.CoordinatorEndPoint,
            ClientAuthenticationOptions = new SslClientAuthenticationOptions
            {
                TargetHost = settings.CoordinatorServerName,
                ApplicationProtocols = [Alpn],
                EnabledSslProtocols = SslProtocols.Tls13,
                ClientCertificates = [clientCertificate],
                RemoteCertificateValidationCallback = ValidateCoordinatorCertificate
            }
        };

        await using var connection = await QuicConnection.ConnectAsync(options, cancellationToken);
        await ExchangeHelloAsync(connection, cancellationToken);
        logger.LogInformation("Agent established an authenticated QUIC control session to {EndPoint}", settings.CoordinatorEndPoint);

        while (!cancellationToken.IsCancellationRequested)
        {
            await Task.Delay(settings.HeartbeatInterval, cancellationToken);
            await SendHeartbeatAsync(connection, cancellationToken);
        }
    }

    private async Task ExchangeHelloAsync(QuicConnection connection, CancellationToken cancellationToken)
    {
        var hello = new ClusterHello
        {
            NodeId = settings.NodeId,
            InstanceId = settings.AgentId,
            BuildVersion = settings.BuildVersion,
            Capabilities = settings.Capabilities
        };
        var request = CreateEnvelope(ClusterMessageType.Hello, ClusterFrameFlags.Request, 1,
            MessagePackSerializer.Serialize(hello));
        var response = await ExchangeAsync(connection, request, cancellationToken);
        if (response.MessageType != (ushort)ClusterMessageType.Hello ||
            response.Flags is not (ClusterFrameFlags.Response or (ClusterFrameFlags.Response | ClusterFrameFlags.Error)))
            throw new ProtocolViolationException("Coordinator returned an invalid Hello response.");

        var result = MessagePackSerializer.Deserialize<ClusterHandshakeResponse>(response.Payload, UntrustedMessagePack);
        if (!result.Accepted)
            throw new InvalidOperationException($"Coordinator rejected Agent Hello: {result.ReasonCode}");
        if (!result.NegotiatedCapabilities.Contains("agent_heartbeat_v1", StringComparer.Ordinal))
            throw new ProtocolViolationException("Coordinator did not negotiate the Agent heartbeat capability.");
    }

    private async Task SendHeartbeatAsync(QuicConnection connection, CancellationToken cancellationToken)
    {
        var heartbeat = new AgentHeartbeat
        {
            AgentId = settings.AgentId,
            NodeId = settings.NodeId,
            BuildVersion = settings.BuildVersion,
            Capabilities = settings.Capabilities,
            Sequence = sequenceStore.Next()
        };
        var request = CreateEnvelope(ClusterMessageType.AgentHeartbeat, ClusterFrameFlags.Request,
            heartbeat.Sequence, MessagePackSerializer.Serialize(heartbeat));
        var response = await ExchangeAsync(connection, request, cancellationToken);
        if (response.MessageType != (ushort)ClusterMessageType.AgentHeartbeatResponse ||
            response.CorrelationId != request.CorrelationId)
            throw new ProtocolViolationException("Coordinator returned an invalid Agent heartbeat response.");

        var result = MessagePackSerializer.Deserialize<AgentHeartbeatResponse>(response.Payload, UntrustedMessagePack);
        if (!result.Accepted || result.Sequence != heartbeat.Sequence)
            throw new InvalidOperationException($"Coordinator rejected Agent heartbeat {heartbeat.Sequence}: {result.ReasonCode}");
    }

    private static async Task<ClusterEnvelope> ExchangeAsync(
        QuicConnection connection,
        ClusterEnvelope request,
        CancellationToken cancellationToken)
    {
        await using var stream = await connection.OpenOutboundStreamAsync(QuicStreamType.Bidirectional, cancellationToken);
        await stream.WriteAsync(MessagePackSerializer.Serialize(request), cancellationToken);
        stream.CompleteWrites();
        var response = await ReadEnvelopeAsync(stream, cancellationToken);
        ClusterEnvelopeValidator.Validate(response);
        if (response.CorrelationId != request.CorrelationId)
            throw new ProtocolViolationException("Coordinator response correlation ID did not match the request.");
        return response;
    }

    private bool ValidateCoordinatorCertificate(
        object sender,
        X509Certificate? certificate,
        X509Chain? chain,
        SslPolicyErrors errors)
    {
        if (certificate is not X509Certificate2 peer ||
            (errors & SslPolicyErrors.RemoteCertificateNameMismatch) != 0 ||
            (errors & SslPolicyErrors.RemoteCertificateNotAvailable) != 0 ||
            !HasEnhancedKeyUsage(peer, "1.3.6.1.5.5.7.3.1"))
            return false;

        using var validationChain = new X509Chain();
        validationChain.ChainPolicy.TrustMode = X509ChainTrustMode.CustomRootTrust;
        validationChain.ChainPolicy.CustomTrustStore.Add(coordinatorCaCertificate);
        validationChain.ChainPolicy.RevocationMode = X509RevocationMode.NoCheck;
        return validationChain.Build(peer);
    }

    private static void ValidateClientCertificate(X509Certificate2 certificate, string nodeId)
    {
        var now = DateTime.UtcNow;
        if (!certificate.HasPrivateKey || certificate.NotBefore.ToUniversalTime() > now ||
            certificate.NotAfter.ToUniversalTime() <= now || !HasEnhancedKeyUsage(certificate, "1.3.6.1.5.5.7.3.2"))
            throw new CryptographicException("Agent client certificate must have a private key and Client Authentication EKU.");

        var sanExtension = certificate.Extensions.FirstOrDefault(extension => extension.Oid?.Value == "2.5.29.17");
        var san = sanExtension is null ? null : new X509SubjectAlternativeNameExtension(sanExtension.RawData, sanExtension.Critical);
        var dnsNames = san?.EnumerateDnsNames().ToArray() ?? [];
        if (dnsNames.Length != 1 || dnsNames[0].Contains('*', StringComparison.Ordinal) ||
            !string.Equals(dnsNames[0], nodeId, StringComparison.OrdinalIgnoreCase))
            throw new CryptographicException("Agent client certificate must contain exactly one DNS SAN matching Agent:NodeId.");
    }

    private static bool HasEnhancedKeyUsage(X509Certificate2 certificate, string expectedOid) => certificate.Extensions
        .Where(extension => extension.Oid?.Value == "2.5.29.37")
        .Select(extension => new X509EnhancedKeyUsageExtension(extension, extension.Critical))
        .Any(extension => extension.EnhancedKeyUsages.Cast<System.Security.Cryptography.Oid>()
            .Any(oid => oid.Value == expectedOid));

    private static ClusterEnvelope CreateEnvelope(ClusterMessageType type, ClusterFrameFlags flags, ulong sequence, byte[] payload) => new()
    {
        MessageType = (ushort)type,
        Flags = flags,
        CorrelationId = Guid.NewGuid(),
        Sequence = sequence,
        PayloadLength = checked((uint)payload.Length),
        Payload = payload
    };

    private static async Task<ClusterEnvelope> ReadEnvelopeAsync(QuicStream stream, CancellationToken cancellationToken)
    {
        using var buffer = new MemoryStream();
        var chunk = ArrayPool<byte>.Shared.Rent(8192);
        try
        {
            int read;
            while ((read = await stream.ReadAsync(chunk.AsMemory(), cancellationToken)) != 0)
            {
                if (buffer.Length + read > MaxSerializedEnvelopeLength)
                    throw new ProtocolViolationException("Coordinator response exceeds the protocol stream limit.");
                buffer.Write(chunk, 0, read);
            }

            if (buffer.Length == 0)
                throw new ProtocolViolationException("Coordinator response stream was empty.");
            return MessagePackSerializer.Deserialize<ClusterEnvelope>(buffer.ToArray(), UntrustedMessagePack);
        }
        finally
        {
            ArrayPool<byte>.Shared.Return(chunk);
        }
    }
}
