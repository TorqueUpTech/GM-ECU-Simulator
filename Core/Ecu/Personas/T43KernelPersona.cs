using Common.Protocol;
using Core.Bus;
using Core.Scheduler;
using Core.Services;

namespace Core.Ecu.Personas;

// Persona presented by the 6Speed.T43 READ kernel once it is running on a GM T43
// (Aisin 6-speed) TCM -- i.e. after the raw kernel-upload handshake completes
// (VirtualBus.DispatchHostTx counts the swallowed kernel stream down, emits 0x99,
// and swaps this in). Reset to Gmw3110Persona by EcuExitLogic on $20 / P3C timeout.
//
// Unlike the E38 (PcmHammer, clean ISO-TP), the 6Speed T43 protocol is RAW-framed:
//
//   $35 [len(3 BE)] [addr(3 BE)]  ->  $75 ack (single frame), then a First-Frame
//     "marker" the tool discards, then the <len> data bytes streamed as raw
//     Consecutive Frames (2N + up to 7 data bytes each). It is NOT ISO-TP: the
//     6Speed tool strips one PCI byte per frame and reassembles exactly <len>
//     bytes, ignoring the FF's payload. So the response is sent as raw frames via
//     VirtualBus.EnqueueRawFrame, not through the ISO-TP fragmenter.
//   $20  ->  hand control back to Gmw3110 (Service20Handler / EcuExitLogic).
//
// Read data streams from NodeState.KernelFlash, seeded from the ECU's loaded bin
// (blank $FF if none), exactly like PcmHammerKernelPersona. Block size is 0x800
// (2048), the 6Speed read granularity; the address space is the 2 MiB T43 flash.
public sealed class T43KernelPersona : IDiagnosticPersona
{
    public static readonly T43KernelPersona Instance = new();
    private T43KernelPersona() { }

    public string Id => "t43-read-kernel";
    public string DisplayName => "6Speed T43 read kernel";

    private const byte KernelRead = 0x35;          // $35 kernel memory read
    private const byte MemoryReadAck = 0x75;       // $75 ack to $35, before the data stream
    private const int  FlashSize = 0x200000;       // 2 MiB address space

    public bool Dispatch(EcuNode node, ReadOnlySpan<byte> usdt, ChannelSession ch,
                        bool isFunctional, byte sid, double nowMs, DpidScheduler scheduler,
                        DiagnosticStack stack)
    {
        _ = nowMs; _ = stack;
        // A running kernel is custom RAM code that ignores stock GMW3110 P3C -- reset
        // it on every kernel request so a long multi-block read (which sends $35, not
        // $3E) doesn't trip the P3Cnom timeout and revert us to Gmw3110 mid-read.
        node.State.TesterPresent.Reset();
        if (isFunctional) return true;   // kernel answers only physically-addressed requests

        switch (sid)
        {
            case Service.TesterPresent:
                Service3EHandler.Handle(node, usdt, ch, isFunctional);
                return true;
            case Service.ReturnToNormalMode:
                node.State.T43UploadRemaining = 0;
                Service20Handler.Handle(node, usdt, ch, scheduler);
                return true;
            case KernelRead:
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
            var seed = node.KernelFlashSeed;   // ECU's loaded bin -> real image instead of $FF
            if (seed is { Length: > 0 })
                seed.AsSpan(0, Math.Min(seed.Length, FlashSize)).CopyTo(f);
            node.State.KernelFlash = f;
        }
        return node.State.KernelFlash;
    }

    // $35 [len(3)] [addr(3)] -> $75 ack, a discarded First-Frame marker, then <len>
    // bytes as raw Consecutive Frames (2N + up to 7 data bytes). All raw, because the
    // 6Speed read framing is not ISO-TP (see the class comment).
    private static void HandleMemoryRead(EcuNode node, ReadOnlySpan<byte> usdt, ChannelSession ch)
    {
        if (usdt.Length < 7) return;   // malformed -> silent; the tool retries the block
        int len  = (usdt[1] << 16) | (usdt[2] << 8) | usdt[3];
        int addr = (usdt[4] << 16) | (usdt[5] << 8) | usdt[6];
        if (len <= 0 || (long)addr + len > FlashSize) return;

        byte[] flash = Flash(node);
        uint respId = node.UsdtResponseCanId;

        VirtualBus.EnqueueRawFrame(ch, respId, [0x01, MemoryReadAck]);                       // $75 ack
        VirtualBus.EnqueueRawFrame(ch, respId, [0x10, 0x00, 0x00, 0x00, 0x00, 0x00, 0x00, 0x00]); // FF marker (discarded)

        Span<byte> cf = stackalloc byte[8];
        int off = 0;
        byte seq = 1;
        while (off < len)
        {
            int n = Math.Min(7, len - off);
            cf[0] = (byte)(0x20 | (seq & 0x0F));                         // CF PCI (advisory; tool ignores it)
            flash.AsSpan(addr + off, n).CopyTo(cf.Slice(1));
            VirtualBus.EnqueueRawFrame(ch, respId, cf.Slice(0, 1 + n));
            off += n;
            seq++;
        }
    }
}
