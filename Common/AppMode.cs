namespace Common;

// Top-level operational mode for the simulator. Each mode scopes how many
// ECUs the user can configure, whether ECU state persists across restarts,
// and which tabs / fields surface in the editor pane.
//
// EcuSimulator is the original behaviour: multiple ECUs, full editor pane,
// state persists to ecu_simulator_config.json. The DPS modes are single-ECU
// workflows for DPS programming. OBD-II Mode $01 emulation is supported
// inside EcuSimulator via per-PID PidMode selection - no separate top-level
// mode for it.
public enum AppMode
{
    EcuSimulator = 0,
    DpsWrite = 1,
    DpsRead = 2,
}

public static class AppModeExtensions
{
    public static bool AllowsMultipleEcus(this AppMode mode)
        => mode is AppMode.EcuSimulator;

    // DPS sessions are intended to be clean per-run: the user primes from an
    // archive, exercises a flow, and the ECU evaporates on exit. Everything
    // else persists its ECU config to disk.
    public static bool PersistsConfig(this AppMode mode) =>
        mode is AppMode.EcuSimulator;

    public static string ConfigFileName(this AppMode mode) => mode switch
    {
        AppMode.EcuSimulator => "ecu_simulator_config.json",
        AppMode.DpsWrite     => "dps_write_config.json",
        AppMode.DpsRead      => "dps_read_config.json",
        _ => "ecu_simulator_config.json",
    };

    public static string DisplayName(this AppMode mode) => mode switch
    {
        AppMode.EcuSimulator => "ECU Simulator",
        AppMode.DpsWrite     => "DPS Write",
        AppMode.DpsRead      => "DPS Read",
        _ => mode.ToString(),
    };
}
