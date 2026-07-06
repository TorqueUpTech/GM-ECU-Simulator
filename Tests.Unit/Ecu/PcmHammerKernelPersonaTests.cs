using Core.Ecu.Personas;
using EcuSimulator.Tests.TestHelpers;
using Xunit;

namespace EcuSimulator.Tests.Ecu;

// The PcmHammer/PCMHacking flash-kernel persona: probe / erase / write / CRC-32
// verify. The CRC round-trip (write bytes, then ask the kernel for the CRC over
// the same range) is exactly what the display's E38 write does to verify a flash,
// so a passing round-trip here means a passing live validation.
public sealed class PcmHammerKernelPersonaTests
{
    private const byte Sid3D = 0x3D, Sid36 = 0x36;

    private static bool Dispatch(Core.Ecu.EcuNode node, Core.Bus.ChannelSession ch, byte[] req)
        => PcmHammerKernelPersona.Instance.Dispatch(
            node, req, ch, isFunctional: false, sid: req[0], nowMs: 0, scheduler: null!, stack: default);

    // Matches Gmlan.ComputeCrc32 / the display: poly 0x04C11DB7, init 0, MSB-first.
    private static uint Crc32(byte[] d, int off, int len)
    {
        uint r = 0;
        for (int i = 0; i < len; i++)
        {
            r ^= (uint)d[off + i] << 24;
            for (int b = 0; b < 8; b++)
                r = (r & 0x80000000u) != 0 ? (r << 1) ^ 0x04C11DB7u : (r << 1);
        }
        return r;
    }

    [Fact]
    public void Probe_ReturnsKernelVersion()
    {
        var node = NodeFactory.CreateNode();
        var ch = NodeFactory.CreateChannel();

        Assert.True(Dispatch(node, ch, new byte[] { Sid3D, 0x00 }));
        Assert.Equal(new byte[] { 0x7D, 0x00, 0x00, 0x00, 0x00, 0x01 },
                     TestFrame.DequeueSingleFrameUsdt(ch));
    }

    [Fact]
    public void EraseWriteVerify_RoundTrips()
    {
        var node = NodeFactory.CreateNode();
        var ch = NodeFactory.CreateChannel();
        uint addr = 0x020000;   // non-const so the byte-casts truncate at runtime
        byte[] data = { 0xAA, 0xBB, 0xCC, 0xDD, 0x11, 0x22, 0x33, 0x44 };

        // erase the sector: $3D $05 <addr3> -> $7D $05 $00
        Assert.True(Dispatch(node, ch, new byte[] { Sid3D, 0x05, 0x02, 0x00, 0x00 }));
        Assert.Equal(new byte[] { 0x7D, 0x05, 0x00 }, TestFrame.DequeueSingleFrameUsdt(ch));

        // write block: $36 $00 <len2> <addr3> <data> <sum2> -> $76
        ushort sum = 0; foreach (var b in data) sum += b;
        var write = new byte[7 + data.Length + 2];
        write[0] = Sid36; write[1] = 0x00;
        write[2] = (byte)(data.Length >> 8); write[3] = (byte)data.Length;
        write[4] = (byte)(addr >> 16); write[5] = (byte)(addr >> 8); write[6] = (byte)addr;
        data.CopyTo(write, 7);
        write[7 + data.Length] = (byte)(sum >> 8); write[7 + data.Length + 1] = (byte)sum;
        Assert.True(Dispatch(node, ch, write));
        Assert.Equal(new byte[] { 0x76 }, TestFrame.DequeueSingleFrameUsdt(ch));

        // the bytes actually landed in the kernel flash image
        Assert.NotNull(node.State.KernelFlash);
        Assert.Equal(data, node.State.KernelFlash!.AsSpan((int)addr, data.Length).ToArray());

        // CRC-32 verify: $3D $02 <size3> <addr3> -> $7D $02 <crc4> == host CRC
        uint expect = Crc32(node.State.KernelFlash!, (int)addr, data.Length);
        var crcReq = new byte[]
        {
            Sid3D, 0x02,
            (byte)(data.Length >> 16), (byte)(data.Length >> 8), (byte)data.Length,
            (byte)(addr >> 16), (byte)(addr >> 8), (byte)addr,
        };
        Assert.True(Dispatch(node, ch, crcReq));
        Assert.Equal(new byte[]
        {
            0x7D, 0x02,
            (byte)(expect >> 24), (byte)(expect >> 16), (byte)(expect >> 8), (byte)expect,
        }, TestFrame.DequeueSingleFrameUsdt(ch));
    }

    [Fact]
    public void WriteWithBadSum_IsRejected()
    {
        var node = NodeFactory.CreateNode();
        var ch = NodeFactory.CreateChannel();
        // 36 00 len=1 addr=0x020000 data=0x55 sum=0x0000 (wrong; should be 0x0055)
        var bad = new byte[] { Sid36, 0x00, 0x00, 0x01, 0x02, 0x00, 0x00, 0x55, 0x00, 0x00 };
        Assert.True(Dispatch(node, ch, bad));           // handled (an NRC was sent)
        var resp = TestFrame.DequeueSingleFrameUsdt(ch);
        Assert.Equal(0x7F, resp[0]);                    // negative response
        Assert.Equal(0x36, resp[1]);
    }

    [Fact]
    public void FlashId_Responds()
    {
        var node = NodeFactory.CreateNode();
        var ch = NodeFactory.CreateChannel();
        // $3D $01 -> $7D $01 <id4> = AMD AM29BL162C (0x00012203, 256 KiB main-array blocks).
        Assert.True(Dispatch(node, ch, new byte[] { Sid3D, 0x01 }));
        Assert.Equal(new byte[] { 0x7D, 0x01, 0x00, 0x01, 0x22, 0x03 },
                     TestFrame.DequeueSingleFrameUsdt(ch));
    }

    [Fact]
    public void Read_ReturnsSeededFlash()
    {
        var node = NodeFactory.CreateNode();
        var ch = NodeFactory.CreateChannel();

        // Seed the kernel flash exactly as ConfigStore would from FlashBinPath.
        var seed = new byte[0x200000];
        seed.AsSpan().Fill(0xFF);
        seed[0x000010] = 0x12;
        seed[0x000011] = 0x34;
        node.KernelFlashSeed = seed;

        // $35 <len3=0x000002> <addr3=0x000010> -> $75 ack, then $36 00 <addr3> <data2>
        Assert.True(Dispatch(node, ch, new byte[] { 0x35, 0x00, 0x00, 0x02, 0x00, 0x00, 0x10 }));
        Assert.Equal(new byte[] { 0x75 }, TestFrame.DequeueSingleFrameUsdt(ch));
        Assert.Equal(new byte[] { 0x36, 0x00, 0x00, 0x00, 0x10, 0x12, 0x34 },
                     TestFrame.DequeueSingleFrameUsdt(ch));
        TestFrame.AssertEmpty(ch);
    }

    [Fact]
    public void KernelRead_ResetsP3C_SoLongReadsDoNotRevert()
    {
        var node = NodeFactory.CreateNode();
        var ch = NodeFactory.CreateChannel();
        // Programming session: P3C active with the timer nearly at the 5 s P3Cnom.
        node.State.TesterPresent.Activate();
        node.State.TesterPresent.TimerMs = 4900;

        // A $35 kernel read must reset P3C, else a long read reverts the kernel at ~5 s.
        Assert.True(Dispatch(node, ch, new byte[] { 0x35, 0x00, 0x00, 0x02, 0x00, 0x00, 0x10 }));
        Assert.Equal(0, node.State.TesterPresent.TimerMs);
    }

    [Fact]
    public void WriteThenRead_RoundTrips()
    {
        var node = NodeFactory.CreateNode();
        var ch = NodeFactory.CreateChannel();
        uint addr = 0x020000;
        byte[] data = { 0xDE, 0xAD };

        // write: $36 $00 <len2> <addr3> <data> <sum2> -> $76
        ushort sum = (ushort)(data[0] + data[1]);
        var write = new byte[]
        {
            Sid36, 0x00, 0x00, 0x02,
            (byte)(addr >> 16), (byte)(addr >> 8), (byte)addr,
            data[0], data[1], (byte)(sum >> 8), (byte)sum,
        };
        Assert.True(Dispatch(node, ch, write));
        Assert.Equal(new byte[] { 0x76 }, TestFrame.DequeueSingleFrameUsdt(ch));

        // read it back: $35 <len3=2> <addr3> -> $75 ack, then $36 00 <addr3> <data2>
        Assert.True(Dispatch(node, ch, new byte[]
            { 0x35, 0x00, 0x00, 0x02, (byte)(addr >> 16), (byte)(addr >> 8), (byte)addr }));
        Assert.Equal(new byte[] { 0x75 }, TestFrame.DequeueSingleFrameUsdt(ch));
        Assert.Equal(new byte[] { 0x36, 0x00, (byte)(addr >> 16), (byte)(addr >> 8), (byte)addr, data[0], data[1] },
                     TestFrame.DequeueSingleFrameUsdt(ch));
    }

    [Fact]
    public void FlashWrite_TracksHighWaterMark_SoTheFlashedImageIsSaved()
    {
        // Regression: HandleFlashWrite used to copy into KernelFlash but never advance
        // KernelFlashWriteHighWaterMark. BootloaderCaptureWriter.WriteKernelFlash gates the
        // post-flash image dump on HWM > 0, so a flash "saved" only the seed (the ECU
        // editor's read bin), not what was flashed. HWM must now reflect the write extent.
        var node = NodeFactory.CreateNode();
        var ch = NodeFactory.CreateChannel();
        Assert.Equal(0u, node.State.KernelFlashWriteHighWaterMark);

        uint addr = 0x03D000;
        byte[] data = { 0x01, 0x02, 0x03, 0x04 };
        ushort sum = 0; foreach (var b in data) sum += b;
        var write = new byte[7 + data.Length + 2];
        write[0] = Sid36; write[1] = 0x00;
        write[2] = (byte)(data.Length >> 8); write[3] = (byte)data.Length;
        write[4] = (byte)(addr >> 16); write[5] = (byte)(addr >> 8); write[6] = (byte)addr;
        data.CopyTo(write, 7);
        write[7 + data.Length] = (byte)(sum >> 8); write[7 + data.Length + 1] = (byte)sum;
        Assert.True(Dispatch(node, ch, write));

        Assert.Equal(addr + (uint)data.Length, node.State.KernelFlashWriteHighWaterMark);

        // A test-write ($44, validate-only) must NOT program or advance the mark.
        uint hwm = node.State.KernelFlashWriteHighWaterMark;
        write[1] = 0x44; write[4] = (byte)((addr + 0x1000) >> 16); write[5] = (byte)((addr + 0x1000) >> 8); write[6] = (byte)(addr + 0x1000);
        Assert.True(Dispatch(node, ch, write));
        Assert.Equal(hwm, node.State.KernelFlashWriteHighWaterMark);
    }
}
