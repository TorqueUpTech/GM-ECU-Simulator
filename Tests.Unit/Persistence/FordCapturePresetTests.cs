using Common.Persistence;
using Core.Ecu.Personas;
using Core.Persistence;
using Xunit;

namespace EcuSimulator.Tests.Persistence;

// Guards the in-repo preset at `ecu_simulator_config_ford_capture.json` so we
// catch schema drift before the user has to: tests pre-bake the expected
// camelCase JSON and round-trip it through ConfigSerializer + ConfigStore.
//
// History: the first version of this preset was written with PascalCase
// keys ("Version", "Ecus", "Name", "PersonaId") and version=1. The serializer
// is configured with JsonNamingPolicy.CamelCase + Version validation that
// rejects below MinSupportedVersion (1)... but cfg.Version = 1 actually
// passed the range check while the PascalCase keys silently deserialised to
// every property's default value, including Ecus=[]. The simulator then sat
// there with zero ECUs and no on-screen indication of why. Hence this test.
public sealed class FordCapturePresetTests
{
    // Repo-relative path. The tests run from
    // Tests.Unit/bin/Debug/net9.0-windows/, so ../../../../ed back to the repo
    // root.
    private static string PresetPath()
    {
        var dir = Path.GetDirectoryName(typeof(FordCapturePresetTests).Assembly.Location)!;
        return Path.GetFullPath(Path.Combine(dir,
            "..", "..", "..", "..", "ecu_simulator_config_ford_capture.json"));
    }

    [Fact]
    public void Preset_File_Exists()
    {
        Assert.True(File.Exists(PresetPath()),
            $"Ford-capture preset missing at {PresetPath()}");
    }

    [Fact]
    public void Preset_Deserialises_With_CamelCase()
    {
        var json = File.ReadAllText(PresetPath());
        var cfg = ConfigSerializer.Deserialize(json);

        // Sanity: a PascalCase-typed file would silently land here with
        // Ecus=[], so the meaningful assertion is non-empty + persona id.
        Assert.NotNull(cfg);
        Assert.Equal(SimulatorConfig.CurrentVersion, cfg.Version);
        Assert.Single(cfg.Ecus);
        var ecu = cfg.Ecus[0];
        Assert.Equal((ushort)0x7E0, ecu.PhysicalRequestCanId);
        Assert.Equal((ushort)0x7E8, ecu.UsdtResponseCanId);
        Assert.Equal((ushort)0x5E8, ecu.UudtResponseCanId);
        Assert.Equal("ford-capture", ecu.PersonaId);
    }

    [Fact]
    public void Preset_Builds_Node_With_FordCapturePersona()
    {
        var json = File.ReadAllText(PresetPath());
        var cfg = ConfigSerializer.Deserialize(json);
        var node = ConfigStore.EcuNodeFrom(cfg.Ecus[0]);

        Assert.Same(FordCapturePersona.Instance, node.Persona);
    }
}
