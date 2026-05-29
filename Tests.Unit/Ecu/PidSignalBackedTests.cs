using Common.Protocol;
using Common.Signals;
using Core.Ecu;
using Core.Services;
using EcuSimulator.Tests.TestHelpers;
using Xunit;

namespace EcuSimulator.Tests.Ecu;

// Covers the signal-backed Pid path: a PID with Signal set draws its value from the owning ECU's EngineModel and
// encodes with its own Scalar/Offset (so $22 can carry GM A2L scaling over a signal that $01 carries via J1979).
public sealed class PidSignalBackedTests
{
    // A $22 PID reading engine RPM with a 0.25 scalar (mirrors the J1979 quarter-rpm scaling for an easy check).
    private static Pid RpmPid() => new()
    {
        Mode = PidMode.Mode22,
        Address = 0x000C,
        Size = PidSize.Word,
        DataType = PidDataType.Unsigned,
        Scalar = 0.25,
        Signal = SignalId.EngineRpm,
    };

    [Fact]
    public void SignalBacked_ServesLiveIdleValue()
    {
        var node = NodeFactory.CreateNode();
        var pid = RpmPid();
        node.AddPid(pid);   // attaches the engine model

        var buf = new byte[2];
        pid.WriteResponseBytes(timeMs: 0, buf);

        // Idle RPM 750, scalar 0.25 -> raw 3000 = 0x0BB8.
        Assert.Equal(new byte[] { 0x0B, 0xB8 }, buf);
    }

    [Fact]
    public void SignalBacked_ReflectsOverride()
    {
        var node = NodeFactory.CreateNode();
        var pid = RpmPid();
        node.AddPid(pid);

        node.EngineModel.SetOverride(SignalId.EngineRpm, 4000);
        var buf = new byte[2];
        pid.WriteResponseBytes(timeMs: 0, buf);

        // 4000 rpm, scalar 0.25 -> raw 16000 = 0x3E80.
        Assert.Equal(new byte[] { 0x3E, 0x80 }, buf);
    }

    [Fact]
    public void SignalBacked_TakesPrecedenceOverStaticBytes()
    {
        var node = NodeFactory.CreateNode();
        var pid = RpmPid();
        pid.StaticBytes = new byte[] { 0xFF, 0xFF };   // would win for a non-signal PID
        node.AddPid(pid);

        var buf = new byte[2];
        pid.WriteResponseBytes(timeMs: 0, buf);

        Assert.Equal(new byte[] { 0x0B, 0xB8 }, buf);
    }

    [Fact]
    public void Unattached_SignalPid_FallsBackToStaticBytes()
    {
        // A signal-backed PID never added to a node has no engine to sample, so it degrades to its StaticBytes
        // rather than throwing. (In real use every PID is added to a node, which attaches the engine.)
        var pid = new Pid
        {
            Size = PidSize.Byte,
            Signal = SignalId.EngineRpm,
            StaticBytes = new byte[] { 0x12 },
        };

        var buf = new byte[1];
        pid.WriteResponseBytes(timeMs: 0, buf);

        Assert.Equal(new byte[] { 0x12 }, buf);
    }

    [Fact]
    public void Service22_ServesSignalBackedLiveValue()
    {
        var node = NodeFactory.CreateNode();
        node.AddPid(RpmPid());
        var ch = NodeFactory.CreateChannel();

        // $22 read of PID 0x000C now returns the live engine RPM, not a static placeholder.
        Service22Handler.Handle(node, new byte[] { 0x22, 0x00, 0x0C }, ch, timeMs: 0, isFunctional: false);

        Assert.Equal(new byte[] { 0x62, 0x00, 0x0C, 0x0B, 0xB8 }, TestFrame.DequeueSingleFrameUsdt(ch));
    }
}
