namespace Common.Pids;

/// <summary>
/// One row from a PID library CSV (Mode $01, $1A, or $22). The library is
/// a reference catalogue the editor presents when the user is creating PIDs
/// on an ECU - it carries the A2L-derived metadata (name, description, size,
/// scaling) without binding to any specific ECU instance. The dispatcher
/// (Service01Handler / Service22Handler / etc.) does not consult this; it
/// only reads from <see cref="Ecu.EcuNode"/>'s own PID stores.
/// </summary>
/// <param name="Did">PID identifier. Mode $01 / $1A use the low 8 bits;
/// Mode $22 uses the full 16 bits.</param>
/// <param name="Size">Wire response length in bytes.</param>
/// <param name="Flag">Reserved per-PID flag from the source A2L (currently 0
/// for every entry; preserved for round-trip fidelity).</param>
/// <param name="A2lKind"><c>MEASUREMENT</c> (sensor reading) or
/// <c>CHARACTERISTIC</c> (calibration parameter), or empty when the A2L row
/// has no kind tag.</param>
/// <param name="FriendlyName">Optional display label. Empty when the source
/// CSV hasn't been hand-curated for this row.</param>
/// <param name="A2lName">The verbatim A2L identifier - the canonical
/// programmer-facing name, e.g. <c>VeEPSI_b_EngineRunning</c>.</param>
/// <param name="Description">Human-readable description sourced from the
/// A2L <c>LONG_IDENTIFIER</c> field.</param>
/// <param name="DataType">A2L datatype token, e.g. <c>UBYTE</c>, <c>SWORD</c>,
/// <c>FLOAT32_IEEE</c>.</param>
/// <param name="Lower">Lower physical bound, or <c>null</c> when absent.</param>
/// <param name="Upper">Upper physical bound, or <c>null</c> when absent.</param>
/// <param name="Conversion">A2L conversion identifier (e.g. <c>CM_T_DEG_Ca</c>).</param>
/// <param name="Unit">Engineering unit string.</param>
/// <param name="Slope">Linear-conversion slope (physical = slope * raw + offset),
/// or <c>null</c> when the source row has no numeric scaling.</param>
/// <param name="Offset">Linear-conversion offset, or <c>null</c>.</param>
public sealed record PidLibraryEntry(
    ushort Did,
    int    Size,
    int    Flag,
    string A2lKind,
    string FriendlyName,
    string A2lName,
    string Description,
    string DataType,
    double? Lower,
    double? Upper,
    string Conversion,
    string Unit,
    double? Slope,
    double? Offset);
