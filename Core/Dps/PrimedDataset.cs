using Core.Identification;

namespace Core.Dps;

// The full parse + scan result the primer hands to the EcuNode builder.
// Held in memory by the application once a Prime succeeds, so the
// diagnostic dialog and bus-log overlay can reach back into it.
//
// Phase3 is the manifest of every Mode $1A and $22 read DPS will perform
// during Phase 3 (post-flash configuration). EditedPhase3 carries the
// user's overrides from the wizard's review page; null in headless
// (auto-load) paths, where the initial Phase3 is applied as-is.
public sealed record PrimedDataset(
    PrimeReport Report,
    UtilityFile UtilityFile,
    ExpectedValueTable ExpectedValues,
    ExpectedRequestLog ExpectedRequests,
    Mode1ADidBinExtractor.BinIdentification? BinIdentification,
    IReadOnlyList<E38PidRecord> KnownPids,
    PidResponseSolver.SolverResult SolverResult,
    byte[] OsCalBytes,                   // virtual-flash seed for future CRC handlers
    Phase3Manifest Phase3,               // extractor output (read-only baseline)
    Phase3Manifest? EditedPhase3);       // wizard's mutated manifest with user overrides
