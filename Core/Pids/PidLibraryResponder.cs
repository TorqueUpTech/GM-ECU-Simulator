using Common.Pids;
using Common.Protocol;
using Common.Waveforms;
using Core.Ecu;

namespace Core.Pids;

/// <summary>
/// Always-on fallback PID store backed by the embedded
/// <see cref="PidLibrary"/>. When a handler can't find a requested DID in
/// the ECU's user-curated per-mode store, it consults the responder; if
/// the library knows the DID the ECU answers with a plausible payload
/// rather than NRC $31 RequestOutOfRange.
/// </summary>
/// <remarks>
/// Gated per-ECU by <see cref="EcuNode.AutoRespondFromLibrary"/> so test
/// fixtures (which assume an unconfigured DID NRCs) stay green and live
/// ECUs (which flip the flag on via the construction paths) get the
/// always-on behaviour.
///
/// Response shape per library row is decided by
/// <see cref="PidLibraryClassifier"/>:
/// <list type="bullet">
///   <item><c>Fixed</c>: synthetic Pid carries a pre-allocated zero buffer
///         in <see cref="Pid.StaticBytes"/>. <see cref="Pid.WriteResponseBytes"/>
///         copies it verbatim - allocation-free per request.</item>
///   <item><c>Waveform</c>: synthetic Pid carries a
///         <see cref="WaveformConfig"/> sized from the library entry's
///         <c>lower</c>/<c>upper</c> bounds (with a sanity guard for FLOAT32
///         entries whose bounds span the entire float range). The waveform
///         generators are stateless functions of <c>timeMs</c>, so one cached
///         Pid is safe to share across every ECU lookup.</item>
/// </list>
/// Mode1A is special-cased: it consults <see cref="DefaultDidValues"/>
/// first so the library-fallback path produces the same placeholder strings
/// the editor's "Auto-populate DIDs" command writes (VIN, supplier IDs,
/// cal IDs, alpha codes). DIDs the defaults table doesn't know fall through
/// to zero-filled at the library-declared length.
/// </remarks>
public static class PidLibraryResponder
{
    private static readonly Lazy<IReadOnlyDictionary<ushort, Pid>> mode22 =
        new(() => Build(PidLibrary.Mode22, PidMode.Mode22));

    /// <summary>Returns the cached synthetic $22 Pid for <paramref name="wireId"/>,
    /// or null when the library does not know that PID.</summary>
    public static Pid? GetMode22(ushort wireId)
        => mode22.Value.TryGetValue(wireId, out var p) ? p : null;

    /// <summary>Returns the payload for $1A DID <paramref name="did"/>, or null
    /// when the library does not know that DID. Prefers a placeholder from
    /// <see cref="DefaultDidValues"/> when one exists (so the library fallback
    /// stays in lockstep with Auto-populate's output); otherwise zero-fills to
    /// the library-declared length.</summary>
    public static byte[]? GetMode1AIdentifier(byte did)
    {
        if (!PidLibrary.Mode1A.TryGetValue(did, out var entry)) return null;
        var canned = DefaultDidValues.Get(did);
        // DefaultDidValues bytes are used verbatim - they were authored at the
        // length real testers expect on the wire and may legitimately differ
        // from the library's nominal Size (e.g. $99 programming date is 8 ASCII
        // bytes regardless of the library row's declared size).
        return canned ?? new byte[entry.Size];
    }

    private static Dictionary<ushort, Pid> Build(
        IReadOnlyDictionary<ushort, PidLibraryEntry> library, PidMode mode)
    {
        var d = new Dictionary<ushort, Pid>(library.Count);
        foreach (var (id, e) in library)
        {
            var (size, length) = e.Size switch
            {
                1 => (PidSize.Byte, null),
                2 => (PidSize.Word, null),
                4 => (PidSize.DWord, null),
                _ => (PidSize.DWord, (int?)e.Size),
            };
            var pid = new Pid
            {
                Mode        = mode,
                Address     = id,
                Name        = !string.IsNullOrWhiteSpace(e.FriendlyName) ? e.FriendlyName
                            : !string.IsNullOrWhiteSpace(e.A2lName)      ? e.A2lName
                            : $"PID 0x{e.Did:X4}",
                Size        = size,
                LengthBytes = length,
                DataType    = MapDataType(e.DataType),
                Scalar      = e.Slope  ?? 1.0,
                Offset      = e.Offset ?? 0.0,
                Unit        = (e.Unit ?? "").TrimEnd(),
            };

            switch (PidLibraryClassifier.Classify(e, mode))
            {
                case ResponseKind.Fixed:
                    // Pre-allocated zero buffer at the library's declared wire
                    // length so a 17-byte PID like 0x155B gets 17 bytes back.
                    // Hand-curated overrides for specific PIDs can be added
                    // alongside DefaultDidValues if needed - the editor picker
                    // also lets the user override per-row at runtime.
                    pid.StaticBytes = new byte[e.Size];
                    break;

                case ResponseKind.Waveform:
                    pid.WaveformConfig = BuildWaveformConfig(e);
                    break;
            }
            d[id] = pid;
        }
        return d;
    }

    // Linear-bounded sin centered on the library's (lower, upper) midpoint
    // with ~20% bound margin. FLOAT32_IEEE rows whose A2L bounds span the
    // entire float range, or any row missing valid bounds, fall back to a
    // gentle ±1 oscillation around zero - the user can tighten it via the
    // editor's per-row waveform inspector. Frequency follows the project
    // default of 0.2 Hz (visible motion against a scan tool without
    // strobing).
    private static WaveformConfig BuildWaveformConfig(PidLibraryEntry e)
    {
        double lower = e.Lower ?? 0.0;
        double upper = e.Upper ?? 0.0;
        double amplitude;
        double offset;
        double range = upper - lower;
        if (double.IsFinite(lower) && double.IsFinite(upper) && range > 0 && range < 1e6)
        {
            offset    = (lower + upper) / 2.0;
            amplitude = range / 2.5;     // 20% headroom off each bound
        }
        else
        {
            offset    = 0.0;
            amplitude = 1.0;
        }

        return new WaveformConfig
        {
            Shape       = WaveformShape.Sin,
            Amplitude   = amplitude,
            Offset      = offset,
            FrequencyHz = 0.2,
        };
    }

    private static PidDataType MapDataType(string? a2lDataType) => a2lDataType?.ToUpperInvariant() switch
    {
        "SBYTE" or "SWORD" or "SLONG" or "INT32" => PidDataType.Signed,
        _ => PidDataType.Unsigned,
    };
}
