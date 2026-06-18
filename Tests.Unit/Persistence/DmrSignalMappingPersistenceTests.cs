using Common.Persistence;
using Common.Signals;
using Core.Ecu;
using Core.Persistence;
using System.Text.Json;
using Xunit;

namespace EcuSimulator.Tests.Persistence;

// Round-trip for the Ford-persona EcuDto.DmrSignalMappings (DMR address -> engine signal map).
public sealed class DmrSignalMappingPersistenceTests
{
    private static EcuNode NodeWithMappings()
    {
        var node = new EcuNode { Name = "Ford PCM", PhysicalRequestCanId = 0x7E0, UsdtResponseCanId = 0x7E8, UudtResponseCanId = 0x5E8 };
        node.AddDmrSignalMapping(new DmrSignalMapping { Address = 0x003F7FA0, Name = "Engine RPM", Signal = SignalId.EngineRpm });
        node.AddDmrSignalMapping(new DmrSignalMapping
        {
            Address = 0x003F86EC, Name = "Road Speed", Signal = SignalId.VehicleSpeed,
            Encoding = DmrValueEncoding.UInt16BE, Scale = 2.0, Offset = 10.0,
        });
        return node;
    }

    [Fact]
    public void EcuDto_RoundTrip_PreservesDmrSignalMappings()
    {
        var dto = ConfigStore.EcuDtoFrom(NodeWithMappings());
        var node2 = ConfigStore.EcuNodeFrom(dto);

        Assert.Equal(2, node2.DmrSignalMappings.Count);
        var rpm = node2.DmrMappingFor(0x003F7FA0)!;
        Assert.Equal(SignalId.EngineRpm, rpm.Signal);
        Assert.Equal(DmrValueEncoding.Float32BE, rpm.Encoding);   // default preserved
        Assert.Equal(1.0, rpm.Scale, 6);
        var spd = node2.DmrMappingFor(0x003F86EC)!;
        Assert.Equal(SignalId.VehicleSpeed, spd.Signal);
        Assert.Equal(DmrValueEncoding.UInt16BE, spd.Encoding);
        Assert.Equal(2.0, spd.Scale, 6);
        Assert.Equal(10.0, spd.Offset, 6);
        Assert.Null(node2.DmrMappingFor(0x00000001));   // unmapped -> null
    }

    [Fact]
    public void ConfigWithoutMappings_LoadsEmpty_AndIsOmittedWhenEmpty()
    {
        var dto = new EcuDto
        {
            Name = "ECM", PhysicalRequestCanId = 0x7E0, UsdtResponseCanId = 0x7E8, UudtResponseCanId = 0x5E8,
            DmrSignalMappings = null,
        };
        var node = ConfigStore.EcuNodeFrom(dto);
        Assert.Empty(node.DmrSignalMappings);
        // A node with no mappings serializes the field as null (kept out of standard configs).
        Assert.Null(ConfigStore.EcuDtoFrom(node).DmrSignalMappings);
    }

    [Fact]
    public void Address_SerializesAsHexString()
    {
        var dto = ConfigStore.EcuDtoFrom(NodeWithMappings());
        string json = JsonSerializer.Serialize(dto, ConfigSerializer.Options);
        Assert.Contains("0x003F7FA0", json);   // HexUIntConverter
    }
}
