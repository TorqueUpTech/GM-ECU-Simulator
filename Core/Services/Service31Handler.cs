using Common.Protocol;
using Core.Bus;
using Core.Ecu;
using Core.Ecu.Personas;

namespace Core.Services;

/// <summary>
/// BootRom RoutineControl ($31) handler: Erase memory by address.
/// Fresh implementation for bootloader erase region tracking.
/// Called during programming mode to define which flash regions will be erased.
/// Note: UDS kernels have their own Service31Handler in Services.Uds namespace.
/// </summary>
public static class BootRomService31Handler
{
    public static bool Handle(EcuNode node, ReadOnlySpan<byte> usdt, ChannelSession ch)
    {
        // $31 [routineIdentifier(2)] [subFunc] [address] [length]
        if (usdt.Length < 2)
        {
            SendNrc(node, ch, Nrc.SubFunctionNotSupportedInvalidFormat);
            return true;
        }

        // Routine identifier should be $FF00 (erase)
        byte routineHigh = usdt[1];
        if (usdt.Length < 3)
        {
            SendNrc(node, ch, Nrc.SubFunctionNotSupportedInvalidFormat);
            return true;
        }

        byte routineLow = usdt[2];
        ushort routineId = (ushort)((routineHigh << 8) | routineLow);

        if (routineId != 0xFF00)
        {
            SendNrc(node, ch, Nrc.SubFunctionNotSupportedInvalidFormat);
            return true;
        }

        // Sub-function: $00 = eraseMemoryByAddress, $01 = eraseCheckRoutine
        if (usdt.Length < 4)
        {
            SendNrc(node, ch, Nrc.SubFunctionNotSupportedInvalidFormat);
            return true;
        }

        byte subFunc = usdt[3];
        switch (subFunc)
        {
            case 0x00:
                return HandleEraseMemory(node, usdt, ch);
            case 0x01:
                return HandleEraseCheck(node, usdt, ch);
            default:
                SendNrc(node, ch, Nrc.SubFunctionNotSupportedInvalidFormat);
                return true;
        }
    }

    private static bool HandleEraseMemory(EcuNode node, ReadOnlySpan<byte> usdt, ChannelSession ch)
    {
        // $31 $FF $00 [ALFI] [address] [length]
        // ALFI = address and length format identifier (typically $22 = 2-byte addr, 2-byte len)
        if (usdt.Length < 5)
        {
            SendNrc(node, ch, Nrc.SubFunctionNotSupportedInvalidFormat);
            return true;
        }

        byte alfi = usdt[4];

        // Parse address and length based on ALFI
        uint address;
        uint length;

        if (alfi == 0x22)
        {
            // 2-byte address, 2-byte length
            if (usdt.Length < 9)
            {
                SendNrc(node, ch, Nrc.SubFunctionNotSupportedInvalidFormat);
                return true;
            }
            address = (uint)((usdt[5] << 8) | usdt[6]);
            length = (uint)((usdt[7] << 8) | usdt[8]);
        }
        else if (alfi == 0x44)
        {
            // 4-byte address, 4-byte length
            if (usdt.Length < 13)
            {
                SendNrc(node, ch, Nrc.SubFunctionNotSupportedInvalidFormat);
                return true;
            }
            address = (uint)((usdt[5] << 24) | (usdt[6] << 16) | (usdt[7] << 8) | usdt[8]);
            length = (uint)((usdt[9] << 24) | (usdt[10] << 16) | (usdt[11] << 8) | usdt[12]);
        }
        else
        {
            SendNrc(node, ch, Nrc.RequestOutOfRange);
            return true;
        }

        // Validate: address and length alignment (64 KiB sectors for E38)
        const uint SectorSize = 64 * 1024;
        if (address % SectorSize != 0 || length % SectorSize != 0 || length == 0)
        {
            SendNrc(node, ch, Nrc.RequestOutOfRange);
            return true;
        }

        // Prevent erasing boot ROM (first 256 KiB reserved)
        const uint BootRomSize = 256 * 1024;
        if (address < BootRomSize || (address + length) < address)
        {
            SendNrc(node, ch, Nrc.RequestOutOfRange);
            return true;
        }

        // Record erase region
        lock (node.State.Sync)
        {
            var region = new FlashEraseRegion(address, length);
            node.State.CapturedFlashRegions.Add(region);

            // Optionally erase the in-memory buffer
            if (node.State.KernelFlash != null && address + length <= node.State.KernelFlash.Length)
            {
                for (uint i = 0; i < length; i++)
                    node.State.KernelFlash[address + i] = 0xFF;
            }
        }

        // Respond: $71 (positive, no data)
        node.State.Fragmenter.EnqueueResponse(ch, node.UsdtResponseCanId,
            new byte[] { 0x71 });
        Persona.ActivateP3C(node, ch);
        return true;
    }

    private static bool HandleEraseCheck(EcuNode node, ReadOnlySpan<byte> usdt, ChannelSession ch)
    {
        // $31 $FF $01 — verify all previously declared erase regions are erased ($FF)
        // Response: $71 (success) or NRC $85 if any region not erased
        lock (node.State.Sync)
        {
            foreach (var region in node.State.CapturedFlashRegions)
            {
                if (node.State.KernelFlash == null || region.StartAddress + region.Size > node.State.KernelFlash.Length)
                {
                    SendNrc(node, ch, Nrc.GeneralProgrammingFailure);
                    return true;
                }

                // Check all bytes are $FF in the region
                for (uint i = 0; i < region.Size; i++)
                {
                    if (node.State.KernelFlash[region.StartAddress + i] != 0xFF)
                    {
                        SendNrc(node, ch, Nrc.GeneralProgrammingFailure);
                        return true;
                    }
                }
            }
        }

        // All regions verified erased
        node.State.Fragmenter.EnqueueResponse(ch, node.UsdtResponseCanId,
            new byte[] { 0x71 });
        Persona.ActivateP3C(node, ch);
        return true;
    }

    private static void SendNrc(EcuNode node, ChannelSession ch, byte nrc)
    {
        node.State.Fragmenter.EnqueueResponse(ch, node.UsdtResponseCanId,
            new byte[] { Service.NegativeResponse, 0x31, nrc });
        Persona.ActivateP3C(node, ch);
    }
}
