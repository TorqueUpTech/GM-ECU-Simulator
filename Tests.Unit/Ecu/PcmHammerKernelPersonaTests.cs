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
}
