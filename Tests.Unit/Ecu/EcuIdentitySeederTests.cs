using Common.Protocol;
using Core.Ecu;
using Core.Persistence;
using Core.Services;
using EcuSimulator.Tests.TestHelpers;
using System.Text;
using Xunit;

namespace EcuSimulator.Tests.Ecu;

// Covers EcuIdentitySeeder - the baseline-identity seeding a freshly-created
// EcuSimulator ECU gets so a tester can query it the moment it appears on the
// bus. The seeder writes Mode1A StaticBytes PID rows; these tests pin the
// three properties that choice was made for (persistence, precedence safety,
// and the Service3B write-through that keeps a tester's VIN write durable).
public sealed class EcuIdentitySeederTests
{
    // A fresh ECU should be born with a Mode1A VIN row carrying the synthetic
    // placeholder from DefaultDidValues - that is the whole point of seeding.
    [Fact]
    public void Seed_AddsMode1AVinRow_FromDefaultDidValues()
    {
        var node = NodeFactory.CreateNode();

        EcuIdentitySeeder.Seed(node);

        var row = node.GetMode1APid(0x90);
        Assert.NotNull(row);
        Assert.Equal(PidMode.Mode1A, row!.Mode);
        Assert.Equal(DefaultDidValues.Get(0x90), row.StaticBytes);
        Assert.Equal(17, row.ResponseLength);
    }

    // A blank ECU should be born with the curated E38/E67-realistic identity set, not just a lone VIN, so its $1A
    // section shows what we support out of the box. Every seeded DID must materialise as a Mode1A row carrying its
    // DefaultDidValues placeholder.
    [Fact]
    public void Seed_AddsTheCuratedIdentitySet()
    {
        var node = NodeFactory.CreateNode();

        EcuIdentitySeeder.Seed(node);

        Assert.True(EcuIdentitySeeder.SeededDids.Length > 1, "expected more than just the VIN");
        foreach (var did in EcuIdentitySeeder.SeededDids)
        {
            var row = node.GetMode1APid(did);
            Assert.NotNull(row);
            Assert.Equal(DefaultDidValues.Get(did), row!.StaticBytes);
        }
    }

    // Primed (DPS) ECUs derive their identity from the archive / donor bin.
    // The seeder must leave them untouched even if a caller invokes it.
    [Fact]
    public void Seed_PrimedNode_IsNoOp()
    {
        var node = NodeFactory.CreateNode();
        node.IsPrimed = true;

        EcuIdentitySeeder.Seed(node);

        Assert.Null(node.GetMode1APid(0x90));
    }

    // Seeding is precedence-safe: a VIN row that already exists (a loaded
    // config row, a bin-extracted value) wins over the synthetic default.
    [Fact]
    public void Seed_DoesNotOverwriteExistingMode1ARow()
    {
        var node = NodeFactory.CreateNode();
        var custom = Encoding.ASCII.GetBytes("1GCRKSE36BZ158034");
        node.AddPid(new Pid
        {
            Mode = PidMode.Mode1A, Address = 0x90, Name = "VIN",
            LengthBytes = custom.Length, StaticBytes = custom,
        });

        EcuIdentitySeeder.Seed(node);

        Assert.Equal(custom, node.GetMode1APid(0x90)!.StaticBytes);
    }

    // The seeded row must survive a save/load. It rides the v15 PidDto path,
    // so an EcuDto round-trip is the cheapest faithful check of persistence.
    [Fact]
    public void Seed_SurvivesConfigRoundTrip()
    {
        var node = NodeFactory.CreateNode();
        EcuIdentitySeeder.Seed(node);

        var reloaded = ConfigStore.EcuNodeFrom(ConfigStore.EcuDtoFrom(node));

        Assert.Equal(DefaultDidValues.Get(0x90), reloaded.GetMode1APid(0x90)!.StaticBytes);
    }

    // A $3B VIN write on a seeded ECU must update the Mode1A row in place (not
    // the identifier dictionary, which $1A reads second and which no longer
    // persists), and the new value must round-trip through a save/load.
    [Fact]
    public void Service3BWrite_UpdatesSeededRow_AndPersists()
    {
        var node = NodeFactory.CreateNode();
        EcuIdentitySeeder.Seed(node);
        node.State.SecurityUnlockedLevel = 1;       // $3B $90 requires security unlock
        var ch = NodeFactory.CreateChannel();

        var newVin = Encoding.ASCII.GetBytes("1GCRKSE36BZ158034");
        var req = new byte[2 + 17];
        req[0] = 0x3B;
        req[1] = 0x90;
        newVin.CopyTo(req.AsSpan(2));

        Assert.True(Service3BHandler.Handle(node, req, ch));
        Assert.Equal(new byte[] { 0x7B, 0x90 }, TestFrame.DequeueSingleFrameUsdt(ch));

        // Write landed on the Mode1A row, not the identifier dictionary.
        Assert.Equal(newVin, node.GetMode1APid(0x90)!.StaticBytes);
        Assert.Null(node.GetIdentifier(0x90));

        var reloaded = ConfigStore.EcuNodeFrom(ConfigStore.EcuDtoFrom(node));
        Assert.Equal(newVin, reloaded.GetMode1APid(0x90)!.StaticBytes);
    }
}
