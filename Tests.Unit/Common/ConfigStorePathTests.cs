using System.IO;
using Common;
using Core.Persistence;
using Xunit;

namespace EcuSimulator.Tests.AppModes;

public sealed class ConfigStorePathTests
{
    [Theory]
    [InlineData(AppMode.EcuSimulator,   "ecu_simulator_config.json")]
    [InlineData(AppMode.DpsWrite,       "dps_write_config.json")]
    [InlineData(AppMode.DpsRead,        "dps_read_config.json")]
    [InlineData(AppMode.FlashToolWrite, "flash_write_config.json")]
    [InlineData(AppMode.FlashToolRead,  "flash_read_config.json")]
    public void PathForMode_ResolvesToLocalAppDataWithModeFilename(AppMode mode, string expectedFile)
    {
        var path = ConfigStore.PathForMode(mode);

        // Filename is per-mode and stable
        Assert.Equal(expectedFile, Path.GetFileName(path));

        // Lives under %LOCALAPPDATA%\GmEcuSimulator
        var expectedDir = Path.Combine(
            Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData),
            "GmEcuSimulator");
        Assert.Equal(expectedDir, Path.GetDirectoryName(path));
    }

    [Fact]
    public void MigrateLegacyConfigFile_DoesNotThrowWhenNoLegacyPresent()
    {
        // Idempotent / safe: a fresh install (no legacy config.json) must
        // tolerate the call without throwing or creating files.
        ConfigStore.MigrateLegacyConfigFile();
    }
}
