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
    private const int  FlashSize = 0x200000;       // 2 MiB address space
    private const uint EraseSectorSize = 0x10000;  // 64 KiB sectors

    public bool Dispatch(EcuNode node, ReadOnlySpan<byte> usdt, ChannelSession ch,
                        bool isFunctional, byte sid, double nowMs, DpidScheduler scheduler,
                        DiagnosticStack stack)
    {
        _ = nowMs; _ = stack;
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

            case 0x05:   // erase the 64 KiB sector containing addr: $3D $05 <addr3>
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
                data.CopyTo(flash.AsSpan((int)addr));
        }
        Respond(node, ch, [Service.Positive(Service.TransferData)]);   // $76
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
