namespace Common.Protocol;

// Catalogue of preconfigured identifiers the PID editor offers in the
// Identifier dropdown for $1A and $22 mode rows. Picking an entry locks
// the row's Size / Type / Scalar / Offset / Unit to the spec-defined
// shape so the user can't accidentally edit them into something the
// real ECU wouldn't return.
//
// $2D mode rows do NOT pull from this catalogue - the user types a memory
// address freehand and edits every other field by hand, because $2D is the
// "custom dynamic PID" case.
//
// PLACEHOLDER DATA: the $1A list is derived from GMW3110-2010 §8.3.2
// Table 25 via Gmw3110DidNames - reasonably complete. The $22 list is
// a hand-picked stub of the half-dozen most commonly-asked-for engine
// PIDs across GM Global A / Global B; the real per-family catalogue
// will land later once Mode22DidBinExtractor.Parse can survey a bin and
// emit a structured PID list. Keep entries sorted by Identifier so the
// dropdown is predictable.
public sealed record PidCatalogueEntry(
    PidMode Mode,
    uint Identifier,
    string Name,
    PidSize Size,
    PidDataType DataType,
    double Scalar,
    double Offset,
    string Unit)
{
    // Display string the ComboBox renders: "$90 - VIN" / "0x000C - Engine RPM".
    public string Display => Mode switch
    {
        PidMode.Mode1A => $"${Identifier:X2} - {Name}",
        PidMode.Mode22 => $"0x{Identifier:X4} - {Name}",
        PidMode.Mode1  => $"${Identifier:X2} - {Name}",
        _              => Name,
    };
}

public static class PidCatalogue
{
    // $1A DID catalogue - one entry per Gmw3110DidNames.KnownDids member.
    // Per-DID size is mostly unknown without a bin survey, so defaulted to
    // Byte / Unsigned / 1.0 / 0.0 / "". The user can change mode to $2D if
    // they need a custom shape; otherwise picking the entry just stamps in
    // these placeholders.
    public static readonly IReadOnlyList<PidCatalogueEntry> Mode1A = BuildMode1A();

    private static PidCatalogueEntry[] BuildMode1A()
    {
        var list = new List<PidCatalogueEntry>(Gmw3110DidNames.KnownDids.Length);
        foreach (var did in Gmw3110DidNames.KnownDids)
        {
            list.Add(new PidCatalogueEntry(
                Mode: PidMode.Mode1A,
                Identifier: did,
                Name: Gmw3110DidNames.NameOf(did) ?? $"DID {did:X2}",
                Size: PidSize.Byte,
                DataType: PidDataType.Unsigned,
                Scalar: 1.0, Offset: 0.0, Unit: ""));
        }
        return list.ToArray();
    }

    // $22 PID catalogue - STUB. A handful of well-known engine PIDs so the
    // dropdown has something to show during UX iteration. Real per-family
    // catalogues will replace this when Mode22DidBinExtractor lands; the
    // entries below are not authoritative and the scaling figures are
    // best-effort placeholders.
    public static readonly IReadOnlyList<PidCatalogueEntry> Mode22 = new[]
    {
        new PidCatalogueEntry(PidMode.Mode22, 0x0005, "Coolant Temperature",     PidSize.Byte,  PidDataType.Unsigned, 1.0,         -40.0, "deg C"),
        new PidCatalogueEntry(PidMode.Mode22, 0x000B, "Intake Manifold Pressure",PidSize.Byte,  PidDataType.Unsigned, 1.0,           0.0, "kPa"),
        new PidCatalogueEntry(PidMode.Mode22, 0x000C, "Engine RPM",              PidSize.Word,  PidDataType.Unsigned, 0.25,          0.0, "rpm"),
        new PidCatalogueEntry(PidMode.Mode22, 0x000D, "Vehicle Speed",           PidSize.Byte,  PidDataType.Unsigned, 1.0,           0.0, "km/h"),
        new PidCatalogueEntry(PidMode.Mode22, 0x000F, "Intake Air Temperature",  PidSize.Byte,  PidDataType.Unsigned, 1.0,         -40.0, "deg C"),
        new PidCatalogueEntry(PidMode.Mode22, 0x0011, "Throttle Position",       PidSize.Byte,  PidDataType.Unsigned, 100.0/255.0,   0.0, "%"),
        new PidCatalogueEntry(PidMode.Mode22, 0x0042, "Control Module Voltage",  PidSize.Word,  PidDataType.Unsigned, 0.001,         0.0, "V"),
        new PidCatalogueEntry(PidMode.Mode22, 0x1940, "Fuel Rail Pressure",      PidSize.Word,  PidDataType.Unsigned, 0.1,           0.0, "kPa"),
    };

    // OBD-II Service $01 (ShowCurrentData) PID catalogue. Stub of the most
    // commonly-asked-for engine PIDs per SAE J1979. Scaling figures follow
    // the public spec so values look plausible against a real scan tool.
    // Real per-vehicle subsets vary; this stub gives the editor dropdown
    // something to offer until the proper Mode $01 service handler lands.
    public static readonly IReadOnlyList<PidCatalogueEntry> Mode1 = new[]
    {
        new PidCatalogueEntry(PidMode.Mode1, 0x04, "Calculated Engine Load",   PidSize.Byte, PidDataType.Unsigned, 100.0/255.0,   0.0, "%"),
        new PidCatalogueEntry(PidMode.Mode1, 0x05, "Engine Coolant Temp",      PidSize.Byte, PidDataType.Unsigned, 1.0,         -40.0, "deg C"),
        new PidCatalogueEntry(PidMode.Mode1, 0x0B, "Intake Manifold Pressure", PidSize.Byte, PidDataType.Unsigned, 1.0,           0.0, "kPa"),
        new PidCatalogueEntry(PidMode.Mode1, 0x0C, "Engine RPM",               PidSize.Word, PidDataType.Unsigned, 0.25,          0.0, "rpm"),
        new PidCatalogueEntry(PidMode.Mode1, 0x0D, "Vehicle Speed",            PidSize.Byte, PidDataType.Unsigned, 1.0,           0.0, "km/h"),
        new PidCatalogueEntry(PidMode.Mode1, 0x0F, "Intake Air Temperature",   PidSize.Byte, PidDataType.Unsigned, 1.0,         -40.0, "deg C"),
        new PidCatalogueEntry(PidMode.Mode1, 0x10, "MAF Air Flow Rate",        PidSize.Word, PidDataType.Unsigned, 0.01,          0.0, "g/s"),
        new PidCatalogueEntry(PidMode.Mode1, 0x11, "Throttle Position",        PidSize.Byte, PidDataType.Unsigned, 100.0/255.0,   0.0, "%"),
        new PidCatalogueEntry(PidMode.Mode1, 0x1F, "Run Time Since Engine Start", PidSize.Word, PidDataType.Unsigned, 1.0,        0.0, "s"),
        new PidCatalogueEntry(PidMode.Mode1, 0x2F, "Fuel Tank Level",          PidSize.Byte, PidDataType.Unsigned, 100.0/255.0,   0.0, "%"),
        new PidCatalogueEntry(PidMode.Mode1, 0x46, "Ambient Air Temperature",  PidSize.Byte, PidDataType.Unsigned, 1.0,         -40.0, "deg C"),
        new PidCatalogueEntry(PidMode.Mode1, 0x5C, "Engine Oil Temperature",   PidSize.Byte, PidDataType.Unsigned, 1.0,         -40.0, "deg C"),
    };

    /// <summary>Returns the catalogue list appropriate for <paramref name="mode"/>;
    /// empty for $2D (which is hand-rolled).</summary>
    public static IReadOnlyList<PidCatalogueEntry> For(PidMode mode) => mode switch
    {
        PidMode.Mode1A => Mode1A,
        PidMode.Mode22 => Mode22,
        PidMode.Mode1  => Mode1,
        _              => Array.Empty<PidCatalogueEntry>(),
    };
}
