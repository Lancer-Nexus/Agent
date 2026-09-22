using System.Text.Json;

namespace LancerNexus.Agent;

public sealed class InstanceRuntimeStatusReader(
    string filePath,
    string instanceId,
    string systemId,
    string endpoint)
{
    private static readonly TimeSpan MaximumAge = TimeSpan.FromSeconds(10);
    private readonly string fullPath = Path.GetFullPath(filePath);

    public InstanceRuntimeStatus? Read(DateTimeOffset nowUtc)
    {
        try
        {
            var status = JsonSerializer.Deserialize<InstanceRuntimeStatus>(File.ReadAllText(fullPath));
            if (status is null || status.WrittenAtUtc > nowUtc.AddSeconds(2) ||
                nowUtc - status.WrittenAtUtc > MaximumAge ||
                !string.Equals(status.InstanceId, instanceId, StringComparison.Ordinal) ||
                !string.Equals(status.SystemId, systemId, StringComparison.Ordinal) ||
                status.CurrentPlayers < 0 || status.MaxPlayers <= 0 || status.CurrentPlayers > status.MaxPlayers ||
                !string.Equals(status.Endpoint, endpoint, StringComparison.Ordinal))
                return null;

            return status;
        }
        catch (Exception exception) when (exception is IOException or UnauthorizedAccessException or JsonException)
        {
            return null;
        }
    }
}

public sealed record InstanceRuntimeStatus
{
    public DateTimeOffset WrittenAtUtc { get; init; }
    public string InstanceId { get; init; } = "";
    public string SystemId { get; init; } = "";
    public bool IsReady { get; init; }
    public int CurrentPlayers { get; init; }
    public int MaxPlayers { get; init; }
    public string Endpoint { get; init; } = "";
}
