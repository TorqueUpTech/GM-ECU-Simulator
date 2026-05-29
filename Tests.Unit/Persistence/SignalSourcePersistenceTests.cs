using Common.Persistence;
using Common.Protocol;
using Common.Signals;
using Core.Ecu;
using Core.Persistence;
using EcuSimulator.Tests.TestHelpers;
using Xunit;

namespace EcuSimulator.Tests.Persistence;

// v16 round-trip: a signal-backed PID's Signal source and the ECU's boot scenario survive save/load, and both
// serialise as human-editable camelCase strings. A node left at the default Idle scenario stays quiet on disk.
public sealed class SignalSourcePersistenceTests
{
    private static EcuNode BuildNode()
    {
        var node = NodeFactory.CreateNode();
        node.AddPid(new Pid
        {
            Mode = PidMode.Mode22,
            Address = 0x000C,
            Size = PidSize.Word,
            Scalar = 0.25,
            Signal = SignalId.EngineRpm,
        });
        node.EngineModel.SetScenario(ScenarioId.Cruise, 0);
        return node;
    }

    [Fact]
    public void EcuDto_RoundTrip_PreservesSignalAndScenario()
    {
        var dto = ConfigStore.EcuDtoFrom(BuildNode());
        var restored = ConfigStore.EcuNodeFrom(dto);

        Assert.Equal(ScenarioId.Cruise, restored.EngineModel.ActiveScenario);
        var pid = restored.GetPidByWireId(0x000C);
        Assert.NotNull(pid);
        Assert.Equal(SignalId.EngineRpm, pid!.Signal);
    }

    [Fact]
    public void JsonRoundTrip_PreservesSignalAndScenario()
    {
        var cfg = new SimulatorConfig { Ecus = { ConfigStore.EcuDtoFrom(BuildNode()) } };
        var json = ConfigSerializer.Serialize(cfg);
        var back = ConfigSerializer.Deserialize(json);

        var ecu = Assert.Single(back.Ecus);
        Assert.Equal(ScenarioId.Cruise, ecu.Scenario);
        var pid = Assert.Single(ecu.Pids);
        Assert.Equal(SignalId.EngineRpm, pid.Signal);

        // Stored as camelCase strings (human-editable), not bare enum numbers.
        Assert.Contains("\"signal\": \"engineRpm\"", json);
        Assert.Contains("\"scenario\": \"cruise\"", json);
    }

    [Fact]
    public void EcuDto_DefaultIdleScenario_IsNotPersisted()
    {
        var dto = ConfigStore.EcuDtoFrom(NodeFactory.CreateNode());
        Assert.Null(dto.Scenario);
    }

    [Fact]
    public void EcuDto_DefaultSweepProfile_IsNotPersisted()
    {
        // A node left on the default sweep profile stays quiet on disk - every tuning field is null.
        var dto = ConfigStore.EcuDtoFrom(NodeFactory.CreateNode());
        Assert.Null(dto.SweepAccelMs);
        Assert.Null(dto.SweepLimiterHoldMs);
        Assert.Null(dto.SweepDecelMs);
        Assert.Null(dto.SweepCrossfadeMs);
        Assert.Null(dto.SweepLimiterCutRpm);
    }

    [Fact]
    public void EcuDto_RoundTrip_PreservesTunedSweepProfile()
    {
        var node = NodeFactory.CreateNode();
        node.EngineModel.Sweep = SweepProfile.Default with
        {
            AccelTimeMs = 6000,
            LimiterHoldMs = 3000,
            DecelTimeMs = 4000,
            CrossfadeMs = 250,
            LimiterBounceRpm = 40,
        };

        var restored = ConfigStore.EcuNodeFrom(ConfigStore.EcuDtoFrom(node)).EngineModel.Sweep;

        Assert.Equal(6000, restored.AccelTimeMs);
        Assert.Equal(3000, restored.LimiterHoldMs);
        Assert.Equal(4000, restored.DecelTimeMs);
        Assert.Equal(250, restored.CrossfadeMs);
        Assert.Equal(40, restored.LimiterBounceRpm);
    }
}
