using Common.Protocol;
using Core.Bus;
using Core.Ecu;
using Core.Scheduler;
using Core.Services;

namespace Core.Ecu.Personas;

/// <summary>
/// BootRomPersona: Bootloader state machine for GM ECUs.
/// Enforces 7-state machine and security gating for $27, $28, $A5, $34, $36, $31, $20.
/// Fresh implementation (not adapted from existing personas).
///
/// State machine:
///   IDLE → SECURITY_PENDING_SEED → SECURITY_UNLOCKED → PROGRAMMING_MODE_REQUESTED
///       → PROGRAMMING_MODE_ACTIVE → DOWNLOAD_ACTIVE → (kernel runs) → IDLE
///
/// Each service has explicit precondition checks before processing.
/// </summary>
public sealed class BootRomPersona : IDiagnosticPersona
{
    public static readonly BootRomPersona Instance = new();
    private BootRomPersona() { }

    public string Id => "bootrom";
    public string DisplayName => "BootROM";

    public bool Dispatch(EcuNode node, ReadOnlySpan<byte> usdt, ChannelSession ch,
                        bool isFunctional, byte sid, double nowMs, DpidScheduler scheduler,
                        DiagnosticStack stack)
    {
        switch (sid)
        {
            case Service.SecurityAccess:
                if (isFunctional) return true;
                return HandleSecurityAccess(node, usdt, ch, nowMs);

            case Service.DisableNormalCommunication:
                if (!CanDisableNormalComm(node)) return SendNrc(node, ch, sid, Nrc.SecurityAccessDenied);
                if (Service28Handler.Handle(node, usdt, ch, isFunctional))
                    Persona.ActivateP3C(node, ch);
                return true;

            case Service.ProgrammingMode:
                if (isFunctional) return true;
                return HandleProgrammingMode(node, usdt, ch, nowMs);

            case Service.RequestDownload:
                if (isFunctional) return true;
                return HandleRequestDownload(node, usdt, ch, nowMs);

            case Service.TransferData:
                if (isFunctional) return true;
                return HandleTransferData(node, usdt, ch, nowMs);

            case 0x31: // RoutineControl (ISO-14229, not GMW3110)
                if (isFunctional) return true;
                return HandleRoutineControl(node, usdt, ch, nowMs);

            case Service.ReturnToNormalMode:
                EcuExitLogic.Run(node, scheduler, null);
                return true;

            case Service.TesterPresent:
                Service3EHandler.Handle(node, usdt, ch, isFunctional);
                return true;

            // Unsupported during boot ROM operation
            default:
                return SendNrc(node, ch, sid, Nrc.ServiceNotSupported);
        }
    }

    private bool HandleSecurityAccess(EcuNode node, ReadOnlySpan<byte> usdt, ChannelSession ch, double nowMs)
    {
        // Precondition: Must be in IDLE or requesting higher unlock level
        if (usdt.Length < 2) return SendNrc(node, ch, Service.SecurityAccess, Nrc.SubFunctionNotSupportedInvalidFormat);

        byte subFunc = usdt[1];
        if (subFunc != 0x01 && subFunc != 0x02)
            return SendNrc(node, ch, Service.SecurityAccess, Nrc.SubFunctionNotSupportedInvalidFormat);

        // Delegate to fresh $27 handler
        if (Service27Handler.Handle(node, usdt, ch, (long)nowMs))
            Persona.ActivateP3C(node, ch);
        return true;
    }

    private bool HandleProgrammingMode(EcuNode node, ReadOnlySpan<byte> usdt, ChannelSession ch, double nowMs)
    {
        if (usdt.Length < 2) return SendNrc(node, ch, Service.ProgrammingMode, Nrc.SubFunctionNotSupportedInvalidFormat);

        byte subFunc = usdt[1];
        switch (subFunc)
        {
            case 0x01: // requestProgrammingMode
            case 0x02: // requestProgrammingMode_HighSpeed
                if (node.State.SecurityUnlockedLevel == 0)
                    return SendNrc(node, ch, Service.ProgrammingMode, Nrc.SecurityAccessDenied);
                lock (node.State.Sync)
                {
                    node.State.ProgrammingModeRequested = true;
                    node.State.ProgrammingHighSpeed = (subFunc == 0x02);
                }
                node.State.Fragmenter.EnqueueResponse(ch, node.UsdtResponseCanId,
                    new[] { Service.Positive(Service.ProgrammingMode) });
                Persona.ActivateP3C(node, ch);
                return true;

            case 0x03: // enableProgrammingMode
                if (!node.State.ProgrammingModeRequested || node.State.SecurityUnlockedLevel == 0)
                    return SendNrc(node, ch, Service.ProgrammingMode, Nrc.ConditionsNotCorrectOrSequenceError);
                lock (node.State.Sync)
                {
                    node.State.ProgrammingModeActive = true;
                }
                node.State.Fragmenter.EnqueueResponse(ch, node.UsdtResponseCanId,
                    new[] { Service.Positive(Service.ProgrammingMode) });
                Persona.ActivateP3C(node, ch);
                return true;

            default:
                return SendNrc(node, ch, Service.ProgrammingMode, Nrc.SubFunctionNotSupportedInvalidFormat);
        }
    }

    private bool HandleRequestDownload(EcuNode node, ReadOnlySpan<byte> usdt, ChannelSession ch, double nowMs)
    {
        // Precondition: SecurityUnlockedLevel > 0 AND ProgrammingModeActive
        if (node.State.SecurityUnlockedLevel == 0)
            return SendNrc(node, ch, Service.RequestDownload, Nrc.SecurityAccessDenied);

        if (!node.State.ProgrammingModeActive)
            return SendNrc(node, ch, Service.RequestDownload, Nrc.ConditionsNotCorrectOrSequenceError);

        if (node.State.DownloadActive)
            return SendNrc(node, ch, Service.RequestDownload, Nrc.ConditionsNotCorrectOrSequenceError);

        // Delegate to Service34Handler
        if (Service34Handler.Handle(node, usdt, ch))
            Persona.ActivateP3C(node, ch);
        return true;
    }

    private bool HandleTransferData(EcuNode node, ReadOnlySpan<byte> usdt, ChannelSession ch, double nowMs)
    {
        // Precondition: DownloadActive must be true
        if (!node.State.DownloadActive)
            return SendNrc(node, ch, Service.TransferData, Nrc.ConditionsNotCorrectOrSequenceError);

        if (node.State.SecurityUnlockedLevel == 0)
            return SendNrc(node, ch, Service.TransferData, Nrc.SecurityAccessDenied);

        // Delegate to Service36Handler
        if (Service36Handler.Handle(node, usdt, ch))
            Persona.ActivateP3C(node, ch);
        return true;
    }

    private bool HandleRoutineControl(EcuNode node, ReadOnlySpan<byte> usdt, ChannelSession ch, double nowMs)
    {
        // Precondition: $31 requires SecurityUnlockedLevel > 0 and ProgrammingModeActive
        if (node.State.SecurityUnlockedLevel == 0)
            return SendNrc(node, ch, 0x31, Nrc.SecurityAccessDenied);

        if (!node.State.ProgrammingModeActive)
            return SendNrc(node, ch, 0x31, Nrc.ConditionsNotCorrectOrSequenceError);

        // $31 RoutineControl: erase memory by address (calibration-only flash support)
        if (BootRomService31Handler.Handle(node, usdt, ch))
            Persona.ActivateP3C(node, ch);
        return true;
    }

    private bool CanDisableNormalComm(EcuNode node) => node.State.SecurityUnlockedLevel > 0;

    private bool SendNrc(EcuNode node, ChannelSession ch, byte sid, byte nrc)
    {
        node.State.Fragmenter.EnqueueResponse(ch, node.UsdtResponseCanId,
            new[] { Service.NegativeResponse, sid, nrc });
        Persona.ActivateP3C(node, ch);
        return true;
    }
}
