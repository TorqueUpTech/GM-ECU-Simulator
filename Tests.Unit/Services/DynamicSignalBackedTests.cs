using Common.Protocol;
using Common.Signals;
using Core.Ecu;
using Core.Scheduler;
using Core.Services;
using Core.Transport;
using EcuSimulator.Tests.TestHelpers;
using Xunit;

namespace EcuSimulator.Tests.Services;

// The dynamic/periodic family must serve live signal-backed values now that a Pid can be signal-backed: a $AA
// streamed DPID and a $2D address alias both route through Pid.WriteResponseBytes, so they pick up the engine signal
// rather than a stale waveform.
public sealed class DynamicSignalBackedTests
{
    private static Pid RpmPid(uint address) => new()
    {
        Mode = PidMode.Mode22,
        Address = address,
        Size = PidSize.Word,
        Scalar = 0.25,
        Signal = SignalId.EngineRpm,
    };

    [Fact]
    public void StreamedDpid_WithSignalBackedPid_EmitsLiveValue()
    {
        var node = NodeFactory.CreateNode();
        var pid = RpmPid(0x000C);
        node.AddPid(pid);
        var dpid = new Dpid { Id = 0xF0, Pids = new[] { pid } };

        var frame = DpidScheduler.BuildUudtFrame(node, dpid, timeMs: 0);

        // Frame is [id bytes][dpid][value...]; idle RPM 750 / 0.25 = 3000 = 0x0BB8.
        Assert.Equal((byte)0xF0, frame[CanFrame.IdBytes]);
        Assert.Equal(new byte[] { 0x0B, 0xB8 }, frame.AsSpan(CanFrame.IdBytes + 1).ToArray());
    }

    [Fact]
    public void Define2D_OfSignalBackedSource_AliasReadsLive()
    {
        var node = NodeFactory.CreateNode();
        node.AddPid(RpmPid(0x1234));   // signal-backed source at memory address 0x1234
        var ch = NodeFactory.CreateChannel();

        // $2D: define new PID 0xF010 mirroring the source at 0x1234, size 2.
        bool ok = Service2DHandler.Handle(node, new byte[] { 0x2D, 0xF0, 0x10, 0x12, 0x34, 0x02 }, ch);
        Assert.True(ok);
        Assert.Equal(new byte[] { 0x6D, 0xF0, 0x10 }, TestFrame.DequeueSingleFrameUsdt(ch));

        // Reading the alias via $22 returns the live engine RPM, inherited from the signal-backed source.
        var ch2 = NodeFactory.CreateChannel();
        Service22Handler.Handle(node, new byte[] { 0x22, 0xF0, 0x10 }, ch2, timeMs: 0, isFunctional: false);
        Assert.Equal(new byte[] { 0x62, 0xF0, 0x10, 0x0B, 0xB8 }, TestFrame.DequeueSingleFrameUsdt(ch2));
    }
}
