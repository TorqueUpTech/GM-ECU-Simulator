namespace Core.Identification.UpXml;

// Parsed representation of autodetect.xml rules. The rules are a flat
// list of (target-xml, group, grouplogic, address, data, compare)
// records; semantics:
//
//   - Rules with the same target XML form a ruleset for that platform.
//   - Within a ruleset, rules with the same <group> integer form a
//     group. All rules in a group share the same <grouplogic>.
//   - "And" group: every rule in the group must match.
//   - "Or"  group: any rule in the group must match.
//   - Across groups: every group must match (i.e. groups are AND-ed).
//   - A platform matches if its complete ruleset evaluates true.
//
// Address grammar (only the three forms actually present in the
// vendored autodetect.xml are accepted):
//
//   filesize       -> compare against the bin's total length
//   HHHH:LEN       -> read LEN bytes (1..8) big-endian at literal hex
//                     file offset HHHH; LEN=1/2/4/8 most common.
//   HHHH@:LEN      -> read LEN bytes BE at (bin_length - HHHH).
//                     "8@:8" = the last eight bytes of the file.
//   HHHH@          -> same as above with LEN inferred from context;
//                     observed once in the vendored file, treated as
//                     LEN=2.
//
// The <data> element is a decimal integer (one rule uses
// 12273828952035819519 = 0xAA559966AA5599FF, a u64 marker), so values
// up to ulong.MaxValue must be representable.

public enum UpDetectCompare { Eq, NotEq, Lt, Gt }

public enum UpGroupLogic { And, Or }

public abstract record UpDetectAddress
{
    /// <summary>"filesize" - special, value is bin.Length.</summary>
    public sealed record Filesize : UpDetectAddress;

    /// <summary>"HHHH:LEN" - read LEN bytes BE at literal offset HHHH.</summary>
    public sealed record LiteralForward(int Offset, int Length) : UpDetectAddress;

    /// <summary>"HHHH@:LEN" - read LEN bytes BE at (binLength - HHHH).
    /// "8@:8" = last 8 bytes.</summary>
    public sealed record LiteralFromEnd(int OffsetFromEnd, int Length) : UpDetectAddress;
}

public sealed record UpDetectRule(
    string Xml,            // target platform XML filename (case preserved as written)
    int Group,
    UpGroupLogic Logic,
    UpDetectAddress Address,
    ulong Data,
    UpDetectCompare Compare);
