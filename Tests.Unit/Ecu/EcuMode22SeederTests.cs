using Common.Protocol;
using Common.Signals;
using Core.Ecu;
using Core.Persistence;
using EcuSimulator.Tests.TestHelpers;
using Xunit;

namespace EcuSimulator.Tests.Ecu;

// Covers EcuMode22Seeder - the curated live $22 set a freshly-created ECU is born with so its $22 editor section
// isn't empty. Each seeded row uses a real GM DID + A2L scaling and is signal-backed, so the value moves with the
// scenario.
public sealed class EcuMode22SeederTests
{
    [Fact]
    public void Seed_AddsSignalBackedMode22Rows()
    {
        var node = NodeFactory.CreateNode();

        EcuMode22Seeder.Seed(node);

        Assert.NotEmpty(EcuMode22Seeder.Seeds);
        foreach (var s in EcuMode22Seeder.Seeds)
        {
            var row = node.GetPidByWireId(s.Did);
            Assert.NotNull(row);
            Assert.Equal(PidMode.Mode22, row!.Mode);
            Assert.Equal(s.Signal, row.Signal);          // signal-backed -> moves with the scenario
            Assert.Equal(s.Scalar, row.Scalar);
        }
    }

    [Fact]
    public void Seed_RpmRow_EncodesPlausibleIdleValue()
    {
        var node = NodeFactory.CreateNode();
        EcuMode22Seeder.Seed(node);

        // $1421 = Medres engine speed, 0.125 rpm/bit, big-endian. At the default Idle scenario (t=0, no dither) the
        // decoded value should sit in a plausible idle band.
        var rpm = node.GetPidByWireId(0x1421)!;
        var buf = new byte[rpm.ResponseLength];
        rpm.WriteResponseBytes(0, buf);
        int raw = (buf[0] << 8) | buf[1];
        double value = raw * 0.125;
        Assert.InRange(value, 400, 1300);
    }

    [Fact]
    public void Seed_PrimedNode_IsNoOp()
    {
        var node = NodeFactory.CreateNode();
        node.IsPrimed = true;

        EcuMode22Seeder.Seed(node);

        Assert.Null(node.GetPidByWireId(0x1421));
    }

    [Fact]
    public void Seed_DoesNotOverwriteExistingRow()
    {
        var node = NodeFactory.CreateNode();
        node.AddPid(new Pid { Mode = PidMode.Mode22, Address = 0x1421, Name = "custom", LengthBytes = 2 });

        EcuMode22Seeder.Seed(node);

        Assert.Equal("custom", node.GetPidByWireId(0x1421)!.Name);   // existing row wins
    }

    [Fact]
    public void Seed_SurvivesConfigRoundTrip()
    {
        var node = NodeFactory.CreateNode();
        EcuMode22Seeder.Seed(node);

        var reloaded = ConfigStore.EcuNodeFrom(ConfigStore.EcuDtoFrom(node));

        var row = reloaded.GetPidByWireId(0x1421);
        Assert.NotNull(row);
        Assert.Equal(SignalId.EngineRpm, row!.Signal);
    }
}
