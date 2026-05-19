using Common.Protocol;
using Common.Waveforms;
using Core.Ecu;
using Core.Services;
using EcuSimulator.Tests.TestHelpers;
using Xunit;

namespace EcuSimulator.Tests.Services;

// SAE J1979 OBD-II Service $01 ShowCurrentData coverage. Lookup goes via
// EcuNode.Mode1Pids (the separate Mode 1 store), not the unified Pids list,
// so $22 and Service01 can't bleed into each other's namespace.
public sealed class Service01HandlerTests
{
    private static EcuNode NodeWithMode1Pid(byte pidId, byte fixedByte)
    {
        var node = NodeFactory.CreateNode();
        var pid = new Pid
        {
            Mode = PidMode.Mode1,
            Address = pidId,
            Size = PidSize.Byte,
            DataType = PidDataType.Unsigned,
            Scalar = 1.0,
            Offset = 0.0,
            WaveformConfig = new WaveformConfig { Shape = WaveformShape.Constant, Offset = fixedByte },
        };
        node.SetMode1Pid(pid);
        return node;
    }

    [Fact]
    public void SinglePid_Physical_ReturnsPositive41Response()
    {
        var node = NodeWithMode1Pid(0x0C, 0x42);
        var ch = NodeFactory.CreateChannel();

        Service01Handler.Handle(node, new byte[] { 0x01, 0x0C }, ch, timeMs: 0, isFunctional: false);

        Assert.Equal(new byte[] { 0x41, 0x0C, 0x42 }, TestFrame.DequeueSingleFrameUsdt(ch));
    }

    [Fact]
    public void MultiPid_PartiallySupported_DropsUnsupportedSilently()
    {
        // $0C supported, $99 not. J1979 follows the GMW3110 $22 convention -
        // unsupported entries silently omitted from the response.
        var node = NodeWithMode1Pid(0x0C, 0x07);
        var ch = NodeFactory.CreateChannel();

        Service01Handler.Handle(node, new byte[] { 0x01, 0x0C, 0x99 }, ch, timeMs: 0, isFunctional: false);

        Assert.Equal(new byte[] { 0x41, 0x0C, 0x07 }, TestFrame.DequeueSingleFrameUsdt(ch));
    }

    [Fact]
    public void NonePhysical_ReturnsNrc31()
    {
        var node = NodeFactory.CreateNode();
        var ch = NodeFactory.CreateChannel();

        Service01Handler.Handle(node, new byte[] { 0x01, 0xFF }, ch, timeMs: 0, isFunctional: false);

        Assert.Equal(new byte[] { Service.NegativeResponse, Service.Obd01ShowCurrentData, Nrc.RequestOutOfRange },
                     TestFrame.DequeueSingleFrameUsdt(ch));
    }

    [Fact]
    public void NoneFunctional_IsSilent()
    {
        var node = NodeFactory.CreateNode();
        var ch = NodeFactory.CreateChannel();

        Service01Handler.Handle(node, new byte[] { 0x01, 0xFF }, ch, timeMs: 0, isFunctional: true);

        TestFrame.AssertEmpty(ch);
    }

    [Fact]
    public void Mode1Lookup_DoesNotReachIntoUnifiedPidStore()
    {
        // Storage separation: a $22-style PID (Mode22, default) at address
        // 0x0C in the unified Pids list must not answer a $01 0x0C request.
        var node = NodeFactory.CreateNode();
        node.AddPid(new Pid
        {
            Mode = PidMode.Mode22,
            Address = 0x000C,
            Size = PidSize.Byte,
            WaveformConfig = new WaveformConfig { Shape = WaveformShape.Constant, Offset = 0x77 },
        });
        var ch = NodeFactory.CreateChannel();

        Service01Handler.Handle(node, new byte[] { 0x01, 0x0C }, ch, timeMs: 0, isFunctional: false);

        Assert.Equal(new byte[] { Service.NegativeResponse, Service.Obd01ShowCurrentData, Nrc.RequestOutOfRange },
                     TestFrame.DequeueSingleFrameUsdt(ch));
    }
}
