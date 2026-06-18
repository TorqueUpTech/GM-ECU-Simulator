using Common.Protocol;
using Core.Bus;
using Core.Services;

namespace Core.Ecu.Personas;

// Stack-neutral diagnostic services shared by every persona.
//
// VirtualBus.DispatchUsdt consults this AFTER the active persona declines a
// SID (returns false) and BEFORE emitting NRC $11. A service whose wire format
// and semantics are identical across the GMW3110 and UDS stacks lives here
// once, instead of being re-listed in - or silently omitted from, then NRC'd
// by - each persona's own switch.
//
// $22 is the canonical case. GMW3110 calls it ReadDataByParameterIdentifier
// and UDS calls it ReadDataByIdentifier, but the bytes are the same: request
// `22 <DID hi> <DID lo> ...`, response `62 <DID> <data> ...`, sourced from the
// ECU's configured PID/DID set by the persona-agnostic Service22Handler. There
// is nothing Ford- or kernel-specific about reading a DID, so every persona
// inherits it from here rather than each re-implementing it.
//
// Gmw3110Persona keeps its OWN $22 case on purpose: it additionally applies
// the E38/E67 real-silicon stack gate (RequireUdsStack - $22 is UDS-stack-only
// on the surveyed bins). Because the persona handles $22 itself it never falls
// through to this layer, so that fidelity is preserved. The kernel and Ford
// personas have no such gate and pick $22 up here.
public static class CommonServices
{
    /// <summary>
    /// Handle a stack-neutral service the active persona declined. Returns true
    /// if a response (positive or a spec-correct NRC) was enqueued, false if
    /// the SID isn't a common service (caller then NRCs $11 for physical).
    /// </summary>
    public static bool TryHandle(EcuNode node, ReadOnlySpan<byte> usdt, ChannelSession ch,
                                 bool isFunctional, byte sid, double nowMs)
    {
        switch (sid)
        {
            case Service.ReadDataByParameterIdentifier:   // $22 - same on both stacks
                Service22Handler.Handle(node, usdt, ch, nowMs, isFunctional);
                return true;
            default:
                return false;
        }
    }
}
