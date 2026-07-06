using Common.Protocol;
using Core.Bus;
using Core.Ecu;
using Core.Scheduler;
using Core.Services;

namespace Core.Ecu.Personas;

/// <summary>
/// T42 kernel persona: Handles post-download operations for T42 (4-speed TCM).
/// Similar to T43KernelPersona (raw non-ISO-TP) but with dual parallel flash banks.
/// Fresh implementation (not adapted from existing code).
/// </summary>
public sealed class T42KernelPersona : KernelPersona
{
    public static readonly T42KernelPersona Instance = new();
    private T42KernelPersona() { }

    public override string Id => "t42kernel";
    public override string DisplayName => "T42 Kernel";

    // T42 flash layout: dual parallel banks
    private const uint T42FlashSize = 2 * 1024 * 1024; // 2 MiB per bank (or unified 2 MiB view)
    private const uint BankSize = 1 * 1024 * 1024;     // 1 MiB per bank

    public override bool Dispatch(EcuNode node, ReadOnlySpan<byte> usdt, ChannelSession ch,
                                 bool isFunctional, byte sid, double nowMs, DpidScheduler scheduler,
                                 DiagnosticStack stack)
    {
        switch (sid)
        {
            case 0x35: // ReadMemory (raw stream, non-ISO-TP)
                return HandleReadMemory(node, usdt, ch);

            case 0x36: // WriteMemory (raw segment)
                return HandleWriteMemory(node, usdt, ch);

            case 0x20: // ReturnToNormalMode - exit kernel
                HandleExit(node, scheduler);
                return true;

            case 0x3E: // TesterPresent keepalive
                Service3EHandler.Handle(node, usdt, ch, isFunctional);
                return true;

            default:
                SendNrc(node, ch, sid, Nrc.ServiceNotSupported);
                return true;
        }
    }

    protected override bool HandleReadMemory(EcuNode node, ReadOnlySpan<byte> usdt, ChannelSession ch)
    {
        // $35 ReadMemory (raw, non-ISO-TP): [SID] [address4] [length4]
        if (usdt.Length < 9)
        {
            SendNrc(node, ch, 0x35, Nrc.SubFunctionNotSupportedInvalidFormat);
            return true;
        }

        uint address = (uint)((usdt[1] << 24) | (usdt[2] << 16) | (usdt[3] << 8) | usdt[4]);
        uint length = (uint)((usdt[5] << 24) | (usdt[6] << 16) | (usdt[7] << 8) | usdt[8]);

        // Bounds check
        if (address + length > T42FlashSize || length == 0)
        {
            SendNrc(node, ch, 0x35, Nrc.RequestOutOfRange);
            return true;
        }

        // Verify kernel flash is loaded
        if (node.State.KernelFlash == null || node.State.KernelFlash.Length < address + length)
        {
            SendNrc(node, ch, 0x35, Nrc.GeneralProgrammingFailure);
            return true;
        }

        // Raw stream: read data directly (no ISO-TP framing)
        byte[] data = new byte[length];
        Array.Copy(node.State.KernelFlash, address, data, 0, length);

        // Queue response header
        node.State.Fragmenter.EnqueueResponse(ch, node.UsdtResponseCanId, new byte[] { 0x75 });

        // TODO: Queue raw data chunks (fragmenter handles CAN framing, no ISO-TP PCI)
        Persona.ActivateP3C(node, ch);
        return true;
    }

    protected override bool HandleWriteMemory(EcuNode node, ReadOnlySpan<byte> usdt, ChannelSession ch)
    {
        // $36 WriteMemory (raw segment): [SID] [segmentNumber] [address4] [length4] [data...] [checksum]
        if (usdt.Length < 11)
        {
            SendNrc(node, ch, 0x36, Nrc.SubFunctionNotSupportedInvalidFormat);
            return true;
        }

        byte segmentNumber = usdt[1];
        uint address = (uint)((usdt[2] << 24) | (usdt[3] << 16) | (usdt[4] << 8) | usdt[5]);
        uint length = (uint)((usdt[6] << 24) | (usdt[7] << 16) | (usdt[8] << 8) | usdt[9]);

        // Extract data and checksum (last byte is checksum)
        if (usdt.Length < 11 + length + 1)
        {
            SendNrc(node, ch, 0x36, Nrc.SubFunctionNotSupportedInvalidFormat);
            return true;
        }

        byte[] frameData = usdt.Slice(10, (int)length).ToArray();
        byte hostChecksum = usdt[10 + (int)length];

        // Verify checksum: simple sum
        byte computed = 0;
        foreach (byte b in frameData)
            computed += b;

        if (computed != hostChecksum)
        {
            SendNrc(node, ch, 0x36, Nrc.GeneralProgrammingFailure);
            return true;
        }

        // Bounds check
        if (address + length > T42FlashSize || node.State.KernelFlash == null ||
            address + length > node.State.KernelFlash.Length)
        {
            SendNrc(node, ch, 0x36, Nrc.RequestOutOfRange);
            return true;
        }

        // Phase 1c: Validate writes are within declared erase regions (calibration-only mode)
        if (node.State.CapturedFlashRegions.Count > 0)
        {
            bool regionMatched = false;
            lock (node.State.Sync)
            {
                foreach (var region in node.State.CapturedFlashRegions)
                {
                    // Check if write range falls entirely within region
                    if (address >= region.StartAddress &&
                        address + length <= region.StartAddress + region.Size)
                    {
                        regionMatched = true;
                        break;
                    }
                }
            }
            if (!regionMatched)
            {
                SendNrc(node, ch, 0x36, Nrc.RequestOutOfRange);
                return true;
            }
        }

        // Store data at specified address (handles both banks transparently)
        Array.Copy(frameData, 0, node.State.KernelFlash, address, length);

        // Update high-water mark
        uint endAddr = address + length;
        if (endAddr > node.State.KernelFlashWriteHighWaterMark)
            node.State.KernelFlashWriteHighWaterMark = endAddr;

        // Respond: $76 <segment number> (acknowledge receipt)
        node.State.Fragmenter.EnqueueResponse(ch, node.UsdtResponseCanId,
            new byte[] { 0x76, segmentNumber });
        Persona.ActivateP3C(node, ch);
        return true;
    }

    protected override bool HandleQuery(EcuNode node, ReadOnlySpan<byte> usdt, ChannelSession ch)
    {
        // T42 kernel doesn't support $3D queries (like T43)
        SendNrc(node, ch, 0x3D, Nrc.ServiceNotSupported);
        return true;
    }

    protected override void HandleExit(EcuNode node, DpidScheduler scheduler)
    {
        // Hand control back to boot ROM
        EcuExitLogic.Run(node, scheduler, null);
    }

    private void SendNrc(EcuNode node, ChannelSession ch, byte sid, byte nrc)
    {
        node.State.Fragmenter.EnqueueResponse(ch, node.UsdtResponseCanId,
            new byte[] { Service.NegativeResponse, sid, nrc });
        Persona.ActivateP3C(node, ch);
    }
}
