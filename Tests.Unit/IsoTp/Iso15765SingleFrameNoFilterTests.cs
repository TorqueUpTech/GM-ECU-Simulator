using Common.PassThru;
using Common.Wire;
using Core.Bus;
using Shim.Ipc;
using Xunit;

namespace EcuSimulator.Tests.IsoTp;

// Regression for the flash-write abort: a single-frame ISO15765 write to a CAN
// ID with no flow-control filter must succeed. PCMTec fires a functional
// TesterPresent `3E 80` at $7DF between $36 blocks during a flash write; $7DF has
// no FC filter (you don't get segmented responses on the functional broadcast
// ID), and the dispatcher used to reject every filterless ISO15765 write with
// ERR_NO_FLOW_CONTROL, killing the flash. A single frame needs no FC handshake;
// only a genuine multi-frame write still requires a filter.
public class Iso15765SingleFrameNoFilterTests
{
    private static (uint channelId, RequestDispatcher dispatcher) OpenChannel()
    {
        var bus = new VirtualBus();
        var state = new IpcSessionState(bus);
        var dispatcher = new RequestDispatcher(state);

        var connect = new IpcWriter();
        connect.WriteU32(0);                       // deviceId (unused)
        connect.WriteU32((uint)ProtocolID.ISO15765);
        connect.WriteU32(0);                       // flags
        connect.WriteU32(500_000);                 // baud
        var (_, resp) = dispatcher.Dispatch(IpcMessageTypes.ConnectRequest, connect.AsSpan());
        var rr = new IpcReader(resp);
        Assert.Equal((uint)ResultCode.STATUS_NOERROR, rr.ReadU32());
        return (rr.ReadU32(), dispatcher);
    }

    private static ResultCode Write(RequestDispatcher d, uint channelId, byte[] frameData)
    {
        var w = new IpcWriter();
        w.WriteU32(channelId);
        w.WriteU32(1);                             // numMsgs
        w.WriteU32(1000);                          // timeoutMs
        w.WritePassThruMsg(new PassThruMsg
        {
            ProtocolID = ProtocolID.ISO15765,
            TxFlags = TxFlag.ISO15765_FRAME_PAD,
            Data = frameData,
        });
        var (rt, resp) = d.Dispatch(IpcMessageTypes.WriteMsgsRequest, w.AsSpan());
        Assert.Equal(IpcMessageTypes.WriteMsgsResponse, rt);
        return (ResultCode)new IpcReader(resp).ReadU32();
    }

    [Fact]
    public void FunctionalSingleFrame_NoFilter_Succeeds()
    {
        var (channelId, dispatcher) = OpenChannel();
        // 3E 80 to functional $7DF, no flow-control filter registered.
        byte[] frame = { 0x00, 0x00, 0x07, 0xDF, 0x3E, 0x80 };
        Assert.Equal(ResultCode.STATUS_NOERROR, Write(dispatcher, channelId, frame));
    }

    [Fact]
    public void MultiFramePayload_NoFilter_StillFailsNoFlowControl()
    {
        var (channelId, dispatcher) = OpenChannel();
        // 8 user bytes (> the 7-byte single-frame max) genuinely needs flow
        // control, so a filterless multi-frame write must still fail.
        byte[] frame = { 0x00, 0x00, 0x07, 0xDF, 0x36, 0x01, 0x02, 0x03, 0x04, 0x05, 0x06, 0x07 };
        Assert.Equal(ResultCode.ERR_NO_FLOW_CONTROL, Write(dispatcher, channelId, frame));
    }
}
