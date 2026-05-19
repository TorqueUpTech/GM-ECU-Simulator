namespace Core.Identification;

// Placeholder for $22 ReadDataByIdentifier PID extraction. $22 is the UDS
// (ISO 14229) identifier-read service with 2-byte PIDs - the wider-
// namespace successor to GMW3110's $1A. Real GM ECUs implement both
// services side-by-side: $1A is served by the dispatcher cmpwi/beq chain
// that Mode1ADidBinExtractor walks, $22 is served by a static flash table
// the $22 handler binary-searches. The simulator spans both spec spaces;
// this file is the UDS-side counterpart to Mode1ADidBinExtractor.
//
// Shape mirrors Mode1ADidBinExtractor so downstream code (PrimedDataset,
// PrimeReportWriter, the wizard's Phase 3 grid) can wire one walker per
// service against uniform result types.
//
// Two structural differences from $1A drive the result shape:
//   1. PIDs are 2-byte (ushort), not 1-byte. The UDS namespace is wider
//      than GMW3110's by design.
//   2. The handler binary-searches a fixed flash table of 8-byte records
//      (PID, length, fetcher pointer, ...) rather than walking a cmpwi/
//      beq chain. On E38 the table lives at 0x145718; T43 / E67 haven't
//      been surveyed yet. The Python prototype that does the same scan
//      lives at tools/dps_utility_builder/extract_e38_pids.py.
//
// Implementation is intentionally a stub - Parse returns null. Fill in
// when the $22 walker is wired up.
public static class Mode22DidBinExtractor
{
    public enum PidSourceKind
    {
        FlashTable,       // PID record sits in the static $22 dispatch table
        InlineConstant,   // fetcher returns a hardcoded literal
        RuntimeComputed,  // fetcher returns a chip-RAM address populated at boot
        SegmentDerived,   // value recovered via segment markers, not the dispatch path
        Unknown,
    }

    public sealed record PidExtraction(
        ushort Pid,
        PidSourceKind Kind,
        int? FlashAddress,
        int LengthBytes,
        byte[] WireBytes,
        string DecodedValue);

    public sealed record Mode22Scan(
        string Family,
        int ServiceDispatcherOffset,
        int Service22HandlerOffset,
        int PidTableOffset,
        IReadOnlyList<byte> SupportedSids,
        IReadOnlyList<PidExtraction> Pids,
        IReadOnlyList<string> Warnings)
    {
        public PidExtraction? FindPid(ushort pid) => Pids.FirstOrDefault(x => x.Pid == pid);
    }

    // Parse a GM ECU flash image for the $22 PID table. Stub: currently
    // returns null. See tools/dps_utility_builder/extract_e38_pids.py for
    // the table-scan prototype that this method should port.
    public static Mode22Scan? Parse(ReadOnlySpan<byte> bin)
    {
        _ = bin;
        return null;
    }
}
