namespace Core.Security;

// How a SecurityAccess module treats incoming $27 traffic. The choice is
// orthogonal to which seed/key cipher is configured - any cipher can be
// wrapped in either behaviour. Strict modules go through the full
// requestSeed -> compute key -> compare path; bypass modules short-circuit
// to a positive response on every step.
//
// Previously this lived on ISeedKeyAlgorithm (and was confusingly named
// "ProgrammingSessionBehavior"). Moving it onto the wrapping module decouples
// cipher identity from session policy: the same cipher class is reusable in
// both a strict registry entry (e.g. gm-e38-2byte) and a bypass test
// fixture without a second algorithm class.
public enum SecurityModuleBehaviour
{
    /// <summary>
    /// Default. Full GMW3110 SecurityAccess flow runs every time:
    /// requestSeed -> cipher.ComputeExpectedKey -> byte-compare -> NRC $35
    /// on mismatch, with the 3-strike lockout / $36 / $37 self-healing.
    /// </summary>
    Strict = 0,

    /// <summary>
    /// Every $27 step short-circuits to a positive response. requestSeed
    /// returns an all-zero seed AND marks the level unlocked (DPS / CCRT
    /// convention - "ECU is already unlocked, skip sendKey"); a later
    /// sendKey is accepted regardless of contents. Used for tester-side
    /// convenience modules (gm-bypass-2byte, gm-bypass-5byte) and for
    /// modelling stub-security ECUs.
    /// </summary>
    BypassAll,
}
