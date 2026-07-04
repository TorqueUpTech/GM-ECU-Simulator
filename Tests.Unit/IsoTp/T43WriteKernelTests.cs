using Common.PassThru;
using Common.Protocol;
using Core.Bus;
using Core.Ecu;
using EcuSimulator.Tests.TestHelpers;
using Xunit;

namespace EcuSimulator.Tests.IsoTp;

// End-to-end sim-side coverage of the RAW (non-ISO-TP) 6Speed T43 WRITE kernel
// protocol handled by T43RawKernelBridge. Mirrors the Can-Display dash's
// T43WriteClient (pushSpsKernel + sendbin) frame-for-frame and asserts the exact
// single-frame acks it loops waiting for:
//   - every $34 RequestDownload -> $74            (Service34Handler, falls through)
//   - each kernel-part address FF -> 01 01 then 01 76 (addr ack + buffered post-stream ack)
//   - each segment-block descriptor -> 01 01
//   - the last CF of each block -> 01 76
// plus that the functional $20 finalize clears all T43 raw state.
//
// A wrong ack here is brick-class on real hardware (the dash treats ack-correct
// as write-complete; there is no CRC), so this locks the wire contract down.
public class T43WriteKernelTests
{
    private const ushort PhysReq  = NodeFactory.PhysReq;    // $7E0
    private const ushort UsdtResp = NodeFactory.UsdtResp;   // $7E8

    private readonly VirtualBus bus;
    private readonly EcuNode node;
    private readonly ChannelSession ch;

    public T43WriteKernelTests()
    {
        bus = new VirtualBus();
        node = NodeFactory.CreateNodeWithGenericModule();   // FakeSeedKeyAlgorithm: seed $1234, key $ABCD
        bus.AddNode(node);
        ch = new ChannelSession { Id = 1, Protocol = ProtocolID.CAN, Baud = 500_000, Bus = bus };
    }

    // ---- raw-frame wire helpers (the test is the host's TP stack) ----------

    private static byte[] Wrap(uint canId, params byte[] data)
    {
        var f = new byte[4 + data.Length];
        f[0] = (byte)(canId >> 24); f[1] = (byte)(canId >> 16);
        f[2] = (byte)(canId >> 8);  f[3] = (byte)canId;
        data.CopyTo(f, 4);
        return f;
    }

    private void Tx(uint canId, params byte[] data) => bus.DispatchHostTx(Wrap(canId, data), ch);

    private byte[] Rx()
    {
        Assert.True(ch.RxQueue.TryDequeue(out PassThruMsg? msg), "expected a response frame on RxQueue");
        return msg!.Data.AsSpan(4).ToArray();   // strip the 4-byte CAN id, return payload
    }

    private void AssertNoResponse() =>
        Assert.False(ch.RxQueue.TryDequeue(out _), "did not expect any response frame");

    // Single-frame USDT request: PCI 0x0N + payload. Used for the precondition
    // handshake and every $34 (the 6Speed tool sends those as single frames too).
    private void SendSf(params byte[] payload)
    {
        var sf = new byte[1 + payload.Length];
        sf[0] = (byte)(payload.Length & 0x0F);
        payload.CopyTo(sf, 1);
        Tx(PhysReq, sf);
    }

    // Send an SF request, expect a single-frame response, return its payload.
    private byte[] SendSfRecv(params byte[] payload)
    {
        SendSf(payload);
        var resp = Rx();
        Assert.Equal(0x00, resp[0] & 0xF0);                 // SF PCI
        int n = resp[0] & 0x0F;
        return resp.AsSpan(1, n).ToArray();
    }

    private void DriveProgrammingPreconditions()
    {
        Assert.Equal(new byte[] { 0x50, 0x02 }, SendSfRecv(0x10, 0x02));   // InitiateDiagnosticOperation
        Assert.Equal(new byte[] { 0x68 },       SendSfRecv(0x28));         // DisableNormalCommunication
        Assert.Equal(new byte[] { 0xE5 },       SendSfRecv(0xA5, 0x01));   // requestProgrammingMode
        SendSf(0xA5, 0x03); AssertNoResponse();                            // enableProgrammingMode (no resp)
        Assert.True(node.State.ProgrammingModeActive);
        Assert.Equal(new byte[] { 0x67, 0x01, 0x12, 0x34 }, SendSfRecv(0x27, 0x01)); // requestSeed
        Assert.Equal(new byte[] { 0x67, 0x02 }, SendSfRecv(0x27, 0x02, 0xAB, 0xCD)); // sendKey
        Assert.True(node.State.SecurityUnlockedLevel > 0);
    }

    // A kernel-part address FF acks "01" (addr xact) then a buffered "76" the tool
    // reads after streaming; the raw kernel stream that follows is swallowed silently.
    private void PushKernelPart(byte b0, byte b1, byte a6, byte a7, int rawStreamFrames)
    {
        Tx(PhysReq, b0, b1, 0x36, 0x00, 0x00, 0x3F, a6, a7);
        Assert.Equal(new byte[] { 0x01, 0x01 }, Rx());
        Assert.Equal(new byte[] { 0x01, 0x76 }, Rx());
        Assert.True(node.State.T43WriteActive);
        Assert.True(node.State.T43WriteKernelSwallow);

        for (int i = 0; i < rawStreamFrames; i++)
        {
            Tx(PhysReq, 0xAA, 0x55, (byte)i, 0x11, 0x22, 0x33, 0x44, 0x55);   // arbitrary, not "04 34"
            AssertNoResponse();
            Assert.True(node.State.T43WriteKernelSwallow);
        }
    }

    // One segment write block: descriptor acks "01", then blockLen bytes stream as
    // 0x21..0x2F CFs (7 data each, last zero-padded); the last CF acks "0176".
    private void WriteBlock(int blockLen)
    {
        byte b  = (byte)(((blockLen >> 8) & 0x0F) + 0x10);
        byte b2 = (byte)((blockLen & 0xFF) + 6);
        Tx(PhysReq, b, b2, 0x36, 0x00, 0x00, 0x3F, 0xC0, 0x00);
        Assert.Equal(new byte[] { 0x01, 0x01 }, Rx());
        Assert.Equal(blockLen, node.State.T43WriteBlockRemaining);

        byte seq = 0x21;
        int expectedCfs = (blockLen + 6) / 7;   // ceil(blockLen / 7)
        for (int j = 0, cf = 1; j < blockLen; j += 7, cf++)
        {
            Tx(PhysReq, seq, 0xDE, 0xAD, 0xBE, 0xEF, 0xCA, 0xFE, 0xBA);
            if (cf < expectedCfs)
            {
                AssertNoResponse();                         // block not yet complete
                Assert.True(node.State.T43WriteBlockRemaining > 0);
            }
            else
            {
                Assert.Equal(new byte[] { 0x01, 0x76 }, Rx());   // completion on the last CF
                Assert.Equal(0, node.State.T43WriteBlockRemaining);
            }
            if (++seq > 0x2F) seq = 0x20;
        }
    }

    [Fact]
    public void Full_T43_write_sequence_acks_every_stage_and_finalize_resets_state()
    {
        DriveProgrammingPreconditions();

        // ---- pushSpsKernel: two $34/addr-FF/stream parts ----
        Assert.Equal(new byte[] { 0x74 }, SendSfRecv(0x34, 0x00, 0x00, 0x0C, 0x20)); // part1 $34
        PushKernelPart(0x1C, 0x26, 0xAF, 0xE0, rawStreamFrames: 6);                  // 1C 26..3F AF E0

        Assert.Equal(new byte[] { 0x74 }, SendSfRecv(0x34, 0x00, 0x04, 0x00));       // part2 $34 (ends swallow)
        Assert.False(node.State.T43WriteKernelSwallow);
        PushKernelPart(0x14, 0x06, 0xBC, 0x00, rawStreamFrames: 4);                  // 14 06..3F BC 00

        // ---- sendbin: one segment $34 then multiple blocks ----
        Assert.Equal(new byte[] { 0x74 }, SendSfRecv(0x34, 0x00, 0x0F, 0xF6));       // segment $34 (ends swallow)
        Assert.False(node.State.T43WriteKernelSwallow);
        WriteBlock(4080);   // full block: exercises the exact 583-CF count boundary
        WriteBlock(14);     // clean multiple of 7
        WriteBlock(100);    // partial final CF (zero-padded)

        // ---- finalize: functional 101 FE 01 20 -> $20 resets everything ----
        Tx(GmlanCanId.AllNodesRequest, GmlanCanId.AllNodesExtAddr, 0x01, 0x20);
        AssertNoResponse();   // concluding a programming event is silent on the wire
        Assert.False(node.State.T43WriteActive);
        Assert.False(node.State.T43WriteKernelSwallow);
        Assert.Equal(0, node.State.T43WriteBlockRemaining);
        Assert.False(node.State.ProgrammingModeActive);
    }

    [Fact]
    public void Genuine_multiframe_36_write_is_not_shadowed_by_the_write_bridge()
    {
        // Guard the collision the bootloader-capture test first caught: a real
        // ISO-TP $36 to 0x3FAFE0 has FF "10 0E 36 00 00 3F AF E0", which shares
        // $36 + address bytes with the T43 write part-1 FF ("1C 26 .."). Only the
        // declared-length prefix differs, so the bridge must let this fall through
        // to the normal reassembler/$36 capture path (no write session opened).
        DriveProgrammingPreconditions();
        Assert.Equal(new byte[] { 0x74 }, SendSfRecv(0x34, 0x00, 0x00, 0x0C, 0x20));

        // FF of a 14-byte $36 (36 + fmt + 4 addr bytes = 6 in the FF, then 8 data
        // bytes over two CFs: 7 + 1). The bridge must NOT swallow any of it.
        Tx(PhysReq, 0x10, 0x0E, 0x36, 0x00, 0x00, 0x3F, 0xAF, 0xE0);
        var fc = Rx();
        Assert.Equal(0x30, fc[0] & 0xF0);                       // ISO-TP FlowControl, NOT a bridge "01 01"
        Assert.False(node.State.T43WriteActive);
        Tx(PhysReq, 0x21, 0xDE, 0xAD, 0xBE, 0xEF, 0xCA, 0xFE, 0xBA);   // CF1: 7 data bytes
        AssertNoResponse();                                    // reassembly not complete yet
        Tx(PhysReq, 0x22, 0xBE);                               // CF2: last data byte
        Assert.Equal(new byte[] { 0x01, 0x76 }, Rx());         // real $36 positive response (SF 01 76)
        Assert.Equal(0xDE, node.State.DownloadBuffer![0]);     // data captured, not swallowed
        Assert.Equal(8u, node.State.DownloadBytesReceived);
    }
}
