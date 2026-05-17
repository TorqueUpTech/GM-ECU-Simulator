namespace Core.Dps;

// Signature-scan + table-walk for the GMW3110 $22 PID definition table embedded
// in an E38 ECM flash bin. Ported from tools/dps_utility_builder/extract_e38_pids.py
// (the canonical implementation, validated against real 2 MiB E38 readbacks).
//
// Record layout (8 bytes, big-endian):
//   byte 0: record type, one of {0x01, 0x02, 0x04, 0x07, 0x0D}
//   byte 1: 0x00
//   bytes 2-3: PID id (u16 BE), neither 0x0000 nor 0xFFFF
//   bytes 4-5: response size in bytes (u16 BE), 1..0x100
//   bytes 6-7: pointer-low (u16 BE)

public sealed record E38PidRecord(byte Type, ushort Pid, ushort Size, ushort PtrLo);

public static class E38PidExtractor
{
    public const int MinRun = 200;
    public const int RecordSize = 8;
    public const int MinSize = 1;
    public const int MaxSize = 0x100;

    private static readonly HashSet<byte> ValidTypeSet = new() { 0x01, 0x02, 0x04, 0x07, 0x0D };

    public static IReadOnlySet<byte> ValidTypes => ValidTypeSet;

    public static (IReadOnlyList<E38PidRecord> Records, int TableOffset) Extract(ReadOnlySpan<byte> bin)
    {
        int anchor = FindTableOffset(bin, out int longestRun);
        if (anchor < 0)
        {
            throw new InvalidDataException(
                $"PID table signature not found (longest valid run = {longestRun} records, need >= {MinRun}).");
        }

        var records = WalkTable(bin, anchor);
        return (records, anchor);
    }

    public static (IReadOnlyList<E38PidRecord> Records, int TableOffset) ExtractFile(string path)
    {
        byte[] data = File.ReadAllBytes(path);
        return Extract(data);
    }

    private static bool IsValidRecord(ReadOnlySpan<byte> data, int off)
    {
        if (off < 0 || off + RecordSize > data.Length)
        {
            return false;
        }
        if (data[off + 1] != 0x00)
        {
            return false;
        }
        if (!ValidTypeSet.Contains(data[off]))
        {
            return false;
        }
        int pid = (data[off + 2] << 8) | data[off + 3];
        if (pid == 0 || pid == 0xFFFF)
        {
            return false;
        }
        int sz = (data[off + 4] << 8) | data[off + 5];
        if (sz < MinSize || sz > MaxSize)
        {
            return false;
        }
        return true;
    }

    private static ushort ReadPid(ReadOnlySpan<byte> data, int off) =>
        (ushort)((data[off + 2] << 8) | data[off + 3]);

    // Returns the offset of any record inside the longest monotonically-PID-increasing
    // run of valid 8-byte records in the bin, or -1 if no run meets MinRun. The
    // caller then walks back to the head. Linear scan; once a run is found we skip
    // past the whole thing so the total work stays O(n).
    private static int FindTableOffset(ReadOnlySpan<byte> data, out int bestLen)
    {
        int bestStart = -1;
        bestLen = 0;
        int n = data.Length;
        int o = 0;
        while (o + RecordSize <= n)
        {
            if (!IsValidRecord(data, o))
            {
                o++;
                continue;
            }

            int runStart = o;
            ushort prevPid = ReadPid(data, o);
            int runLen = 1;
            int p = o + RecordSize;
            while (p + RecordSize <= n && IsValidRecord(data, p))
            {
                ushort pid = ReadPid(data, p);
                if (pid <= prevPid)
                {
                    break;
                }
                prevPid = pid;
                runLen++;
                p += RecordSize;
            }

            if (runLen > bestLen)
            {
                bestLen = runLen;
                bestStart = runStart;
            }

            // Skip past the discovered run so we don't re-scan every record inside it.
            o = runLen > 1 ? p : o + 1;
        }

        if (bestLen < MinRun || bestStart < 0)
        {
            return -1;
        }
        return bestStart;
    }

    // Walks backwards from any in-table offset to the head (first record whose
    // predecessor is either invalid or has a non-monotonic PID), then forward
    // emitting records until the run terminates.
    private static List<E38PidRecord> WalkTable(ReadOnlySpan<byte> data, int anchor)
    {
        int head = anchor;
        while (IsValidRecord(data, head - RecordSize))
        {
            ushort prevPid = ReadPid(data, head - RecordSize);
            ushort curPid = ReadPid(data, head);
            if (prevPid >= curPid)
            {
                break;
            }
            head -= RecordSize;
        }

        var result = new List<E38PidRecord>();
        int o = head;
        int prev = -1;
        while (IsValidRecord(data, o))
        {
            byte rt = data[o];
            ushort pid = ReadPid(data, o);
            ushort sz = (ushort)((data[o + 4] << 8) | data[o + 5]);
            ushort ptr = (ushort)((data[o + 6] << 8) | data[o + 7]);
            if (pid <= prev)
            {
                break;
            }
            prev = pid;
            result.Add(new E38PidRecord(rt, pid, sz, ptr));
            o += RecordSize;
        }
        return result;
    }
}
