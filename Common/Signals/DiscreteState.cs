namespace Common.Signals;

// The non-analog ECU state behind the OBD-II status PIDs - the things that are not a sensor reading: lamp state, how
// many DTCs are stored, which sensors exist, and which standards the ECU claims. Mostly static per ECU and
// editor-configurable. The one moving piece, fuel-system status ($03), is NOT stored here: it is derived live from
// EngineModel.IsClosedLoop at encode time so it tracks the scenario.
public sealed class DiscreteState
{
    // Malfunction Indicator Lamp and stored-DTC count, reported together in PID $01 byte A. Default is a healthy ECU.
    public bool MilOn { get; set; }
    public int StoredDtcCount { get; set; }

    // PID $13 "O2 sensors present" bitmap: bits 0-3 = bank 1 sensors 1-4, bits 4-7 = bank 2. Default is the common
    // dual-bank layout with one pre-cat and one post-cat sensor per bank (B1S1, B1S2, B2S1, B2S2).
    public byte O2SensorsPresent { get; set; } = 0b0011_0011;

    // PID $1C "OBD standards this vehicle conforms to". 0x01 = OBD-II as defined by CARB, the usual answer for a US
    // GM powertrain controller.
    public byte ObdStandard { get; set; } = 0x01;

    // PID $51 "Fuel type". 0x01 = gasoline.
    public byte FuelType { get; set; } = 0x01;
}
