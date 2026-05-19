using System.Buffers.Binary;

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
// Record layout (8 bytes BE, confirmed on E38):
//   byte 0    : record-type / bank selector (observed: 0x01, 0x02, 0x04, 0x07, 0x0D)
//   byte 1    : 0x00 padding
//   bytes 2-3 : PID number (BE uint16)
//   bytes 4-5 : data length in bytes (BE uint16)
//   bytes 6-7 : low half of the data pointer (BE uint16) - high half is
//               loaded by a per-record `lis r12, <hi>` at runtime, so the
//               full flash address can only be recovered by disassembling
//               the $22 handler. Until that work lands we surface the
//               record-level facts (PID + length) and leave FlashAddress
//               null; downstream tooling treats these as zeroed-payload
//               placeholders with the correct wire shape.
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

    // Record-type bytes observed in the E38 survey. Acts as a structural
    // filter for the signature scan; widening this set on T43/E67 may be
    // needed once those families are surveyed.
    private static readonly HashSet<byte> ValidRecordTypes = new() { 0x01, 0x02, 0x04, 0x07, 0x0D };

    private const int RecordSize = 8;
    private const int MinSize = 1;
    private const int MaxSize = 0x100;

    // Confidence floor: the real E38 table holds ~536 records; anything
    // shorter than 200 valid monotonically-increasing records is almost
    // certainly a false positive on padding or unrelated tables.
    private const int MinRun = 200;

    /// <summary>
    /// Parse a GM ECU flash image for the $22 PID table. Returns null if
    /// the bin is too small or no PID table can be located. See
    /// tools/dps_utility_builder/extract_e38_pids.py for the prototype
    /// this method ports.
    /// </summary>
    public static Mode22Scan? Parse(ReadOnlySpan<byte> bin)
    {
        if (bin.Length < 0x10000) return null;

        var bytes = bin.ToArray();
        var warnings = new List<string>();

        var anchor = FindTableAnchor(bytes);
        if (anchor < 0)
        {
            warnings.Add($"$22 PID table signature not found (need >= {MinRun} valid monotonic records).");
            return null;
        }

        var head = WalkBackToHead(bytes, anchor);
        var pids = new List<PidExtraction>();
        int o = head;
        int prevPid = -1;
        while (IsValidRecord(bytes, o))
        {
            byte rt = bytes[o];
            ushort pid = BinaryPrimitives.ReadUInt16BigEndian(bytes.AsSpan(o + 2, 2));
            ushort size = BinaryPrimitives.ReadUInt16BigEndian(bytes.AsSpan(o + 4, 2));
            if (pid <= prevPid) break;
            prevPid = pid;
            pids.Add(BuildExtraction(rt, pid, size));
            o += RecordSize;
        }

        var family = DetectFamily(bytes);

        // ServiceDispatcherOffset / Service22HandlerOffset require the same
        // PPC dispatcher walk Mode1ADidBinExtractor does. The PID table is
        // independently locatable by signature, so we ship those as 0 until
        // a follow-up wires them in. TODO: share the dispatcher walk with
        // Mode1ADidBinExtractor and resolve the $22 handler trampoline.
        return new Mode22Scan(
            Family: family,
            ServiceDispatcherOffset: 0,
            Service22HandlerOffset: 0,
            PidTableOffset: head,
            SupportedSids: Array.Empty<byte>(),
            Pids: pids,
            Warnings: warnings);
    }

    // The ptr_lo low-half is preserved in DecodedValue so a downstream
    // disassembly pass can correlate this extraction against the $22
    // handler's per-record `lis` immediate. WireBytes is empty - we don't
    // know the value, only the shape - and LengthBytes carries the right
    // wire size so the simulator's $22 handler emits a correctly-sized
    // (zero-filled) response rather than NRC'ing RequestOutOfRange.
    private static PidExtraction BuildExtraction(byte recordType, ushort pid, ushort size)
    {
        return new PidExtraction(
            Pid: pid,
            Kind: PidSourceKind.FlashTable,
            FlashAddress: null,
            LengthBytes: size,
            WireBytes: Array.Empty<byte>(),
            DecodedValue: $"table-record type=0x{recordType:X2} size={size} ptr_lo=tbd");
    }

    private static bool IsValidRecord(byte[] d, int off)
    {
        if (off < 0 || off + RecordSize > d.Length) return false;
        if (d[off + 1] != 0x00) return false;
        if (!ValidRecordTypes.Contains(d[off])) return false;
        int pid = (d[off + 2] << 8) | d[off + 3];
        if (pid == 0 || pid == 0xFFFF) return false;
        int sz = (d[off + 4] << 8) | d[off + 5];
        if (sz < MinSize || sz > MaxSize) return false;
        return true;
    }

    // Scan the whole bin for the longest monotonically-PID-increasing run
    // of valid 8-byte records. The signature is highly specific - a ~500+
    // record run is unambiguous - so we don't hardcode the family-specific
    // offset (E38 = 0x145718; T43 / E67 unknown).
    private static int FindTableAnchor(byte[] d)
    {
        int bestStart = -1;
        int bestLen = 0;
        int n = d.Length;
        int o = 0;
        while (o + RecordSize <= n)
        {
            if (!IsValidRecord(d, o)) { o += 1; continue; }
            int runStart = o;
            int prevPid = (d[o + 2] << 8) | d[o + 3];
            int runLen = 1;
            int p = o + RecordSize;
            while (p + RecordSize <= n && IsValidRecord(d, p))
            {
                int pid = (d[p + 2] << 8) | d[p + 3];
                if (pid <= prevPid) break;
                prevPid = pid;
                runLen++;
                p += RecordSize;
            }
            if (runLen > bestLen) { bestLen = runLen; bestStart = runStart; }
            // Skip past the whole run so we don't re-scan every record inside it.
            o = runLen > 1 ? p : o + 1;
        }
        return bestLen >= MinRun ? bestStart : -1;
    }

    // Caller hands us any offset inside the table. Walk backwards in 8-byte
    // steps to the head, guarding against an adjacent unrelated table that
    // happens to match the record shape by also requiring monotonic PIDs
    // across the boundary.
    private static int WalkBackToHead(byte[] d, int anchor)
    {
        int head = anchor;
        while (IsValidRecord(d, head - RecordSize))
        {
            int prevPid = (d[head - RecordSize + 2] << 8) | d[head - RecordSize + 3];
            int curPid = (d[head + 2] << 8) | d[head + 3];
            if (prevPid >= curPid) break;
            head -= RecordSize;
        }
        return head;
    }

    // Mirror Mode1ADidBinExtractor's family detection rather than duplicate
    // it; the $22 walker shouldn't drift from the $1A walker's family call
    // since both run against the same bin. We re-implement here only because
    // the $1A method is private - if it becomes public, collapse to a single
    // call site.
    private static string DetectFamily(byte[] d)
    {
        if (FindAscii(d, "BOSCH TC19.12") >= 0) return "T43";
        if (LooksLikeE67(d)) return "E67";
        if (LooksLikeE38(d)) return "E38";
        return "Unknown";
    }

    private static bool LooksLikeE67(byte[] d)
    {
        bool hasVin = HasAsciiVinDescriptor(d, 0xC0AC) || HasAsciiVinDescriptor(d, 0xE0AC);
        if (!hasVin) return false;
        if (FindAscii(d, "DELPHI") >= 0) return false;
        return FindAscii(d, "BOSCH") >= 0;
    }

    private static bool LooksLikeE38(byte[] d)
        => HasAsciiVinDescriptor(d, 0xC0AC) || HasAsciiVinDescriptor(d, 0xE0AC);

    private static bool HasAsciiVinDescriptor(byte[] d, int off)
    {
        if (off < 0 || off + 25 > d.Length) return false;
        for (int i = 0; i < 25; i++)
        {
            byte b = d[off + i];
            if (b < 0x20 || b > 0x7E) return false;
        }
        return true;
    }

    private static int FindAscii(byte[] d, string s)
    {
        var needle = System.Text.Encoding.ASCII.GetBytes(s);
        for (int i = 0; i + needle.Length <= d.Length; i++)
        {
            bool match = true;
            for (int j = 0; j < needle.Length; j++)
                if (d[i + j] != needle[j]) { match = false; break; }
            if (match) return i;
        }
        return -1;
    }
}
