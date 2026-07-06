using Common.Protocol;
using Core.Bus;
using Core.Scheduler;
using Core.Services;

namespace Core.Ecu.Personas;

// UDS persona presented by the PCMHacking.net / PcmHammer flash kernel once it is
// running on a GM E38 (and similar Gen-IV) ECM - i.e. after $36 sub $80
// DownloadAndExecute uploads a kernel whose image carries the "PCMHacking"
// banner (see Service36Handler's activation sniff).
//
// Unlike the generic UdsKernelPersona (which answers only $31/$3E/$20 and NRCs
// everything else), a real PcmHammer kernel exposes its OWN command set so a host
// can erase, write, and CRC-verify calibration. Transcribed from PcmHammer's
// Gmlan.cs (mode $3D queries + the kernel's $36 flash-write):
//
//   $3D $00                                  -> $7D $00 <ver4>     version / probe
//   $3D $02 <size3> <addr3>                  -> $7D $02 <crc4>     CRC-32 of a range
//   $3D $05 <addr3>                          -> $7D $05 $00        erase 64 KiB sector
//   $36 <ct> <len2> <addr3> <data..> <sum2>  -> $76                write block
//                                              ($7F $36 nrc on a bad sum)
//   $20                                      -> hand control back to the boot ROM
//
// $36 ct: $00 = program, $44 = test-write (validate the transfer, no program).
// The kernel operates on NodeState.KernelFlash (2 MiB, $FF on erase, copied on
// write, CRC read). Reset to Gmw3110Persona by EcuExitLogic on $20 / P3C timeout.
public sealed class PcmHammerKernelPersona : IDiagnosticPersona
{
    public static readonly PcmHammerKernelPersona Instance = new();
    private PcmHammerKernelPersona() { }

    public string Id => "pcmhammer-kernel";
    public string DisplayName => "PcmHammer flash kernel";

    private const byte KernelQuery = 0x3D;         // mode $3D (kernel query/erase)
    private const byte KernelRead = 0x35;          // mode $35 (kernel memory read)
    private const byte MemoryReadAck = 0x75;       // $75 ack to $35, before the $36 data block
    private const byte MemoryBlockResponse = 0x36; // $36 block carries read data (same byte as TransferData)
    private const int  FlashSize = 0x200000;       // 2 MiB address space
    // Real E38-class 2 MiB flash uses LARGE blocks -- PcmHammer's chip map (AM29BL162C, the
    // only 2 MiB entry in FlashChip.cs) is 256 KiB blocks across the main array. Modelling
    // 64 KiB here HID a brick-class flasher bug (cal segments 1..5 share the ONE 0x1C0000
    // block, so an interleaved erase-then-write wipes earlier segments). Erase the whole
    // 256 KiB block so the sim exercises that reality.
    private const uint EraseSectorSize = 0x40000;  // 256 KiB blocks (Gen-IV main array)

    public bool Dispatch(EcuNode node, ReadOnlySpan<byte> usdt, ChannelSession ch,
                        bool isFunctional, byte sid, double nowMs, DpidScheduler scheduler,
                        DiagnosticStack stack)
    {
        _ = nowMs; _ = stack;
        // A running flash kernel is custom RAM code that ignores stock GMW3110 P3C -
        // it answers until $20. Reset P3C on every kernel request so a long read/write
        // (which sends $35/$3D/$36, not $3E) doesn't trip the 5 s P3Cnom timeout and
        // revert us to Gmw3110Persona mid-operation (observed: a continuous $35 read
        // reverted at ~5 s / address 0x008000 -> $7F 35 11 -> the dash's ERR_READ).
        node.State.TesterPresent.Reset();
        if (isFunctional) return true;   // kernel answers only physically-addressed requests

        switch (sid)
        {
            case Service.TesterPresent:
                Service3EHandler.Handle(node, usdt, ch, isFunctional);
                return true;
            case Service.ReturnToNormalMode:
                Service20Handler.Handle(node, usdt, ch, scheduler);
                return true;
            case KernelQuery:
                HandleKernelQuery(node, usdt, ch);
                return true;
            case Service.TransferData:       // $36 kernel flash-write (not the boot-ROM form)
                HandleFlashWrite(node, usdt, ch);
                return true;
            case KernelRead:                 // $35 kernel memory read -> streams a $36 block back
                HandleMemoryRead(node, usdt, ch);
                return true;
            default:
                return false;                // NRC $11 ServiceNotSupported, like a real kernel
        }
    }

    private static byte[] Flash(EcuNode node)
    {
        if (node.State.KernelFlash is null || node.State.KernelFlash.Length < FlashSize)
        {
            var f = new byte[FlashSize];
            f.AsSpan().Fill(0xFF);
            // Seed from the ECU's loaded bin (FlashBinPath -> KernelFlashSeed) so a
            // real tool's READ returns a genuine image instead of blank $FF.
            // Truncated / $FF-padded to the 2 MiB address space.
            var seed = node.KernelFlashSeed;
            if (seed is { Length: > 0 })
                seed.AsSpan(0, Math.Min(seed.Length, FlashSize)).CopyTo(f);
            node.State.KernelFlash = f;
        }
        return node.State.KernelFlash;
    }

    private static void HandleKernelQuery(EcuNode node, ReadOnlySpan<byte> usdt, ChannelSession ch)
    {
        if (usdt.Length < 2) { SendNrc(node, ch, KernelQuery, Nrc.SubFunctionNotSupportedInvalidFormat); return; }
        switch (usdt[1])
        {
            case 0x00:   // version / probe
                Respond(node, ch, [Service.Positive(KernelQuery), 0x00, 0x00, 0x00, 0x00, 0x01]);
                return;

            case 0x01:   // flash chip id: $3D $01 -> $7D $01 <id4>. Report AMD AM29BL162C
                         // (0x00012203) -- PcmHammer's 2 MiB chip, 256 KiB main-array blocks --
                         // so a host that reads the id picks the right (large) erase block and
                         // its erase geometry matches this persona's 256 KiB EraseSectorSize.
                Respond(node, ch, [Service.Positive(KernelQuery), 0x01, 0x00, 0x01, 0x22, 0x03]);
                return;

            case 0x02:   // CRC-32: $3D $02 <size3> <addr3>  (size precedes address)
            {
                if (usdt.Length < 8) { SendNrc(node, ch, KernelQuery, Nrc.SubFunctionNotSupportedInvalidFormat); return; }
                uint size = (uint)((usdt[2] << 16) | (usdt[3] << 8) | usdt[4]);
                uint addr = (uint)((usdt[5] << 16) | (usdt[6] << 8) | usdt[7]);
                byte[] flash = Flash(node);
                uint crc = ((long)addr + size <= flash.Length) ? Crc32(flash, addr, size) : 0u;
                Respond(node, ch, [Service.Positive(KernelQuery), 0x02,
                    (byte)(crc >> 24), (byte)(crc >> 16), (byte)(crc >> 8), (byte)crc]);
                return;
            }

            case 0x05:   // erase the flash BLOCK (EraseSectorSize) containing addr: $3D $05 <addr3>
            {
                if (usdt.Length < 5) { SendNrc(node, ch, KernelQuery, Nrc.SubFunctionNotSupportedInvalidFormat); return; }
                uint addr = (uint)((usdt[2] << 16) | (usdt[3] << 8) | usdt[4]);
                byte[] flash = Flash(node);
                uint sector = addr & ~(EraseSectorSize - 1);
                if (sector < flash.Length)
                {
                    int end = (int)Math.Min(flash.Length, sector + EraseSectorSize);
                    flash.AsSpan((int)sector, end - (int)sector).Fill(0xFF);
                }
                Respond(node, ch, [Service.Positive(KernelQuery), 0x05, 0x00]);   // status $00 = ok
                return;
            }

            default:
                SendNrc(node, ch, KernelQuery, Nrc.SubFunctionNotSupportedInvalidFormat);
                return;
        }
    }

    private static void HandleFlashWrite(EcuNode node, ReadOnlySpan<byte> usdt, ChannelSession ch)
    {
        // $36 <ct> <len hi,lo> <addr hi,mid,lo> <data..> <sum hi,lo>
        if (usdt.Length < 7 + 2) { SendNrc(node, ch, Service.TransferData, Nrc.SubFunctionNotSupportedInvalidFormat); return; }
        byte ct = usdt[1];
        int len = (usdt[2] << 8) | usdt[3];
        uint addr = (uint)((usdt[4] << 16) | (usdt[5] << 8) | usdt[6]);
        if (usdt.Length != 7 + len + 2) { SendNrc(node, ch, Service.TransferData, Nrc.RequestOutOfRange); return; }

        var data = usdt.Slice(7, len);
        ushort sum = 0;
        for (int i = 0; i < len; i++) sum += data[i];
        ushort got = (ushort)((usdt[7 + len] << 8) | usdt[7 + len + 1]);
        if (sum != got) { SendNrc(node, ch, Service.TransferData, Nrc.GeneralProgrammingFailure); return; }

        if (ct == 0x00)   // $00 = program; $44 = test-write (validate only, no program)
        {
            byte[] flash = Flash(node);
            if ((long)addr + len <= flash.Length)
            {
                data.CopyTo(flash.AsSpan((int)addr));
                // Track the extent of real writes so BootloaderCaptureWriter.WriteKernelFlash
                // actually SAVES the flashed image at session end. Without this the high-water
                // mark stays 0 and only the seed (= the ECU editor's read bin) is written --
                // i.e. the flash appeared to "save the read file", not what was flashed.
                uint end = addr + (uint)len;
                if (end > node.State.KernelFlashWriteHighWaterMark)
                    node.State.KernelFlashWriteHighWaterMark = end;
            }
        }
        Respond(node, ch, [Service.Positive(Service.TransferData)]);   // $76
    }

    // $35 [len hi,mid,lo] [addr hi,mid,lo] -> a $75 ack, then a $36 data block:
    // $36 $00 [addr hi,mid,lo] [<len> data bytes]. PcmHammer's CanKernelReader
    // filters the $75 ack and reassembles the $36 block (Gmlan.ParseMemoryBlock:
    // header 36 00 addr3, then exactly <len> bytes). The read streams straight
    // from NodeState.KernelFlash, which Flash() seeds from the ECU's loaded bin.
    private static void HandleMemoryRead(EcuNode node, ReadOnlySpan<byte> usdt, ChannelSession ch)
    {
        if (usdt.Length < 7) { SendNrc(node, ch, KernelRead, Nrc.SubFunctionNotSupportedInvalidFormat); return; }
        int len = (usdt[1] << 16) | (usdt[2] << 8) | usdt[3];
        uint addr = (uint)((usdt[4] << 16) | (usdt[5] << 8) | usdt[6]);
        if (len <= 0 || (long)addr + len > FlashSize)
        {
            SendNrc(node, ch, KernelRead, Nrc.RequestOutOfRange);
            return;
        }

        byte[] flash = Flash(node);

        // The CAN-Display's E38 reader (readBlockE38) waits for the $75 ack first (2 s),
        // THEN the multi-frame $36 data block (3 s) - matching a real Antus kernel. Send
        // both: $75 ack, then the $36 block. The block's CFs are paced by the reader's
        // FlowControl STmin (honoured by IsoTpFragmenter), so a receiver that can't take
        // frames back-to-back must request a non-zero STmin in its FC.
        Respond(node, ch, [MemoryReadAck]);                 // $75 ack
        var block = new byte[5 + len];
        block[0] = MemoryBlockResponse;                     // $36
        block[1] = 0x00;                                    // sub-type (matches ParseMemoryBlock)
        block[2] = (byte)(addr >> 16);
        block[3] = (byte)(addr >> 8);
        block[4] = (byte)addr;
        flash.AsSpan((int)addr, len).CopyTo(block.AsSpan(5));
        Respond(node, ch, block);
    }

    private static void Respond(EcuNode node, ChannelSession ch, ReadOnlySpan<byte> payload)
        => node.State.Fragmenter.EnqueueResponse(ch, node.UsdtResponseCanId, payload);

    private static void SendNrc(EcuNode node, ChannelSession ch, byte sid, byte nrc)
        => node.State.Fragmenter.EnqueueResponse(ch, node.UsdtResponseCanId,
            [Service.NegativeResponse, sid, nrc]);

    // CRC-32: polynomial 0x04C11DB7, initial 0, MSB-first, no reflection, no final
    // XOR - matches PcmHammer Gmlan.ComputeCrc32 and the display's verify.
    private static uint Crc32(byte[] d, uint offset, uint len)
    {
        uint r = 0;
        for (uint i = 0; i < len; i++)
        {
            r ^= (uint)d[offset + i] << 24;
            for (int b = 0; b < 8; b++)
                r = (r & 0x80000000u) != 0 ? (r << 1) ^ 0x04C11DB7u : (r << 1);
        }
        return r;
    }
}
