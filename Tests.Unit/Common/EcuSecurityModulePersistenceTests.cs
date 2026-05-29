using System.Text.Json;
using Common.Persistence;
using Core.Ecu;
using Core.Persistence;
using Core.Security;
using Xunit;

namespace EcuSimulator.Tests.Persistence;

// Pins the security-module selection persistence chain so the Advanced
// card's Sec module ComboBox sticks across restarts. Two save paths are
// covered: the SimulatorConfig auto-save (ConfigSerializer.Options,
// camelCase) and the per-ECU *.ecu.json save (SaveEcuCore's default
// JsonSerializerOptions, PascalCase). Both must restore the picked module
// id when read back.
public sealed class EcuSecurityModulePersistenceTests
{
    [Fact]
    public void EcuDtoFrom_CapturesSecurityModuleId()
    {
        var node = new EcuNode { Name = "ECM",
            PhysicalRequestCanId = 0x7E0, UsdtResponseCanId = 0x7E8, UudtResponseCanId = 0x5E8 };
        node.SecurityModule = SecurityModuleRegistry.Create("gm-e92-5byte");

        var dto = ConfigStore.EcuDtoFrom(node);

        Assert.Equal("gm-e92-5byte", dto.SecurityModuleId);
    }

    [Fact]
    public void EcuNodeFrom_RestoresSecurityModuleFromId()
    {
        var dto = new EcuDto
        {
            Name = "ECM",
            PhysicalRequestCanId = 0x7E0,
            UsdtResponseCanId = 0x7E8,
            UudtResponseCanId = 0x5E8,
            SecurityModuleId = "gm-e92-5byte",
        };

        var node = ConfigStore.EcuNodeFrom(dto);

        Assert.NotNull(node.SecurityModule);
        Assert.Equal("gm-e92-5byte", node.SecurityModule!.Id);
    }

    [Fact]
    public void SimulatorConfig_RoundTrip_PreservesSecurityModuleId()
    {
        var node = new EcuNode { Name = "ECM",
            PhysicalRequestCanId = 0x7E0, UsdtResponseCanId = 0x7E8, UudtResponseCanId = 0x5E8 };
        node.SecurityModule = SecurityModuleRegistry.Create("gm-e92-5byte");
        var cfg = new SimulatorConfig
        {
            Version = SimulatorConfig.CurrentVersion,
            Ecus = { ConfigStore.EcuDtoFrom(node) },
        };

        var json = ConfigSerializer.Serialize(cfg);
        var roundTripped = ConfigSerializer.Deserialize(json);

        Assert.Single(roundTripped.Ecus);
        Assert.Equal("gm-e92-5byte", roundTripped.Ecus[0].SecurityModuleId);
    }

    [Fact]
    public void EcuJsonFile_RoundTrip_PreservesSecurityModuleId()
    {
        // Mirrors SaveEcuCore / LoadEcu - default JsonSerializerOptions with
        // WriteIndented=true. PascalCase property names; round-trips OK
        // because both sides use the same default casing.
        var node = new EcuNode { Name = "ECM",
            PhysicalRequestCanId = 0x7E0, UsdtResponseCanId = 0x7E8, UudtResponseCanId = 0x5E8 };
        node.SecurityModule = SecurityModuleRegistry.Create("gm-e92-5byte");
        var dto = ConfigStore.EcuDtoFrom(node);
        var opts = new JsonSerializerOptions { WriteIndented = true };

        var json = JsonSerializer.Serialize(dto, opts);
        var roundTripped = JsonSerializer.Deserialize<EcuDto>(json, opts);

        Assert.NotNull(roundTripped);
        Assert.Equal("gm-e92-5byte", roundTripped!.SecurityModuleId);
    }
}
