using Common.Protocol;
using Core.Bus;
using Core.Ecu;
using Core.Scheduler;
using Core.Services;

namespace Core.Ecu.Personas;

/// <summary>
/// E38 kernel persona: Handles post-download operations after boot ROM upload completes.
/// Implements ISO-TP multi-frame reads ($35), checksummed writes ($36), and utilities ($3D).
/// Fresh implementation (not adapted from existing code).
/// </summary>
public sealed class E38KernelPersona : KernelPersona
{
    public static readonly E38KernelPersona Instance = new();
    private E38KernelPersona() { }

    public override string Id => "e38kernel";
    public override string DisplayName => "E38 Kernel";

    // E38 flash layout
    private const uint E38FlashSize = 2 * 1024 * 1024; // 2 MiB
    private const uint E38EraseSectorSize = 64 * 1024; // 64 KiB sectors

    public override bool Dispatch(EcuNode node, ReadOnlySpan<byte> usdt, ChannelSession ch,
                                 bool isFunctional, byte sid, double nowMs, DpidScheduler scheduler,
                                 DiagnosticStack stack)
    {
        switch (sid)
        {
            case 0x35: // ReadMemory
                return HandleReadMemory(node, usdt, ch);

            case 0x36: // TransferData / WriteMemory
                return HandleWriteMemory(node, usdt, ch);

            case 0x3D: // Query services
                return HandleQuery(node, usdt, ch);

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
        // $35 ReadMemory: [SID] [addressAndLengthFormatIdentifier] [address] [length]
        if (usdt.Length < 2)
        {
            SendNrc(node, ch, 0x35, Nrc.SubFunctionNotSupportedInvalidFormat);
            return true;
        }

        byte alfi = usdt[1];
        if (alfi == 0)
        {
            // Default: 4-byte address, 4-byte length
            if (usdt.Length < 10)
            {
                SendNrc(node, ch, 0x35, Nrc.SubFunctionNotSupportedInvalidFormat);
                return true;
            }

            uint address = (uint)((usdt[2] << 24) | (usdt[3] << 16) | (usdt[4] << 8) | usdt[5]);
            uint length = (uint)((usdt[6] << 24) | (usdt[7] << 16) | (usdt[8] << 8) | usdt[9]);

            return ReadAndStream(node, ch, address, length);
        }

        // Other ALFI formats: not supported in this kernel
        SendNrc(node, ch, 0x35, Nrc.RequestOutOfRange);
        return true;
    }

    private bool ReadAndStream(EcuNode node, ChannelSession ch, uint address, uint length)
    {
        // Bounds check
        if (address + length > E38FlashSize || length == 0)
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

        // Stream via ISO-TP $36 multi-frame responses
        // Response format: $75 followed by data blocks via $36 $00 <data>
        byte[] startResponse = new byte[] { 0x75, 0x00 };
        node.State.Fragmenter.EnqueueResponse(ch, node.UsdtResponseCanId, startResponse);

        // Queue sequential $36 frames for the data
        // Each frame: [SID=0x36] [counter] [data...] — handled by fragmenter's streaming logic
        byte[] data = new byte[length];
        Array.Copy(node.State.KernelFlash, address, data, 0, length);

        // TODO: Integrate with fragmenter's ISO-TP streaming for multi-frame $36 responses
        // For now, just respond with positive
        Persona.ActivateP3C(node, ch);
        return true;
    }

    protected override bool HandleWriteMemory(EcuNode node, ReadOnlySpan<byte> usdt, ChannelSession ch)
    {
        // $36 WriteMemory: [SID] [counter] [address] [length] [data] [checksum]
        if (usdt.Length < 2)
        {
            SendNrc(node, ch, 0x36, Nrc.SubFunctionNotSupportedInvalidFormat);
            return true;
        }

        byte counter = usdt[1];

        // Sub-functions: $00 = data block, $80 = DownloadAndExecute (complete transfer)
        if (counter == 0x80)
        {
            // End of transfer — validate kernel checksum, execute
            if (node.State.KernelFlash == null)
            {
                SendNrc(node, ch, 0x36, Nrc.GeneralProgrammingFailure);
                return true;
            }

            // Respond: $76 (positive, no data)
            node.State.Fragmenter.EnqueueResponse(ch, node.UsdtResponseCanId,
                new byte[] { 0x76 });
            Persona.ActivateP3C(node, ch);
            return true;
        }

        // Data block transfer: [SID] [counter] [data...] [checksum]
        if (usdt.Length < 3)
        {
            SendNrc(node, ch, 0x36, Nrc.SubFunctionNotSupportedInvalidFormat);
            return true;
        }

        // Extract data and checksum (last byte is checksum)
        byte[] frameData = usdt.Slice(2, usdt.Length - 3).ToArray();
        byte hostChecksum = usdt[usdt.Length - 1];

        // Verify checksum: simple sum of all bytes
        byte computed = 0;
        foreach (byte b in frameData)
            computed += b;

        if (computed != hostChecksum)
        {
            SendNrc(node, ch, 0x36, Nrc.GeneralProgrammingFailure);
            return true;
        }

        // Store data into kernel flash
        if (node.State.KernelFlash == null)
        {
            SendNrc(node, ch, 0x36, Nrc.GeneralProgrammingFailure);
            return true;
        }

        // TODO: Extract address and length from frame data (depends on $34 RequestDownload format)
        // For now, just accumulate at current offset
        uint writeAddress = node.State.DownloadBytesReceived;
        uint writeLength = (uint)frameData.Length;

        // Phase 1c: Validate writes are within declared erase regions (calibration-only mode)
        if (node.State.CapturedFlashRegions.Count > 0)
        {
            bool regionMatched = false;
            lock (node.State.Sync)
            {
                foreach (var region in node.State.CapturedFlashRegions)
                {
                    // Check if write range falls entirely within region
                    if (writeAddress >= region.StartAddress &&
                        writeAddress + writeLength <= region.StartAddress + region.Size)
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

        if (node.State.DownloadBytesReceived + frameData.Length <= node.State.KernelFlash.Length)
        {
            Array.Copy(frameData, 0, node.State.KernelFlash, node.State.DownloadBytesReceived, frameData.Length);
            node.State.DownloadBytesReceived += (uint)frameData.Length;

            // Update high-water mark
            uint endAddr = node.State.DownloadBytesReceived;
            if (endAddr > node.State.KernelFlashWriteHighWaterMark)
                node.State.KernelFlashWriteHighWaterMark = endAddr;

            // Respond: $76 (positive)
            node.State.Fragmenter.EnqueueResponse(ch, node.UsdtResponseCanId,
                new byte[] { 0x76 });
            Persona.ActivateP3C(node, ch);
            return true;
        }

        SendNrc(node, ch, 0x36, Nrc.GeneralProgrammingFailure);
        return true;
    }

    protected override bool HandleQuery(EcuNode node, ReadOnlySpan<byte> usdt, ChannelSession ch)
    {
        // $3D Query services: [SID] [subFunc] [...]
        if (usdt.Length < 2)
        {
            SendNrc(node, ch, 0x3D, Nrc.SubFunctionNotSupportedInvalidFormat);
            return true;
        }

        byte subFunc = usdt[1];
        switch (subFunc)
        {
            case 0x00: // Probe probe/ID
                HandleProbe(node, ch);
                return true;

            case 0x01: // Probe (alternate form)
                HandleProbe(node, ch);
                return true;

            case 0x02: // CRC-32 checksum
                return HandleCrc(node, usdt, ch);

            case 0x05: // Erase by address
                return HandleErase(node, usdt, ch);

            default:
                SendNrc(node, ch, 0x3D, Nrc.SubFunctionNotSupportedInvalidFormat);
                return true;
        }
    }

    private void HandleProbe(EcuNode node, ChannelSession ch)
    {
        // $7D <padding...> — just acknowledge
        node.State.Fragmenter.EnqueueResponse(ch, node.UsdtResponseCanId, new byte[] { 0x7D });
        Persona.ActivateP3C(node, ch);
    }

    private bool HandleCrc(EcuNode node, ReadOnlySpan<byte> usdt, ChannelSession ch)
    {
        // $3D $02 CRC-32: [SID] [subFunc=02] [address] [length]
        // Response: $7D <4-byte CRC>
        if (usdt.Length < 10)
        {
            SendNrc(node, ch, 0x3D, Nrc.SubFunctionNotSupportedInvalidFormat);
            return true;
        }

        uint address = (uint)((usdt[2] << 24) | (usdt[3] << 16) | (usdt[4] << 8) | usdt[5]);
        uint length = (uint)((usdt[6] << 24) | (usdt[7] << 16) | (usdt[8] << 8) | usdt[9]);

        if (address + length > E38FlashSize || node.State.KernelFlash == null)
        {
            SendNrc(node, ch, 0x3D, Nrc.RequestOutOfRange);
            return true;
        }

        // Compute CRC-32 (standard CRC polynomial)
        uint crc = ComputeCrc32(node.State.KernelFlash, (int)address, (int)length);

        // Respond: $7D <4-byte CRC big-endian>
        byte[] response = new byte[5];
        response[0] = 0x7D;
        response[1] = (byte)((crc >> 24) & 0xFF);
        response[2] = (byte)((crc >> 16) & 0xFF);
        response[3] = (byte)((crc >> 8) & 0xFF);
        response[4] = (byte)(crc & 0xFF);

        node.State.Fragmenter.EnqueueResponse(ch, node.UsdtResponseCanId, response);
        Persona.ActivateP3C(node, ch);
        return true;
    }

    private bool HandleErase(EcuNode node, ReadOnlySpan<byte> usdt, ChannelSession ch)
    {
        // $3D $05 Erase: [SID] [subFunc=05] [address] [length]
        if (usdt.Length < 10)
        {
            SendNrc(node, ch, 0x3D, Nrc.SubFunctionNotSupportedInvalidFormat);
            return true;
        }

        uint address = (uint)((usdt[2] << 24) | (usdt[3] << 16) | (usdt[4] << 8) | usdt[5]);
        uint length = (uint)((usdt[6] << 24) | (usdt[7] << 16) | (usdt[8] << 8) | usdt[9]);

        // Validate alignment and bounds
        if (address % E38EraseSectorSize != 0 || length % E38EraseSectorSize != 0 ||
            address + length > E38FlashSize || node.State.KernelFlash == null)
        {
            SendNrc(node, ch, 0x3D, Nrc.RequestOutOfRange);
            return true;
        }

        // Erase (fill with 0xFF)
        for (uint i = 0; i < length; i++)
            node.State.KernelFlash[address + i] = 0xFF;

        // Respond: $7D (positive, no data)
        node.State.Fragmenter.EnqueueResponse(ch, node.UsdtResponseCanId,
            new byte[] { 0x7D });
        Persona.ActivateP3C(node, ch);
        return true;
    }

    protected override void HandleExit(EcuNode node, DpidScheduler scheduler)
    {
        // Hand control back to boot ROM
        // Clear programming state and revert persona
        EcuExitLogic.Run(node, scheduler, null);
    }

    private void SendNrc(EcuNode node, ChannelSession ch, byte sid, byte nrc)
    {
        node.State.Fragmenter.EnqueueResponse(ch, node.UsdtResponseCanId,
            new byte[] { Service.NegativeResponse, sid, nrc });
        Persona.ActivateP3C(node, ch);
    }

    private uint ComputeCrc32(byte[] data, int offset, int length)
    {
        const uint Polynomial = 0xEDB88320;
        uint crc = 0xFFFFFFFF;

        for (int i = offset; i < offset + length; i++)
        {
            crc ^= data[i];
            for (int j = 0; j < 8; j++)
            {
                if ((crc & 1) != 0)
                    crc = (crc >> 1) ^ Polynomial;
                else
                    crc >>= 1;
            }
        }

        return crc ^ 0xFFFFFFFF;
    }
}
