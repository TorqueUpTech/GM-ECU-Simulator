using System.Text;
using Common.IsoTp;
using Common.PassThru;
using Core.Bus;
using Core.Ecu;
using EcuSimulator.Tests.TestHelpers;
using Shim.IsoTp;
using Xunit;

namespace EcuSimulator.Tests.IsoTp;

// Regression coverage for the bus-log frame ordering on an ISO15765 channel.
//
// The simulator's bus is in-process and synchronous, so a multi-frame ECU
// response runs its whole FF -> FC -> CF cascade on one call stack: the
// fragmenter emits the First Frame, that synchronously drives the host's
// IsoChannel to answer with a FlowControl, which re-enters the fragmenter to
// emit the Consecutive Frames - all before the original EnqueueRx for the FF
// has returned.
//
// The bug this guards against: ChannelSession.EnqueueRx logged the frame
// AFTER handing it to the TP layer, so the FF's log line was written only
// once the re-entrant CFs had already logged themselves. A VIN read therefore
// showed the First Frame LAST in the bus log even though the wire order (and
// the reassembled payload the tester received) were correct. EnqueueRx now
// logs before dispatching, so log order matches wire order.
public class BusLogOrderingTests
{
    private const ushort PhysReq = NodeFactory.PhysReq;       // $7E0
    private const ushort UsdtResp = NodeFactory.UsdtResp;     // $7E8

    private static (VirtualBus bus, EcuNode node, Iso15765Channel iso, List<string> log) Setup()
    {
        var bus = new VirtualBus();
        var node = NodeFactory.CreateNode();
        bus.AddNode(node);

        var log = new List<string>();
        bus.LogFrame = (pretty, _, _) => log.Add(pretty);

        var ch = new ChannelSession
        {
            Id = 1,
            Protocol = ProtocolID.ISO15765,
            Baud = 500_000,
            Bus = bus,
        };
        var iso = new Iso15765Channel(new IsoTpTimingParameters());
        iso.BusEgress = frame => bus.DispatchHostTx(frame, ch);
        ch.IsoChannel = iso;
        ch.IsoChannelInbound = (canId, frame) => iso.OnInboundCanFrame(canId, frame.AsSpan(4));
        iso.AddFilter(new Iso15765Channel.IsoFilter
        {
            Id = 1,
            MaskCanId = 0xFFFFFFFF,
            PatternCanId = UsdtResp,
            FlowCtlCanId = PhysReq,
            Format = AddressFormat.Normal,
        });

        return (bus, node, iso, log);
    }

    // Pulls the PCI nibble out of a logged "[chan 1] Tx 7E8 10 13 .." line by
    // finding the CAN ID token and reading the byte that follows it.
    private static int PciTypeOf(string line, string canIdHex)
    {
        int idx = line.IndexOf(canIdHex + " ", StringComparison.Ordinal);
        Assert.True(idx >= 0, $"CAN ID {canIdHex} not found in: {line}");
        string firstByte = line.Substring(idx + canIdHex.Length + 1, 2);
        return Convert.ToInt32(firstByte, 16) & 0xF0;
    }

    [Fact]
    public void MultiFrame_VIN_response_logs_FirstFrame_before_ConsecutiveFrames()
    {
        var (_, node, iso, log) = Setup();

        // 17-char VIN at DID $90 -> 19-byte USDT response (5A 90 + 17) -> FF + 2 CFs.
        var vin = Encoding.ASCII.GetBytes("6G1ZS5ED6GR000001");
        node.SetIdentifier(0x90, vin);

        var begin = iso.BeginTransmit(PhysReq, new byte[] { 0x1A, 0x90 });
        Assert.True(begin.Started);
        iso.BusEgress!(begin.CanFrame!);   // drives the whole synchronous cascade
        iso.EndTransmit(begin.Filter!);

        // The tester still receives the correctly reassembled VIN - the bug was
        // log-only, never on the wire.
        Assert.True(iso.ReassembledPayloadQueue.TryDequeue(out var msg));
        Assert.Equal(vin, msg!.Data.AsSpan(2 + 4).ToArray());   // [4 CAN_ID][5A 90][VIN]

        // Walk the response-side ($7E8) frames in log order and pull their PCI
        // types. The First Frame ($1x) must appear before any Consecutive
        // Frame ($2x).
        var respPciTypes = log
            .Where(l => l.Contains(" Tx ") && l.Contains(" 7E8 "))
            .Select(l => PciTypeOf(l, "7E8"))
            .ToList();

        int ffIndex = respPciTypes.IndexOf(0x10);
        int firstCfIndex = respPciTypes.IndexOf(0x20);

        Assert.True(ffIndex >= 0, "no First Frame logged on the response ID");
        Assert.True(firstCfIndex >= 0, "no Consecutive Frame logged on the response ID");
        Assert.True(ffIndex < firstCfIndex,
            $"First Frame must log before Consecutive Frames; got FF at {ffIndex}, first CF at {firstCfIndex}. " +
            $"Full log:\n{string.Join("\n", log)}");
    }

    [Fact]
    public void SingleFrame_response_still_logs_in_order()
    {
        var (_, node, iso, log) = Setup();

        // DID $B0 -> single-byte diag-address override -> 3-byte SF response.
        var begin = iso.BeginTransmit(PhysReq, new byte[] { 0x1A, 0xB0 });
        Assert.True(begin.Started);
        iso.BusEgress!(begin.CanFrame!);
        iso.EndTransmit(begin.Filter!);

        Assert.True(iso.ReassembledPayloadQueue.TryDequeue(out _));

        // Request logged before response: "Rx 7E0 .." precedes "Tx 7E8 ..".
        int reqIndex = log.FindIndex(l => l.Contains(" Rx ") && l.Contains(" 7E0 "));
        int respIndex = log.FindIndex(l => l.Contains(" Tx ") && l.Contains(" 7E8 "));
        Assert.True(reqIndex >= 0 && respIndex >= 0);
        Assert.True(reqIndex < respIndex,
            $"request should log before response. Full log:\n{string.Join("\n", log)}");
    }
}
