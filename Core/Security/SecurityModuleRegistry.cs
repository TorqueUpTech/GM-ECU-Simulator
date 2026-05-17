using Core.Security.Algorithms;
using Core.Security.Modules;

namespace Core.Security;

// String-ID → factory map for ISecurityAccessModule instances. Per-ECU
// instances (each EcuNode owns its own), so each module gets independent
// bookkeeping. Built-ins register in the static ctor; a future DLL loader
// can call Register the same way without touching anything else.
public static class SecurityModuleRegistry
{
    private static readonly Dictionary<string, Func<ISecurityAccessModule>> Factories = new();

    static SecurityModuleRegistry()
    {
        Register("gmw3110-2010-not-implemented",
            () => new Gmw3110_2010_Generic(new NotImplementedAlgorithm(),
                                           id: "gmw3110-2010-not-implemented"));
        // STRICT 5-byte SecurityAccess for DPS-recognised E92-family ECMs.
        // Computes the exact key DPS would compute and rejects anything else,
        // using the algorithm reverse-engineered from sa015bcr.dll on
        // 2026-05-17 and the password captured via tools/sa015bcr_hook/.
        // The algorithm is per-algoId; the password we ship defaults to the
        // E92/Algo 92 value. Override via SecurityModuleConfig if you have a
        // different captured password (e.g. for another DPS-supported 5-byte
        // algoId).
        //
        // (The earlier 2-byte open-source "Algo 92" implementation
        // [E38Algorithm], based on jakka351 / pcmhacking community sources,
        // was empirically wrong: the algorithm DPS actually labels "Algo 92"
        // is the 5-byte cipher implemented in sale.dll + sa015bcr.dll.
        // E38Algorithm is retained as a class for its own direct tests but
        // no longer holds the gm-algo-92 registry slot.)
        Register("gm-algo-92",
            () => new Gmw3110_2010_Generic(new Gm5ByteAlgorithm(),
                                           id: "gm-algo-92"));
        // Community-sourced 2-byte "GMLAN 0x92" cipher for E38 / E67 ECMs:
        //   k = ~(byteSwap(seed) + 0x7D58) + 0x8001
        // Used by non-DPS testers (HP Tuners, EFILive, jakka351 tooling) when
        // talking to an E38 over an ExtendedDiagnosticSession ($10 03). Not the
        // same cipher DPS uses for its 5-byte Algo 92 - see gm-algo-92 above
        // for that path. Pick this one when the bus log shows 2-byte seed +
        // 2-byte key against an E38.
        Register("gm-e38",
            () => new Gmw3110_2010_Generic(new E38Algorithm(),
                                           id: "gm-e38"));
        // GM algorithm number not yet documented for T43 - rename to gm-algo-NN
        // once a TCM utility-file `27 NN` byte sequence or vendor doc confirms it.
        // (Searched Interpreters_September_01_2009.docx and the 2011 DPS Programmers
        // Reference Manual: both define the op-code language, not per-controller
        // algorithm assignments. OpCodeAssessment.xls shows a TCM with algorithm
        // 0x84 but its part numbers don't match the T43 6T70 family, so 0x84
        // can't be confidently attributed to T43.)
        Register("gm-t43",
            () => new Gmw3110_2010_Generic(new T43Algorithm(),
                                           id: "gm-t43"));
        Register("gm-programming-bypass",
            () => new Gmw3110_2010_Generic(new Gmw3110ProgrammingBypassAlgorithm(),
                                           id: "gm-programming-bypass"));
        // Permissive variant for DPS Enhanced 5-byte utility files whose
        // algorithm password we haven't captured yet. Emits a real non-zero
        // 5-byte seed, accepts any key. Useful for new families on first
        // contact and for tests that want to bypass verification.
        Register("gm-permissive-5byte",
            () => new Gmw3110Permissive5Byte());
    }

    public static void Register(string id, Func<ISecurityAccessModule> factory)
    {
        if (string.IsNullOrWhiteSpace(id))
            throw new ArgumentException("Module id must be non-empty", nameof(id));
        Factories[id] = factory ?? throw new ArgumentNullException(nameof(factory));
    }

    /// <summary>Creates a new instance of the module with the given id, or null if id is null/unknown.</summary>
    public static ISecurityAccessModule? Create(string? id)
        => id is not null && Factories.TryGetValue(id, out var factory) ? factory() : null;

    /// <summary>All registered module IDs. Used to populate the editor's module picker.</summary>
    public static IReadOnlyCollection<string> KnownIds => Factories.Keys;
}
