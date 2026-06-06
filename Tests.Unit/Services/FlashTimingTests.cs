using System;
using System.Threading;
using Core.Bus;
using Core.Services;
using EcuSimulator.Tests.TestHelpers;
using Xunit;

namespace EcuSimulator.Tests.Services;

// FlashTiming paces flash-write responses so a simulator can model a real ECU's
// 30 s+ flash time. delay=0 must be byte-identical to an immediate enqueue (the
// default every existing flow relies on); delay>0 defers the positive response
// (both $36 transfer and erase) with no ResponsePending frames.
public sealed class FlashTimingTests
{
    private static (Core.Ecu.EcuNode node, ChannelSession ch) Make(int xfer = 0, int erase = 0)
    {
        var node = NodeFactory.CreateNode();
        node.FlashTransferDelayMs = xfer;
        node.FlashEraseDelayMs = erase;
        return (node, NodeFactory.CreateChannel());
    }

    private static byte[] PollUsdt(ChannelSession ch, int timeoutMs)
    {
        long deadline = Environment.TickCount64 + timeoutMs;
        while (ch.RxQueue.IsEmpty && Environment.TickCount64 < deadline)
            Thread.Sleep(10);
        Assert.False(ch.RxQueue.IsEmpty, "timed out waiting for the delayed response");
        return TestFrame.DequeueSingleFrameUsdt(ch);
    }

    [Fact]
    public void Transfer_DelayZero_EnqueuesImmediately()
    {
        var (node, ch) = Make(xfer: 0);
        FlashTiming.EnqueueTransferResponse(node, ch, new byte[] { 0x76, 0x00 });
        Assert.Equal(new byte[] { 0x76, 0x00 }, TestFrame.DequeueSingleFrameUsdt(ch));
    }

    [Fact]
    public void Transfer_Delay_DefersThenDelivers()
    {
        var (node, ch) = Make(xfer: 150);
        FlashTiming.EnqueueTransferResponse(node, ch, new byte[] { 0x76, 0x00 });

        Assert.True(ch.RxQueue.IsEmpty, "response should be deferred, not immediate");
        Assert.Equal(new byte[] { 0x76, 0x00 }, PollUsdt(ch, 2000));
    }

    [Fact]
    public void Erase_DelayZero_EnqueuesPositiveImmediately()
    {
        var (node, ch) = Make(erase: 0);
        FlashTiming.EnqueueEraseResponse(node, ch, new byte[] { 0xF1, 0x00, 0xB2, 0xAA });
        Assert.Equal(new byte[] { 0xF1, 0x00, 0xB2, 0xAA }, TestFrame.DequeueSingleFrameUsdt(ch));
    }

    [Fact]
    public void Erase_Delay_DefersPositiveWithoutResponsePending()
    {
        var (node, ch) = Make(erase: 150);
        FlashTiming.EnqueueEraseResponse(node, ch, new byte[] { 0xF1, 0x00, 0xB2, 0xAA });

        // No ResponsePending: nothing on the wire until the erase "completes".
        // (PCMTec aborts on a $7F B1 78 pending response to $B1.)
        Assert.True(ch.RxQueue.IsEmpty, "erase response should be deferred, not pending");
        Assert.Equal(new byte[] { 0xF1, 0x00, 0xB2, 0xAA }, PollUsdt(ch, 2000));
    }
}
