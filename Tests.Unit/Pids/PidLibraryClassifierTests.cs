using Common.Pids;
using Common.Protocol;
using Core.Pids;
using Xunit;

namespace EcuSimulator.Tests.Pids;

// Heuristic classifier behaviour, pinned at the entry level so failures
// here surface mis-classifications without dragging in handlers / ISO-TP.
// Cases chosen to exercise each branch of the score cascade.
public sealed class PidLibraryClassifierTests
{
    private static PidLibraryEntry Entry(
        ushort did, int size, string kind = "", string name = "", string dataType = "UBYTE",
        double? lower = null, double? upper = null, string conv = "", string desc = "")
        => new(did, size, 0, kind, "", name, desc, dataType, lower, upper, conv, "", null, null);

    [Fact]
    public void OversizeEntries_AlwaysFixed_RegardlessOfOtherSignals()
    {
        // 17-byte VIN labelled MEASUREMENT with "Ba" prefix - waveform-leaning
        // by every other signal, but a scalar wave can't fill 17 bytes so the
        // size-cap forces Fixed.
        var vin = Entry(0x0028, size: 17, kind: "MEASUREMENT", name: "BaVINF_y_OdoVIN", desc: "Array stores the corresponding data for DIDs $28 (17-digit OdoVIN)");
        Assert.Equal(ResponseKind.Fixed, PidLibraryClassifier.Classify(vin, PidMode.Mode22));
    }

    [Fact]
    public void Mode1AEntries_AlwaysFixed_ByDesign()
    {
        // GMW3110 identifier space - even an unmarked 1-byte UBYTE row stays
        // Fixed in Mode1A, matching the "identifiers are stored config" rule.
        var blank = Entry(0x00B6, size: 1);
        Assert.Equal(ResponseKind.Fixed, PidLibraryClassifier.Classify(blank, PidMode.Mode1A));
    }

    [Fact]
    public void Characteristic_With_KePrefix_IsFixed()
    {
        // KeTPSC_Raw_Slope_TPS_1 - CHARACTERISTIC (cal parameter) + "Ke"
        // prefix (calibration constant). Both signals push to Fixed.
        var cal = Entry(0x0011, size: 1, kind: "CHARACTERISTIC", name: "KeTPSC_Raw_Slope_TPS_1");
        Assert.Equal(ResponseKind.Fixed, PidLibraryClassifier.Classify(cal, PidMode.Mode22));
    }

    [Fact]
    public void Measurement_With_TempConversion_IsWaveform()
    {
        // SfECTI_T_EngCoolCvrtd - MEASUREMENT + "Sf" prefix + CM_T_DEG_Ca
        // (temperature conversion). All three push to Waveform.
        var ect = Entry(0x0005, size: 1, kind: "MEASUREMENT", name: "SfECTI_T_EngCoolCvrtd",
                        dataType: "SWORD", conv: "CM_T_DEG_Ca", lower: -256, upper: 256);
        Assert.Equal(ResponseKind.Waveform, PidLibraryClassifier.Classify(ect, PidMode.Mode22));
    }

    [Fact]
    public void Description_VinKeyword_OverridesMeasurementKind()
    {
        // Hypothetical 4-byte row tagged MEASUREMENT but whose description
        // mentions "VIN" - the keyword bumps fixed enough to win the tie-
        // breaking comparison even with the MEASUREMENT kind in play.
        // (Real example: 4-byte partial-VIN slices that some A2L exports
        // marked as MEASUREMENT.)
        var partial = Entry(0x1234, size: 4, kind: "MEASUREMENT",
                            name: "BaVINF_part", desc: "Partial VIN slice (chars 14-17)");
        Assert.Equal(ResponseKind.Fixed, PidLibraryClassifier.Classify(partial, PidMode.Mode22));
    }

    [Fact]
    public void Untagged_With_NoSignals_DefaultsToFixed()
    {
        // No kind, no recognised prefix, no recognised conversion. Tie at 0
        // resolves to Fixed (deterministic zero bytes are safer than
        // guessing waveform bounds for a row we know nothing about).
        var blank = Entry(0x9999, size: 2);
        Assert.Equal(ResponseKind.Fixed, PidLibraryClassifier.Classify(blank, PidMode.Mode22));
    }

    [Fact]
    public void Untagged_With_MeasurementPrefix_AndPercentConv_IsWaveform()
    {
        // No A2L kind (some library rows have it blank) but the name prefix
        // and conversion both signal sensor data. Should still classify as
        // Waveform despite missing the strongest single signal.
        var loadPct = Entry(0x0004, size: 1, name: "VeMAFC_Pct_EngLoadJ1979",
                            dataType: "FLOAT32_IEEE", conv: "CM_T_Pct_Percent");
        Assert.Equal(ResponseKind.Waveform, PidLibraryClassifier.Classify(loadPct, PidMode.Mode22));
    }
}
