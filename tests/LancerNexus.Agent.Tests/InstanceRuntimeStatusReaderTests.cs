using System.Text.Json;
using LancerNexus.Agent;
using System.Runtime.Versioning;
using Xunit;

namespace LancerNexus.Agent.Tests;

[SupportedOSPlatform("linux")]
public sealed class InstanceRuntimeStatusReaderTests
{
    private static readonly DateTimeOffset Now = new(2026, 9, 22, 12, 0, 0, TimeSpan.Zero);

    [Fact]
    public void Read_ReturnsFreshMatchingServerStatus()
    {
        var status = Status();

        var actual = Read(status);

        Assert.NotNull(actual);
        Assert.True(actual.IsReady);
        Assert.Equal(17, actual.CurrentPlayers);
        Assert.Equal(200, actual.MaxPlayers);
        Assert.False(Read(status with { IsReady = false })!.IsReady);
    }

    [Theory]
    [InlineData(11, 0, 0)]
    [InlineData(-3, 0, 1)]
    [InlineData(0, -1, 0)]
    [InlineData(0, 0, 201)]
    public void Read_RejectsStaleFutureOrInvalidPlayerCounts(int ageSeconds, int maxPlayers, int players)
    {
        var status = Status() with
        {
            WrittenAtUtc = Now.AddSeconds(-ageSeconds),
            MaxPlayers = maxPlayers == 0 ? 200 : maxPlayers,
            CurrentPlayers = players
        };

        Assert.Null(Read(status));
    }

    [Fact]
    public void Read_RejectsIdentityOrEndpointMismatch()
    {
        Assert.Null(Read(Status() with { InstanceId = "different" }));
        Assert.Null(Read(Status() with { SystemId = "li02" }));
        Assert.Null(Read(Status() with { Endpoint = "10.20.0.32:2300" }));
    }

    [Fact]
    public void Read_ReportsMissingOrMalformedFilesAsUnavailable()
    {
        var path = Path.Combine(Path.GetTempPath(), $"lancer-runtime-status-{Guid.NewGuid():N}.json");
        var reader = new InstanceRuntimeStatusReader(path, "liberty-01", "li01", "10.20.0.31:2300");
        Assert.Null(reader.Read(Now));

        try
        {
            File.WriteAllText(path, "not-json");
            Assert.Null(reader.Read(Now));
        }
        finally
        {
            if (File.Exists(path)) File.Delete(path);
        }
    }

    private static InstanceRuntimeStatus? Read(InstanceRuntimeStatus status)
    {
        var path = Path.Combine(Path.GetTempPath(), $"lancer-runtime-status-{Guid.NewGuid():N}.json");
        try
        {
            File.WriteAllText(path, JsonSerializer.Serialize(status));
            return new InstanceRuntimeStatusReader(path, "liberty-01", "li01", "10.20.0.31:2300").Read(Now);
        }
        finally
        {
            if (File.Exists(path)) File.Delete(path);
        }
    }

    private static InstanceRuntimeStatus Status() => new()
    {
        WrittenAtUtc = Now,
        InstanceId = "liberty-01",
        SystemId = "li01",
        IsReady = true,
        CurrentPlayers = 17,
        MaxPlayers = 200,
        Endpoint = "10.20.0.31:2300"
    };
}
