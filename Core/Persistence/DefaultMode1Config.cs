using Common.Persistence;
using Common.Protocol;
using Common.Waveforms;
using Core.Bus;

namespace Core.Persistence;

// First-launch fallback for AppMode.Mode1 (OBD-II Service $01 emulator).
// Hydrates the bus with one ECM at the standardised $7E0/$7E8 pair and a
// stub set of the most common Mode $01 PIDs (1-byte identifiers per SAE
// J1979). Scaling formulas follow the public OBD-II PID spec so the values
// the host reads look plausible against a real scan tool.
//
// This is intentionally a STUB - the live values come from the waveform
// generators wired into each Pid, and the GMW3110 $22 service is still what
// answers requests. A dedicated Mode $01 service handler with 1-byte PID
// dispatch lands in a follow-up; until then these entries seed the editor
// pane so the user has something to inspect and refine.
public static class DefaultMode1Config
{
    public static SimulatorConfig Build() => new()
    {
        Version = SimulatorConfig.CurrentVersion,
        Description = "OBD-II Mode $01 ECM stub (1-byte PIDs, SAE J1979 scaling)",
        Ecus =
        {
            new EcuDto
            {
                Name = "ECM",
                PhysicalRequestCanId = 0x7E0,
                UsdtResponseCanId    = 0x7E8,
                UudtResponseCanId    = 0x5E8,
                Pids =
                {
                    // $04 Calculated engine load - 1 byte, 100/255 * A %
                    new PidDto
                    {
                        Mode = PidMode.Mode1,
                        Address = 0x04, Name = "Calculated engine load",
                        Size = PidSize.Byte, DataType = PidDataType.Unsigned,
                        Scalar = 100.0 / 255.0, Offset = 0.0, Unit = "%",
                        Waveform = new WaveformDto { Shape = WaveformShape.Sin,
                                                     Amplitude = 80, Offset = 100, FrequencyHz = 0.1 },
                    },
                    // $05 Engine coolant temperature - 1 byte, A - 40 deg C
                    new PidDto
                    {
                        Mode = PidMode.Mode1,
                        Address = 0x05, Name = "Engine coolant temperature",
                        Size = PidSize.Byte, DataType = PidDataType.Unsigned,
                        Scalar = 1.0, Offset = -40.0, Unit = "°C",
                        Waveform = new WaveformDto { Shape = WaveformShape.Sin,
                                                     Amplitude = 30, Offset = 130, FrequencyHz = 0.05 },
                    },
                    // $0B Intake manifold absolute pressure - 1 byte, A kPa
                    new PidDto
                    {
                        Mode = PidMode.Mode1,
                        Address = 0x0B, Name = "Intake manifold pressure",
                        Size = PidSize.Byte, DataType = PidDataType.Unsigned,
                        Scalar = 1.0, Offset = 0.0, Unit = "kPa",
                        Waveform = new WaveformDto { Shape = WaveformShape.Sin,
                                                     Amplitude = 40, Offset = 60, FrequencyHz = 0.2 },
                    },
                    // $0C Engine RPM - 2 bytes, (256*A + B) / 4
                    new PidDto
                    {
                        Mode = PidMode.Mode1,
                        Address = 0x0C, Name = "Engine RPM",
                        Size = PidSize.Word, DataType = PidDataType.Unsigned,
                        Scalar = 0.25, Offset = 0.0, Unit = "rpm",
                        Waveform = new WaveformDto { Shape = WaveformShape.Sin,
                                                     Amplitude = 2500, Offset = 3000, FrequencyHz = 0.1 },
                    },
                    // $0D Vehicle speed - 1 byte, A km/h
                    new PidDto
                    {
                        Mode = PidMode.Mode1,
                        Address = 0x0D, Name = "Vehicle speed",
                        Size = PidSize.Byte, DataType = PidDataType.Unsigned,
                        Scalar = 1.0, Offset = 0.0, Unit = "km/h",
                        Waveform = new WaveformDto { Shape = WaveformShape.Triangle,
                                                     Amplitude = 60, Offset = 80, FrequencyHz = 0.03 },
                    },
                    // $0F Intake air temperature - 1 byte, A - 40 deg C
                    new PidDto
                    {
                        Mode = PidMode.Mode1,
                        Address = 0x0F, Name = "Intake air temperature",
                        Size = PidSize.Byte, DataType = PidDataType.Unsigned,
                        Scalar = 1.0, Offset = -40.0, Unit = "°C",
                        Waveform = new WaveformDto { Shape = WaveformShape.Sin,
                                                     Amplitude = 10, Offset = 65, FrequencyHz = 0.08 },
                    },
                    // $10 MAF air flow rate - 2 bytes, (256*A + B) / 100 g/s
                    new PidDto
                    {
                        Mode = PidMode.Mode1,
                        Address = 0x10, Name = "MAF air flow rate",
                        Size = PidSize.Word, DataType = PidDataType.Unsigned,
                        Scalar = 0.01, Offset = 0.0, Unit = "g/s",
                        Waveform = new WaveformDto { Shape = WaveformShape.Sin,
                                                     Amplitude = 1500, Offset = 2000, FrequencyHz = 0.15 },
                    },
                    // $11 Throttle position - 1 byte, 100/255 * A %
                    new PidDto
                    {
                        Mode = PidMode.Mode1,
                        Address = 0x11, Name = "Throttle position",
                        Size = PidSize.Byte, DataType = PidDataType.Unsigned,
                        Scalar = 100.0 / 255.0, Offset = 0.0, Unit = "%",
                        Waveform = new WaveformDto { Shape = WaveformShape.Triangle,
                                                     Amplitude = 100, Offset = 128, FrequencyHz = 0.12 },
                    },
                    // $1F Run time since engine start - 2 bytes, 256*A + B seconds
                    new PidDto
                    {
                        Mode = PidMode.Mode1,
                        Address = 0x1F, Name = "Run time since engine start",
                        Size = PidSize.Word, DataType = PidDataType.Unsigned,
                        Scalar = 1.0, Offset = 0.0, Unit = "s",
                        Waveform = new WaveformDto { Shape = WaveformShape.Sawtooth,
                                                     Amplitude = 32000, Offset = 32000, FrequencyHz = 0.001 },
                    },
                    // $2F Fuel tank level input - 1 byte, 100/255 * A %
                    new PidDto
                    {
                        Mode = PidMode.Mode1,
                        Address = 0x2F, Name = "Fuel tank level",
                        Size = PidSize.Byte, DataType = PidDataType.Unsigned,
                        Scalar = 100.0 / 255.0, Offset = 0.0, Unit = "%",
                        Waveform = new WaveformDto { Shape = WaveformShape.Sawtooth,
                                                     Amplitude = 200, Offset = 100, FrequencyHz = 0.002 },
                    },
                    // $46 Ambient air temperature - 1 byte, A - 40 deg C
                    new PidDto
                    {
                        Mode = PidMode.Mode1,
                        Address = 0x46, Name = "Ambient air temperature",
                        Size = PidSize.Byte, DataType = PidDataType.Unsigned,
                        Scalar = 1.0, Offset = -40.0, Unit = "°C",
                        Waveform = new WaveformDto { Shape = WaveformShape.Sin,
                                                     Amplitude = 5, Offset = 60, FrequencyHz = 0.02 },
                    },
                    // $5C Engine oil temperature - 1 byte, A - 40 deg C
                    new PidDto
                    {
                        Mode = PidMode.Mode1,
                        Address = 0x5C, Name = "Engine oil temperature",
                        Size = PidSize.Byte, DataType = PidDataType.Unsigned,
                        Scalar = 1.0, Offset = -40.0, Unit = "°C",
                        Waveform = new WaveformDto { Shape = WaveformShape.Sin,
                                                     Amplitude = 25, Offset = 130, FrequencyHz = 0.04 },
                    },
                },
            },
        },
    };

    /// <summary>
    /// One-time apply when the user switches into Mode 1 and no saved config
    /// exists at <see cref="ConfigStore.PathForMode"/>. No-op if the bus
    /// already has nodes.
    /// </summary>
    public static void ApplyIfEmpty(VirtualBus bus)
    {
        if (bus.Nodes.Count > 0) return;
        ConfigStore.ApplyTo(Build(), bus);
    }
}
