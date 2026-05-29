using Common.Protocol;
using Common.Waveforms;

namespace Common.Persistence;

// Curated $22 PIDs that always render as live waveforms, even when a bin
// extractor would otherwise plant a static-byte placeholder at the same
// id. The intent is "bin-driven ECU creation keeps the demo feel of live
// signals" - identity DIDs come from the bin, but RPM/MAP/ECT/etc. stay
// dynamic so a tester scanning the ECU sees changing numbers.
//
// The id space is OBD-II Mode $01 numerically; on GM, $22 0x000C and $01
// $0C both ask for RPM and the wire-encoding conventions match (this is
// what the E38 PID-table extractor assumes too). Scalar/Offset are the
// canonical OBD-II per-PID values - waveform amplitudes/offsets are in
// engineering units (RPM, °C, kPa, ...) and ValueCodec.Encode converts
// to raw counts via `raw = (eng - Offset) / Scalar`.
//
// Code, not JSON: the list is small, code-stable, and WaveformConfig
// already serialises through ConfigSchema if a user saves an ECU built
// from this library. A separate JSON resource loader would add layers
// for no benefit.
public static class AlwaysDynamicPids
{
    public sealed record Entry(
        ushort Pid,
        string Name,
        int LengthBytes,
        PidDataType DataType,
        double Scalar,
        double Offset,
        string Unit,
        WaveformConfig Waveform);

    public static IReadOnlyDictionary<ushort, Entry> ById { get; } = Build();

    private static IReadOnlyDictionary<ushort, Entry> Build()
    {
        var d = new Dictionary<ushort, Entry>();

        // 0x0C Engine RPM: ((A*256)+B)/4 rpm. Waveform 600-7000 RPM (idle
        // through redline), 0.5 Hz sine - tester sees a slow rev sweep.
        Add(d, 0x000C, "Engine RPM", 2, PidDataType.Unsigned,
            scalar: 0.25, offset: 0.0, unit: "rpm",
            shape: WaveformShape.Sin, amp: 3200, off: 3800, freqHz: 0.5);

        // 0x0D Vehicle Speed: A km/h. 0-120 km/h triangle, 0.1 Hz.
        Add(d, 0x000D, "Vehicle Speed", 1, PidDataType.Unsigned,
            scalar: 1.0, offset: 0.0, unit: "km/h",
            shape: WaveformShape.Triangle, amp: 60, off: 60, freqHz: 0.1);

        // 0x0B Intake MAP: A kPa. 20-100 kPa sine, 0.7 Hz - approximates
        // load cycling during the RPM sweep.
        Add(d, 0x000B, "Intake MAP", 1, PidDataType.Unsigned,
            scalar: 1.0, offset: 0.0, unit: "kPa",
            shape: WaveformShape.Sin, amp: 40, off: 60, freqHz: 0.7);

        // 0x05 ECT: A-40 °C. 40-95 °C sawtooth, 0.05 Hz - feels like a
        // slow warmup from cold-start back to operating temp.
        Add(d, 0x0005, "ECT", 1, PidDataType.Unsigned,
            scalar: 1.0, offset: -40.0, unit: "°C",
            shape: WaveformShape.Sawtooth, amp: 27.5, off: 67.5, freqHz: 0.05);

        // 0x0F IAT: A-40 °C. 20-50 °C sine, 0.1 Hz.
        Add(d, 0x000F, "IAT", 1, PidDataType.Unsigned,
            scalar: 1.0, offset: -40.0, unit: "°C",
            shape: WaveformShape.Sin, amp: 15, off: 35, freqHz: 0.1);

        // 0x11 TPS: (100/255)*A %. 0-100 % triangle, 0.3 Hz.
        Add(d, 0x0011, "Throttle Position", 1, PidDataType.Unsigned,
            scalar: 100.0 / 255.0, offset: 0.0, unit: "%",
            shape: WaveformShape.Triangle, amp: 50, off: 50, freqHz: 0.3);

        // 0x10 MAF: ((A*256)+B)/100 g/s. 2-50 g/s sine, 0.5 Hz.
        Add(d, 0x0010, "MAF", 2, PidDataType.Unsigned,
            scalar: 0.01, offset: 0.0, unit: "g/s",
            shape: WaveformShape.Sin, amp: 24, off: 26, freqHz: 0.5);

        // 0x04 Calculated Engine Load: (100/255)*A %. 15-90 % sine, 0.5 Hz.
        Add(d, 0x0004, "Calculated Load", 1, PidDataType.Unsigned,
            scalar: 100.0 / 255.0, offset: 0.0, unit: "%",
            shape: WaveformShape.Sin, amp: 37.5, off: 52.5, freqHz: 0.5);

        // 0x2F Fuel Level: (100/255)*A %. Slow sawtooth drop 100->10 %,
        // 0.001 Hz (~16 min full cycle) so it looks like a real tank.
        Add(d, 0x002F, "Fuel Level", 1, PidDataType.Unsigned,
            scalar: 100.0 / 255.0, offset: 0.0, unit: "%",
            shape: WaveformShape.Sawtooth, amp: 45, off: 55, freqHz: 0.001);

        // 0x42 Control Module Voltage: ((A*256)+B)/1000 V. 13.5-14.5 V
        // sine, 0.05 Hz - normal running alternator voltage.
        Add(d, 0x0042, "Control Module Voltage", 2, PidDataType.Unsigned,
            scalar: 0.001, offset: 0.0, unit: "V",
            shape: WaveformShape.Sin, amp: 0.5, off: 14.0, freqHz: 0.05);

        return d;
    }

    private static void Add(Dictionary<ushort, Entry> d,
        ushort pid, string name, int lengthBytes, PidDataType dataType,
        double scalar, double offset, string unit,
        WaveformShape shape, double amp, double off, double freqHz)
    {
        d[pid] = new Entry(
            Pid: pid,
            Name: name,
            LengthBytes: lengthBytes,
            DataType: dataType,
            Scalar: scalar,
            Offset: offset,
            Unit: unit,
            Waveform: new WaveformConfig
            {
                Shape = shape,
                Amplitude = amp,
                Offset = off,
                FrequencyHz = freqHz,
            });
    }
}
