namespace Common;

// Top-level operational mode for the simulator. Each mode scopes how many
// ECUs the user can configure, whether ECU state persists across restarts,
// and which tabs / fields surface in the editor pane.
//
// EcuSimulator is the original behaviour: multiple ECUs, full editor pane,
// state persists to ecu_simulator_config.json. The other four modes are
// single-ECU workflows added later for DPS programming and Flash-Tool
// readbacks.
public enum AppMode
{
    EcuSimulator = 0,
    DpsWrite = 1,
    DpsRead = 2,
    FlashToolWrite = 3,
    FlashToolRead = 4,
    // OBD-II Mode $01 emulator. Single virtual ECU at $7E0/$7E8 hosting the
    // standard 1-byte PID set ($04 engine load, $05 coolant temp, $0C RPM,
    // $0D speed, ...). PID list is a stub for now - real Mode $01 service
    // handler + 1-byte PID dispatch comes in a follow-up.
    Mode1 = 5,
}

public static class AppModeExtensions
{
    public static bool AllowsMultipleEcus(this AppMode mode)
        => mode is AppMode.EcuSimulator or AppMode.Mode1;

    // DPS sessions are intended to be clean per-run: the user primes from an
    // archive, exercises a flow, and the ECU evaporates on exit. Everything
    // else persists its ECU config to disk.
    public static bool PersistsConfig(this AppMode mode) =>
        mode is AppMode.EcuSimulator or AppMode.FlashToolWrite or AppMode.FlashToolRead or AppMode.Mode1;

    public static string ConfigFileName(this AppMode mode) => mode switch
    {
        AppMode.EcuSimulator   => "ecu_simulator_config.json",
        AppMode.DpsWrite       => "dps_write_config.json",
        AppMode.DpsRead        => "dps_read_config.json",
        AppMode.FlashToolWrite => "flash_write_config.json",
        AppMode.FlashToolRead  => "flash_read_config.json",
        AppMode.Mode1          => "mode1_config.json",
        _ => "ecu_simulator_config.json",
    };

    public static string DisplayName(this AppMode mode) => mode switch
    {
        AppMode.EcuSimulator   => "ECU Simulator",
        AppMode.DpsWrite       => "DPS Write",
        AppMode.DpsRead        => "DPS Read",
        AppMode.FlashToolWrite => "Flash Tool Write",
        AppMode.FlashToolRead  => "Flash Tool Read",
        AppMode.Mode1          => "Mode 1",
        _ => mode.ToString(),
    };
}
