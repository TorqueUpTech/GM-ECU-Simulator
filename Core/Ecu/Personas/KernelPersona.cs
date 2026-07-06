using Common.Protocol;
using Core.Bus;
using Core.Scheduler;

namespace Core.Ecu.Personas;

/// <summary>
/// Abstract base for kernel personas (E38, E40, T43, T42).
/// Defines the contract for handling kernel opcodes after boot ROM hands off control.
/// Fresh implementation (not adapted from existing code).
/// </summary>
public abstract class KernelPersona : IDiagnosticPersona
{
    public abstract string Id { get; }
    public abstract string DisplayName { get; }

    public abstract bool Dispatch(EcuNode node, ReadOnlySpan<byte> usdt, ChannelSession ch,
                                 bool isFunctional, byte sid, double nowMs,
                                 Core.Scheduler.DpidScheduler scheduler,
                                 DiagnosticStack stack);

    /// <summary>
    /// Handle $35 ReadMemory service.
    /// Returns true if response was queued.
    /// </summary>
    protected abstract bool HandleReadMemory(EcuNode node, ReadOnlySpan<byte> usdt, ChannelSession ch);

    /// <summary>
    /// Handle $36 TransferData / WriteMemory service.
    /// Returns true if response was queued.
    /// </summary>
    protected abstract bool HandleWriteMemory(EcuNode node, ReadOnlySpan<byte> usdt, ChannelSession ch);

    /// <summary>
    /// Handle $3D service queries (CRC, Erase, Probe).
    /// Returns true if response was queued.
    /// </summary>
    protected abstract bool HandleQuery(EcuNode node, ReadOnlySpan<byte> usdt, ChannelSession ch);

    /// <summary>
    /// Handle $20 ReturnToNormalMode from kernel.
    /// Hands control back to boot ROM.
    /// </summary>
    protected abstract void HandleExit(EcuNode node, Core.Scheduler.DpidScheduler scheduler);
}
