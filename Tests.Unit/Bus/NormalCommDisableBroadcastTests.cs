using Common.Dbc;
using Common.PassThru;
using Common.Protocol;
using Core.Bus;
using Core.Ecu;
using Core.Services;
using EcuSimulator.Tests.TestHelpers;
using Xunit;

namespace EcuSimulator.Tests.Bus;

// $28 DisableNormalCommunication halts an ECU's autonomous CAN broadcast; $20 ReturnToNormalMode (and the
// Reset ECU State menu, and a P3C timeout - all of which funnel through EcuExitLogic) brings it back. These
// tests drive the real BroadcastScheduler/TimerOnDelay against a fake broadcaster, the same shape as
// BroadcastSchedulerTests, and exercise the handler + exit logic that flip NodeState.NormalCommunicationDisabled.
public sealed class NormalCommDisableBroadcastTests
{
    private sealed class FakeBroadcaster : IFrameBroadcaster
    {
        public List<byte[]> Frames { get; } = new();
        public void BroadcastFrame(byte[] frame) { lock (Frames) Frames.Add((byte[])frame.Clone()); }
        public int Count { get { lock (Frames) return Frames.Count; } }
        public void Clear() { lock (Frames) Frames.Clear(); }
    }

    private static EcuNode NodeWithConstantRpmBroadcast(uint canId, int periodMs)
    {
        var node = NodeFactory.CreateNode();
        var msg = new BroadcastMessage { CanId = canId, Dlc = 8, PeriodMs = periodMs, Enabled = true };
        msg.Signals.Add(new BroadcastSignal
        {
            Name = "ENGINE_SPEED",
            StartBit = 7, Length = 16, ByteOrder = DbcByteOrder.Motorola,
            Scale = 1.0,
            ValueSource = BroadcastValueSource.Constant,
            Constant = 3200,
        });
        node.AddBroadcast(msg);
        return node;
    }

    private static (VirtualBus bus, EcuNode node, ChannelSession ch, FakeBroadcaster fake) Setup(int periodMs = 25)
    {
        var bus = new VirtualBus();
        var fake = new FakeBroadcaster();
        bus.Broadcaster = fake;
        var node = NodeWithConstantRpmBroadcast(0x0C9, periodMs);
        bus.AddNode(node);
        var ch = new ChannelSession { Id = 1, Protocol = ProtocolID.CAN, Baud = 500_000, Bus = bus };
        return (bus, node, ch, fake);
    }

    [Fact]
    public void RebuildAndStart_SkipsNodeWithNormalCommunicationDisabled()
    {
        var (bus, node, _, fake) = Setup();
        node.State.NormalCommunicationDisabled = true;     // as if $28 had already landed before the session started

        bus.BroadcastScheduler.RebuildAndStart();
        Thread.Sleep(100);
        bus.BroadcastScheduler.StopAll();

        Assert.Equal(0, fake.Count);
    }

    [Fact]
    public void Service28_StopsBroadcast_ThenExitLogicResumesIt()
    {
        var (bus, node, ch, fake) = Setup();

        // Host session up: the node is broadcasting.
        bus.BroadcastScheduler.RebuildAndStart();
        Thread.Sleep(80);
        Assert.True(fake.Count > 0, "node should be broadcasting before $28");

        // $28 DisableNormalCommunication: positive ack + autonomous TX stops.
        bool positive = Service28Handler.Handle(node, new byte[] { Service.DisableNormalCommunication }, ch, isFunctional: false);
        Assert.True(positive);
        Assert.True(node.State.NormalCommunicationDisabled);

        fake.Clear();
        Thread.Sleep(80);
        Assert.Equal(0, fake.Count);                       // broadcast halted while normal comm is disabled

        // $20 / Reset ECU State / P3C timeout all run EcuExitLogic, which clears the flag and resumes the broadcast.
        EcuExitLogic.Run(node, bus.Scheduler, ch);
        Assert.False(node.State.NormalCommunicationDisabled);

        fake.Clear();
        Thread.Sleep(80);
        Assert.True(fake.Count > 0, "broadcast should resume after normal communication is restored");

        bus.BroadcastScheduler.StopAll();
    }
}
