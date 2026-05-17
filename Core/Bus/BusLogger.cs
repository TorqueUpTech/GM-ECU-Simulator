using System.Globalization;

namespace Core.Bus;

// Single sink for every disk-bound log line in the simulator.
//
// Wraps a FileLogSink and owns the five-column CSV record format:
//   [HH:mm:ss.fff] , [TAG] , [ROLE] , <content...>
//
// Column 3 is the role/direction column. For CAN frames it carries the
// bracketed direction ([Rx] or [Tx]); for J2534 / SIM events it is left
// empty so column 4 is the actual content. This gives spreadsheet importers
// a stable column layout: line type and direction are both filterable, and
// frame payloads always start in column 4.
//
// Callers pick the source tag by choosing one of three typed methods
// (J2534 / CAN / SIM). Each method handles embedded '\n' in the input and
// emits one fully-prefixed physical line per segment - that property is why
// this class exists: multi-line StringBuilder payloads (e.g. PassThruWriteMsgs
// with its tx[i] detail lines) used to only get the prefix on their first
// physical line.
//
// Tag taxonomy:
//   J2534 - everything emitted from the Shim/ project: PassThru* IPC
//           narration (including tx[i]/rx[i] frame detail), pipe-server
//           lifecycle, periodic-message register/unregister, idle-drain
//           notices.
//   CAN   - per-frame Rx/Tx on the virtual bus, supplied by VirtualBus.LogFrame.
//           The csv argument already starts with "[Rx]," / "[Tx]," so the
//           role column comes through unchanged.
//   SIM   - everything else internal to the simulator: service-handler
//           decisions, security-module state, DPID scheduler diagnostics,
//           idle-supervisor events, capture-writer output, app lifecycle.
//
// Includes per-tag toggles (IncludeJ2534 / IncludeCan / IncludeSim) so the
// user's "Log menu" gates can suppress one stream without affecting the
// others. Default is all-on; MainWindow applies the persisted preferences.
public sealed class BusLogger : IDisposable
{
    private readonly FileLogSink sink = new();

    public bool IncludeJ2534 { get; set; } = true;
    public bool IncludeCan   { get; set; } = true;
    public bool IncludeSim   { get; set; } = true;

    public bool IsRunning => sink.IsRunning;
    public string? CurrentPath => sink.CurrentPath;
    public long BytesWritten => sink.BytesWritten;
    public long LinesWritten => sink.LinesWritten;

    public static string DefaultDirectory() => FileLogSink.DefaultDirectory();
    public static string DefaultPath() => FileLogSink.DefaultPath();

    public void Start(string path) => sink.Start(path);
    public void Stop() => sink.Stop();
    public void Dispose() => sink.Dispose();

    public void WriteJ2534(string line) { if (IncludeJ2534) WriteTagged("J2534", line, emptyRoleColumn: true); }
    public void WriteCan(string csv)    { if (IncludeCan)   WriteTagged("CAN",   csv,  emptyRoleColumn: false); }
    public void WriteSim(string line)   { if (IncludeSim)   WriteTagged("SIM",   line, emptyRoleColumn: true); }

    private void WriteTagged(string tag, string text, bool emptyRoleColumn)
    {
        if (!sink.IsRunning) return;
        // Capture a single timestamp for the whole logical record. Sub-lines
        // produced by a multi-line builder (tx[0], tx[1], rx[0]) describe one
        // PassThru call and should share the same timestamp - reading them
        // back as separate timestamps would falsely suggest gaps between
        // frames in a batched call.
        var stamp = DateTime.Now.ToString("HH:mm:ss.fff", CultureInfo.InvariantCulture);
        // J2534 / SIM events have no inherent role/direction, so column 3 is
        // an empty field (",,"). CAN frames already carry their bracketed
        // direction at the start of the csv payload, so a single comma is
        // enough - it lines up as the column-3 separator.
        var prefix = emptyRoleColumn
            ? $"[{stamp}],[{tag}],,"
            : $"[{stamp}],[{tag}],";
        foreach (var segment in text.Split('\n'))
        {
            // Trim trailing '\r' so CRLF builders don't leave dangling carriage
            // returns mid-line in the CSV.
            var trimmed = segment.Length > 0 && segment[^1] == '\r'
                ? segment[..^1]
                : segment;
            sink.Write($"{prefix}{trimmed}");
        }
    }
}
