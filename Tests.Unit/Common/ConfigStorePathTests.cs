using Common;
using Core.Persistence;
using Xunit;

namespace EcuSimulator.Tests.AppModes;

public sealed class ConfigStorePathTests
{
    [Theory]
    [InlineData(AppMode.EcuSimulator, "ecu_simulator_config.json")]
    [InlineData(AppMode.DpsWrite,     "dps_write_config.json")]
    [InlineData(AppMode.DpsRead,      "dps_read_config.json")]
    public void PathForMode_ResolvesToLocalAppDataWithModeFilename(AppMode mode, string expectedFile)
    {
        var path = ConfigStore.PathForMode(mode);

        // Filename is per-mode and stable
        Assert.Equal(expectedFile, Path.GetFileName(path));

        // Lives under %LOCALAPPDATA%\GmEcuSimulator\config
        var expectedDir = Path.Combine(
            Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData),
            "GmEcuSimulator", "config");
        Assert.Equal(expectedDir, Path.GetDirectoryName(path));
    }
}
