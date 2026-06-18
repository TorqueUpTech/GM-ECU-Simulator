using System.Threading;
using Common.PassThru;
using Core.Bus;
using Xunit;

namespace EcuSimulator.Tests.Bus;

// Host-bound frames must carry a J2534 PassThruMsg.Timestamp (microseconds) so a
// host (e.g. PCMTec's datalog) can plot a real time axis. It is stamped from one
// GLOBAL bus clock at the single delivery chokepoint ChannelSession.EnqueueRx -
// not per-persona - so every channel/ECU shares the same time base.
public sealed class FrameTimestampTests
{
    private static ChannelSession CanChannel(VirtualBus bus)
        => new() { Id = 1, Protocol = ProtocolID.CAN, Baud = 500_000, Bus = bus };

    private static PassThruMsg CanFrame(uint timestamp = 0)
        => new()
        {
            ProtocolID = ProtocolID.CAN,
            Timestamp = timestamp,
            Data = new byte[] { 0x00, 0x00, 0x06, 0xA0, 0x07, 0xE0, 0x01, 0x00 },
        };

    [Fact]
    public void EnqueueRx_StampsTimestamp_FromGlobalMicrosecondClock()
    {
        var bus = new VirtualBus();
        var ch = CanChannel(bus);
        Thread.Sleep(5);                       // let the global µs clock advance past 0

        ch.EnqueueRx(CanFrame());              // Timestamp left at 0

        Assert.True(ch.RxQueue.TryDequeue(out var got));
        Assert.True(got!.Timestamp > 0, "frame should be stamped from the global bus clock");
        Assert.True(got.Timestamp <= bus.NowMicros, "stamp must not exceed current bus time");
    }

    [Fact]
    public void Timestamp_IsMicroseconds_RoughlyTracksNowMs()
    {
        var bus = new VirtualBus();
        Thread.Sleep(20);
        // NowMicros must be ~1000x NowMs (µs vs ms), not equal to it.
        uint micros = bus.NowMicros;
        double ms = bus.NowMs;
        Assert.True(micros >= ms * 900, $"µs ({micros}) should be ~1000x ms ({ms})");
    }

    [Fact]
    public void EnqueueRx_PreservesCallerSuppliedTimestamp()
    {
        var bus = new VirtualBus();
        var ch = CanChannel(bus);

        ch.EnqueueRx(CanFrame(timestamp: 12345));

        Assert.True(ch.RxQueue.TryDequeue(out var got));
        Assert.Equal(12345u, got!.Timestamp);   // non-zero caller stamp wins, not overwritten
    }

    [Fact]
    public void SuccessiveFrames_HaveNonDecreasingTimestamps()
    {
        var bus = new VirtualBus();
        var ch = CanChannel(bus);

        ch.EnqueueRx(CanFrame());
        Thread.Sleep(3);
        ch.EnqueueRx(CanFrame());

        Assert.True(ch.RxQueue.TryDequeue(out var first));
        Assert.True(ch.RxQueue.TryDequeue(out var second));
        Assert.True(second!.Timestamp >= first!.Timestamp, "monotonic global clock");
    }
}
