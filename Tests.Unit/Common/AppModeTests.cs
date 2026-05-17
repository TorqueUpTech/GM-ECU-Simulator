using Common;
using Xunit;

namespace EcuSimulator.Tests.AppModes;

public sealed class AppModeTests
{
    [Theory]
    [InlineData(AppMode.EcuSimulator,   true)]
    [InlineData(AppMode.DpsWrite,       false)]
    [InlineData(AppMode.DpsRead,        false)]
    [InlineData(AppMode.FlashToolWrite, false)]
    [InlineData(AppMode.FlashToolRead,  false)]
    public void AllowsMultipleEcus_IsTrueOnlyForEcuSimulator(AppMode mode, bool expected)
        => Assert.Equal(expected, mode.AllowsMultipleEcus());

    [Theory]
    [InlineData(AppMode.EcuSimulator,   true)]
    [InlineData(AppMode.FlashToolWrite, true)]
    [InlineData(AppMode.FlashToolRead,  true)]
    [InlineData(AppMode.DpsWrite,       false)]
    [InlineData(AppMode.DpsRead,        false)]
    public void PersistsConfig_IsFalseForDpsModes(AppMode mode, bool expected)
        => Assert.Equal(expected, mode.PersistsConfig());

    [Theory]
    [InlineData(AppMode.EcuSimulator,   "ecu_simulator_config.json")]
    [InlineData(AppMode.DpsWrite,       "dps_write_config.json")]
    [InlineData(AppMode.DpsRead,        "dps_read_config.json")]
    [InlineData(AppMode.FlashToolWrite, "flash_write_config.json")]
    [InlineData(AppMode.FlashToolRead,  "flash_read_config.json")]
    public void ConfigFileName_HasStableNamePerMode(AppMode mode, string expected)
        => Assert.Equal(expected, mode.ConfigFileName());

    [Theory]
    [InlineData(AppMode.EcuSimulator,   "ECU Simulator")]
    [InlineData(AppMode.DpsWrite,       "DPS Write")]
    [InlineData(AppMode.DpsRead,        "DPS Read")]
    [InlineData(AppMode.FlashToolWrite, "Flash Tool Write")]
    [InlineData(AppMode.FlashToolRead,  "Flash Tool Read")]
    public void DisplayName_MatchesUserVocabulary(AppMode mode, string expected)
        => Assert.Equal(expected, mode.DisplayName());

    [Fact]
    public void DisplayNames_AreAllDistinct()
    {
        var names = new[]
        {
            AppMode.EcuSimulator.DisplayName(),
            AppMode.DpsWrite.DisplayName(),
            AppMode.DpsRead.DisplayName(),
            AppMode.FlashToolWrite.DisplayName(),
            AppMode.FlashToolRead.DisplayName(),
        };
        Assert.Equal(names.Length, names.Distinct().Count());
    }

    [Fact]
    public void ConfigFileNames_AreAllDistinct()
    {
        var names = new[]
        {
            AppMode.EcuSimulator.ConfigFileName(),
            AppMode.DpsWrite.ConfigFileName(),
            AppMode.DpsRead.ConfigFileName(),
            AppMode.FlashToolWrite.ConfigFileName(),
            AppMode.FlashToolRead.ConfigFileName(),
        };
        Assert.Equal(names.Length, names.Distinct().Count());
    }
}
