using Common.Pids;
using Common.Protocol;

namespace Core.Pids;

/// <summary>
/// Tag assigned to a library entry by <see cref="PidLibraryClassifier"/>:
/// <see cref="Fixed"/> entries respond with a static byte array (typically
/// zero-filled or a hand-curated value); <see cref="Waveform"/> entries
/// respond with a live synthetic sample so a scan tool sees motion.
/// </summary>
internal enum ResponseKind { Fixed, Waveform }

/// <summary>
/// Decides whether a <see cref="PidLibraryEntry"/> should be served as a
/// fixed byte array or as a live waveform sample, based on the A2L-derived
/// metadata. Used by <see cref="PidLibraryResponder"/> when building the
/// auto-response cache so a fresh ECU produces plausible-looking traffic
/// against a scan tool without hand-curating 600+ rows.
///
/// Decision cascade (in priority order):
/// <list type="number">
///   <item>Size &gt; 4 bytes is always <see cref="ResponseKind.Fixed"/> -
///         a scalar waveform can only fill 1/2/4 bytes; wider payloads
///         are VIN-style arrays or buffers.</item>
///   <item>Mode1A is always <see cref="ResponseKind.Fixed"/> - GMW3110
///         identifiers are stored configuration, not live data.</item>
///   <item>Otherwise: weighted score across <c>a2l_kind</c>, datatype,
///         conversion identifier, name prefix (GM Hungarian convention),
///         and description keywords. <c>Fixed</c> wins ties.</item>
/// </list>
/// </summary>
internal static class PidLibraryClassifier
{
    // GM Hungarian-ish two-character name prefixes that strongly suggest a
    // calibration / stored / configuration value (i.e. fixed). Not exhaustive
    // - the convention isn't formally documented in any public source - but
    // covers the dominant cases observed across the A2L exports.
    private static readonly HashSet<string> CalibrationNamePrefixes = new(StringComparer.Ordinal)
    {
        "Ke", "Ka", "Kc", "Kf",  // calibration constant / array / counter / flag
        "Ba", "Be",              // buffer (VIN, history)
        "Na",                    // NVRAM array
    };

    // GM Hungarian-ish two-character prefixes that suggest a live / volatile
    // measurement (waveform candidate).
    private static readonly HashSet<string> MeasurementNamePrefixes = new(StringComparer.Ordinal)
    {
        "Ve", "Va", "Vf", "Vp", "Vh", "Vk",  // volatile (sensor / computed)
        "Sf", "Sa", "Sb",                    // sensor: filtered / array / boolean-state
        "Pa", "Pe", "Pf",                    // periodic
        "De",                                // diagnostic element (often live)
    };

    // A2L conversion identifiers that imply "raw byte container" (no unit-
    // bearing scaling), which suggests a stored value rather than a sensor.
    private static readonly HashSet<string> OpaqueConversions = new(StringComparer.Ordinal)
    {
        "CM_BYTE", "CM_LONGWORD", "CM_T_COUNT_UB_00", "CM_T_COUNT_WORD",
    };

    // Conversion-name prefixes for typical engineering units; strong signal
    // for a sensor reading and therefore a waveform candidate.
    private static readonly string[] MeasurementConversionPrefixes =
    {
        "CM_T_DEG_",   // temperatures
        "CM_T_KPA",    // pressures
        "CM_T_RPM",    // engine speed
        "CM_T_VOLT",   // voltages
        "CM_T_ANGLE",  // angles
        "CM_T_PCT_",   // percentages (upper)
        "CM_T_Pct_",   // percentages (lower)
        "CM_T_dm_",    // mass flow
    };

    // Lowercase substrings in the description that strongly imply a stored
    // identifier or calibration value. First-match-wins for scoring.
    private static readonly string[] FixedKeywords =
    {
        "vin", "serial", "part number", "calibration id", "calibration part",
        "history", "buffer", "stored", "nvram", "alpha code",
    };

    public static ResponseKind Classify(PidLibraryEntry entry, PidMode mode)
    {
        // Hard constraint: a scalar waveform can only fill 1/2/4 bytes.
        // Anything wider (VIN at 17, cal history at 92) is always fixed.
        if (entry.Size > 4) return ResponseKind.Fixed;

        // Mode1A is the identifier namespace by spec - GMW3110 §8.3.2.
        if (mode == PidMode.Mode1A) return ResponseKind.Fixed;

        int fixedScore    = 0;
        int waveformScore = 0;

        if (string.Equals(entry.A2lKind, "CHARACTERISTIC", StringComparison.OrdinalIgnoreCase))
            fixedScore += 10;
        else if (string.Equals(entry.A2lKind, "MEASUREMENT", StringComparison.OrdinalIgnoreCase))
            waveformScore += 8;

        if (string.Equals(entry.DataType, "FLOAT32_IEEE", StringComparison.OrdinalIgnoreCase))
            waveformScore += 3;

        if (entry.Conversion is { Length: > 0 } conv)
        {
            if (OpaqueConversions.Contains(conv)) fixedScore += 3;
            foreach (var p in MeasurementConversionPrefixes)
                if (conv.StartsWith(p, StringComparison.Ordinal)) { waveformScore += 3; break; }
        }

        if (entry.A2lName is { Length: >= 2 } name)
        {
            var prefix = name.Substring(0, 2);
            if (CalibrationNamePrefixes.Contains(prefix)) fixedScore    += 5;
            if (MeasurementNamePrefixes.Contains(prefix)) waveformScore += 3;
        }

        if (entry.Description is { Length: > 0 } desc)
        {
            var lower = desc.ToLowerInvariant();
            foreach (var kw in FixedKeywords)
                if (lower.Contains(kw)) { fixedScore += 4; break; }
        }

        // Fixed wins ties: when nothing classifies, deterministic zero bytes
        // are safer than a guess at meaningful waveform bounds.
        return fixedScore >= waveformScore ? ResponseKind.Fixed : ResponseKind.Waveform;
    }
}
